using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

var dllPath = args.Length > 0 ? args[0] : null;
if (dllPath == null) { Console.WriteLine("Usage: pdxpatcher <PDX.SDK.dll> [--patch]"); return 1; }

bool dryRun = args.Length < 2 || args[1] != "--patch";
Console.WriteLine($"Mode: {(dryRun ? "DRY RUN" : "PATCHING")}\nLoading: {dllPath}\n");

var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters { ReadingMode = ReadingMode.Immediate });

var diskIO = module.Types.FirstOrDefault(t => t.Name == "DiskIODefaultWindows");
if (diskIO == null) { Console.WriteLine("ERROR: DiskIODefaultWindows not found!"); return 1; }
Console.WriteLine($"Found: {diskIO.FullName}\n");

int totalPatched = 0;

// Resolve IOException from game's mscorlib (not .NET 10 host runtime)
var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
var ioExceptionRef = new TypeReference("System.IO", "IOException", module, mscorlib);
Console.WriteLine($"IOException resolved from: {mscorlib.Name} v{mscorlib.Version}\n");

// ---- FIX 1: LONG PATH METHODS: NOP IOException throws after P/Invoke calls ----
// Wine returns false from Win32 calls even on success. Skip the throw.
string[] nopTargets = { "DeleteLongPathFile", "DeleteLongPathDirectory", "CreateLongPathDirectory", "LongPathMove" };
foreach (var methodName in nopTargets)
{
    var method = diskIO.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null || !method.HasBody) continue;
    var instr = method.Body.Instructions.ToList();
    Console.WriteLine($"=== {methodName} ({instr.Count} instructions) ===");

    for (int i = 0; i < instr.Count; i++)
    {
        if (instr[i].OpCode != OpCodes.Throw) continue;
        if (i < 1 || instr[i - 1].OpCode != OpCodes.Newobj) continue;
        var ctor = instr[i - 1].Operand as MethodReference;
        if (ctor == null || !ctor.DeclaringType.Name.Contains("IOException")) continue;

        Console.WriteLine($"  Found IOException throw at [{i-1}..{i}]");
        if (!dryRun)
        {
            instr[i - 1].OpCode = OpCodes.Nop; instr[i - 1].Operand = null;
            instr[i].OpCode = OpCodes.Nop; instr[i].Operand = null;
            Console.WriteLine("  -> PATCHED (NOP)");
        }
        else Console.WriteLine("  -> Would NOP");
        totalPatched++;
    }
}

// ---- FIX 2: SHORT PATH METHODS: wrap BCL calls in try-catch(IOException) ----
var tryCatchTargets = new[]
{
    ("Delete",          "System.IO.File",      "Delete"),
    ("DeleteDirectory", "System.IO.Directory", "Delete"),
    ("CreateDirectory", "System.IO.Directory", "CreateDirectory"),
    ("Move",            "System.IO.Directory", "Move"),
};

foreach (var (methodName, typeName, callName) in tryCatchTargets)
{
    var method = diskIO.Methods.FirstOrDefault(m => m.Name == methodName);
    if (method == null || !method.HasBody) continue;
    var instr = method.Body.Instructions;
    Console.WriteLine($"\n=== {methodName} (short path) ===");

    Instruction? targetCall = null;
    Instruction? retAfter = null;
    for (int i = 0; i < instr.Count; i++)
    {
        if (instr[i].OpCode != OpCodes.Call) continue;
        var target = instr[i].Operand as MethodReference;
        if (target == null || target.DeclaringType.FullName != typeName || target.Name != callName) continue;
        targetCall = instr[i];
        retAfter = instr[i + 1];
        Console.WriteLine($"  Found {typeName}::{callName} at [{i}], followed by {retAfter.OpCode}");
        break;
    }

    if (targetCall == null) { Console.WriteLine("  Not found, skipping"); continue; }

    Instruction? afterHandler;
    if (retAfter!.OpCode == OpCodes.Pop)
    {
        var retInstr = instr[instr.IndexOf(retAfter) + 1];
        afterHandler = retInstr;
        Console.WriteLine($"  Including pop in try block, handler ends at ret");
    }
    else
    {
        afterHandler = retAfter;
    }

    if (!dryRun)
    {
        var il = method.Body.GetILProcessor();
        var leaveTarget = afterHandler;
        var tryLeave = il.Create(OpCodes.Leave_S, leaveTarget);

        if (retAfter.OpCode == OpCodes.Pop)
            il.InsertAfter(retAfter, tryLeave);
        else
            il.InsertAfter(targetCall, tryLeave);

        var catchPop = il.Create(OpCodes.Pop);
        il.InsertAfter(tryLeave, catchPop);

        var catchLeave = il.Create(OpCodes.Leave_S, leaveTarget);
        il.InsertAfter(catchPop, catchLeave);

        var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = targetCall,
            TryEnd = catchPop,
            HandlerStart = catchPop,
            HandlerEnd = leaveTarget,
            CatchType = ioExceptionRef
        };
        method.Body.ExceptionHandlers.Add(handler);
        Console.WriteLine($"  -> PATCHED (try-catch around {callName})");
    }
    else Console.WriteLine($"  -> Would add try-catch(IOException)");
    totalPatched++;
}

// ---- FIX 3: CREATELONGPATHDIRECTORY: skip PathExists check per segment ----
Console.WriteLine("\n=== DiskIODefaultWindows.CreateLongPathDirectory (PathExists bypass) ===");
{
    var clpd = diskIO.Methods.FirstOrDefault(m => m.Name == "CreateLongPathDirectory");
    if (clpd != null && clpd.HasBody)
    {
        var clpdIl = clpd.Body.Instructions;
        bool found = false;
        for (int i = 0; i < clpdIl.Count - 1; i++)
        {
            if (clpdIl[i].OpCode != OpCodes.Callvirt && clpdIl[i].OpCode != OpCodes.Call) continue;
            var mr = clpdIl[i].Operand as MethodReference;
            if (mr == null || mr.Name != "PathExists") continue;
            if (clpdIl[i + 1].OpCode != OpCodes.Brtrue_S && clpdIl[i + 1].OpCode != OpCodes.Brtrue) continue;

            Console.WriteLine($"  Found PathExists + brtrue at [{i}..{i+1}]");
            if (!dryRun)
            {
                for (int j = i - 2; j <= i + 1; j++)
                {
                    clpdIl[j].OpCode = OpCodes.Nop;
                    clpdIl[j].Operand = null;
                }
                Console.WriteLine("  -> PATCHED (NOP'd)");
            }
            else Console.WriteLine("  -> Would NOP PathExists per-segment check");
            totalPatched++;
            found = true;
            break;
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 4: CREATEDIRECTORY: skip PathExists early exit ----
Console.WriteLine("\n=== DiskIODefaultWindows.CreateDirectory (PathExists bypass) ===");
{
    var createDir = diskIO.Methods.FirstOrDefault(m => m.Name == "CreateDirectory");
    if (createDir != null && createDir.HasBody)
    {
        var cdIl = createDir.Body.Instructions;
        bool found = false;
        for (int i = 0; i < cdIl.Count - 1; i++)
        {
            if (cdIl[i].OpCode != OpCodes.Callvirt && cdIl[i].OpCode != OpCodes.Call) continue;
            var mr = cdIl[i].Operand as MethodReference;
            if (mr == null || mr.Name != "PathExists") continue;
            if (cdIl[i + 1].OpCode != OpCodes.Brtrue_S && cdIl[i + 1].OpCode != OpCodes.Brtrue) continue;

            Console.WriteLine($"  Found PathExists + brtrue at [{i}..{i+1}]");
            if (!dryRun)
            {
                for (int j = i - 2; j <= i + 1; j++)
                {
                    cdIl[j].OpCode = OpCodes.Nop;
                    cdIl[j].Operand = null;
                }
                Console.WriteLine("  -> PATCHED (NOP'd)");
            }
            else Console.WriteLine("  -> Would NOP PathExists early-exit");
            totalPatched++;
            found = true;
            break;
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 5: CREATEWRITESTREAM: always create parent directory ----
Console.WriteLine("\n=== FileIO.CreateWriteStream (always create directory) ===");
var fileIO = module.Types.FirstOrDefault(t => t.Name == "FileIO");
if (fileIO != null)
{
    var createWriteStream = fileIO.Methods.FirstOrDefault(m => m.Name == "CreateWriteStream");
    if (createWriteStream != null && createWriteStream.HasBody)
    {
        var cwsIl = createWriteStream.Body.Instructions;
        bool found = false;
        for (int i = 0; i < cwsIl.Count - 1; i++)
        {
            if ((cwsIl[i].OpCode != OpCodes.Callvirt && cwsIl[i].OpCode != OpCodes.Call)) continue;
            var mr = cwsIl[i].Operand as MethodReference;
            if (mr == null || mr.Name != "PathExists") continue;
            if (cwsIl[i + 1].OpCode != OpCodes.Brtrue_S && cwsIl[i + 1].OpCode != OpCodes.Brtrue) continue;

            Console.WriteLine($"  Found PathExists + brtrue at [{i}..{i+1}]");
            if (!dryRun)
            {
                for (int j = i - 3; j <= i + 1; j++)
                {
                    cwsIl[j].OpCode = OpCodes.Nop;
                    cwsIl[j].Operand = null;
                }
                Console.WriteLine("  -> PATCHED (NOP'd)");
            }
            else Console.WriteLine("  -> Would NOP PathExists+brtrue block");
            totalPatched++;
            found = true;
            break;
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 6: GETLONGPATH: replace '/' with '\\' in Replace call ----
Console.WriteLine("\n=== DiskIODefaultWindows.GetLongPath (slash -> backslash) ===");
{
    var getLongPath = diskIO.Methods.FirstOrDefault(m => m.Name == "GetLongPath");
    if (getLongPath != null && getLongPath.HasBody)
    {
        var il = getLongPath.Body.Instructions;
        bool found = false;
        for (int i = 0; i < il.Count - 1; i++)
        {
            if (il[i].OpCode != OpCodes.Ldc_I4_S) continue;
            if ((sbyte)il[i].Operand != 47) continue;
            if (il[i + 1].OpCode != OpCodes.Ldsfld) continue;
            var field = il[i + 1].Operand as FieldReference;
            if (field == null || field.Name != "DirectorySeparatorChar") continue;

            Console.WriteLine($"  Found ldc.i4.s 47 + ldsfld DirectorySeparatorChar at [{i}..{i+1}]");
            if (!dryRun)
            {
                il[i].Operand = (sbyte)92;
                Console.WriteLine("  -> PATCHED (47 '/' -> 92 '\\\\')");
            }
            else Console.WriteLine("  -> Would change ldc.i4.s 47 -> 92");
            totalPatched++;
            found = true;
            // Don't break - two occurrences
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 7: CANCELLATION TOKEN: force IsCancellationRequested to always return false ----
// Wine causes CancellationToken to appear spuriously cancelled.
//
// The token address is loaded via either:
//   (a) ldarg.0 + ldflda field   -> NOP 2 preceding instructions
//   (b) ldfld X + ldflda field   -> NOP 2 preceding (but ldarg.0 is before that too, NOP 3)
//   (c) ldarga.s param           -> NOP 1 preceding instruction
//
// Then replace "call get_IsCancellationRequested" with "ldc.i4.0" (push false).
// The following branch stays intact and always takes the "not cancelled" path.
Console.WriteLine("\n=== Cancellation token checks (force never-cancelled) ===");
int cancelPatched = 0;
foreach (var type in module.Types)
{
    var allMethods = type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods));
    foreach (var method in allMethods)
    {
        if (!method.HasBody) continue;
        var il = method.Body.Instructions;
        for (int i = 1; i < il.Count; i++)
        {
            if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr == null || mr.Name != "get_IsCancellationRequested") continue;

            var typeName = method.DeclaringType.FullName;
            string nextInfo = (i + 1 < il.Count) ? $" + {il[i+1].OpCode}" : "";
            Console.WriteLine($"  {typeName}::{method.Name}[{i}]{nextInfo}");

            if (!dryRun)
            {
                // NOP preceding instructions based on pattern
                var prev = il[i - 1];
                if (prev.OpCode == OpCodes.Ldflda)
                {
                    // ldflda consumes an object ref from the stack
                    // Check if [i-2] is ldfld (which itself consumed an object ref)
                    if (i >= 3 && il[i - 2].OpCode == OpCodes.Ldfld)
                    {
                        // Pattern: ldarg.0 + ldfld + ldflda + call -> NOP 3
                        il[i - 3].OpCode = OpCodes.Nop; il[i - 3].Operand = null;
                        il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null;
                    }
                    else if (i >= 2)
                    {
                        // Pattern: ldarg.0 + ldflda + call -> NOP 2
                        il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null;
                    }
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                else if (prev.OpCode == OpCodes.Ldarga_S || prev.OpCode == OpCodes.Ldarga)
                {
                    // ldarga.s loads address directly, 1 instruction
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                else
                {
                    Console.WriteLine($"    WARNING: unexpected prev opcode {prev.OpCode}");
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                // Replace call with ldc.i4.0 (false = not cancelled)
                il[i].OpCode = OpCodes.Ldc_I4_0;
                il[i].Operand = null;
                Console.WriteLine("  -> PATCHED (always false)");
            }
            cancelPatched++;
            totalPatched++;
        }
    }
}
Console.WriteLine(cancelPatched > 0
    ? $"  Total: {(dryRun ? "Would patch" : "PATCHED")} {cancelPatched} cancellation check(s)"
    : "  Pattern not found!");

// ---- FIX 8: CREATELONGPATHFILESTREAM: NOP invalid-handle IOException ----
Console.WriteLine("\n=== DiskIODefaultWindows.CreateLongPathFileStream (invalid handle bypass) ===");
{
    var clpfs = diskIO.Methods.FirstOrDefault(m => m.Name == "CreateLongPathFileStream");
    if (clpfs != null && clpfs.HasBody)
    {
        var clpfsIl = clpfs.Body.Instructions.ToList();
        bool found = false;
        for (int i = 0; i < clpfsIl.Count; i++)
        {
            if (clpfsIl[i].OpCode != OpCodes.Throw) continue;
            if (i < 1 || clpfsIl[i - 1].OpCode != OpCodes.Newobj) continue;
            var ctor = clpfsIl[i - 1].Operand as MethodReference;
            if (ctor == null || !ctor.DeclaringType.Name.Contains("IOException")) continue;
            Console.WriteLine($"  Found IOException throw at [{i-1}..{i}]");
            if (!dryRun)
            {
                clpfsIl[i - 1].OpCode = OpCodes.Nop; clpfsIl[i - 1].Operand = null;
                clpfsIl[i].OpCode = OpCodes.Nop; clpfsIl[i].Operand = null;
                Console.WriteLine("  -> PATCHED (NOP)");
            }
            else Console.WriteLine("  -> Would NOP");
            totalPatched++;
            found = true;
            break;
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 9: FILEALREADYDOWNLOADED: always re-download ----
Console.WriteLine("\n=== DownloadFilesInManifest (FileAlreadyDownloaded -> always download) ===");
var remoteRepository = module.Types.FirstOrDefault(t => t.Name == "RemoteRepository");
if (remoteRepository != null)
{
    var dfimSM = remoteRepository.NestedTypes.FirstOrDefault(t => t.Name.Contains("DownloadFilesInManifest"));
    if (dfimSM != null)
    {
        var moveNext = dfimSM.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext != null && moveNext.HasBody)
        {
            var il = moveNext.Body.Instructions;
            bool found = false;
            for (int i = 0; i < il.Count - 1; i++)
            {
                if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
                var mr = il[i].Operand as MethodReference;
                if (mr == null || mr.Name != "GetResult") continue;
                if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) continue;
                if (!mr.DeclaringType.FullName.Contains("TaskAwaiter")) continue;
                var declType = mr.DeclaringType as GenericInstanceType;
                if (declType == null || declType.GenericArguments.Count == 0) continue;
                if (declType.GenericArguments[0].FullName != "System.Boolean") continue;

                Console.WriteLine($"  Found FileAlreadyDownloaded GetResult + brfalse at [{i}..{i+1}]");
                // brfalse POPS the bool from the stack; br does NOT.
                // Changing brfalse->br leaves a dangling bool that corrupts PerformDownload args.
                // Fix: pop the bool (stack-safe), then unconditionally branch to download path.
                var downloadTarget = (Instruction)il[i + 1].Operand; // the download path target
                if (!dryRun)
                {
                    il[i + 1].OpCode = OpCodes.Pop;      // consume the bool from GetResult
                    il[i + 1].Operand = null;
                    // Next instruction is the "already downloaded" path — replace with br to download
                    il[i + 2].OpCode = OpCodes.Br;
                    il[i + 2].Operand = downloadTarget;
                    Console.WriteLine("  -> PATCHED (pop + br: always re-downloads, stack-safe)");
                }
                else Console.WriteLine("  -> Would pop + br to always re-download");
                totalPatched++;
                found = true;
                break;
            }
            if (!found) Console.WriteLine("  Pattern not found!");
        }
    }
    else Console.WriteLine("  DownloadFilesInManifest state machine not found!");
}
else Console.WriteLine("  RemoteRepository type not found!");

// ---- FIX 10: INSTALLTOFOLDER: bypass GetInstalledVersion error ----
Console.WriteLine("\n=== InstallToFolder (GetInstalledVersion error bypass) ===");
var executor = module.Types.FirstOrDefault(t => t.Name == "Executor");
if (executor != null)
{
    var installSM = executor.NestedTypes.FirstOrDefault(t => t.Name == "<InstallToFolder>d__13");
    if (installSM != null)
    {
        var moveNext = installSM.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext != null && moveNext.HasBody)
        {
            var il = moveNext.Body.Instructions;
            bool patched = false;
            for (int i = 0; i < il.Count - 5; i++)
            {
                if (il[i].OpCode != OpCodes.Callvirt) continue;
                var mr = il[i].Operand as MethodReference;
                if (mr == null || mr.Name != "get_Success") continue;
                if (il[i + 1].OpCode != OpCodes.Brtrue_S && il[i + 1].OpCode != OpCodes.Brtrue) continue;

                var errorStart = il[i + 2];
                var errorMid = il[i + 3];
                var errorEnd = il[i + 4];
                if (errorEnd.OpCode != OpCodes.Leave && errorEnd.OpCode != OpCodes.Leave_S) continue;

                Console.WriteLine($"  Found get_Success() check at [{i}], error return at [{i+2}..{i+4}]");

                if (!dryRun)
                {
                    il[i + 1].OpCode = OpCodes.Pop;
                    il[i + 1].Operand = null;
                    errorStart.OpCode = OpCodes.Nop; errorStart.Operand = null;
                    errorMid.OpCode = OpCodes.Nop; errorMid.Operand = null;
                    errorEnd.OpCode = OpCodes.Nop; errorEnd.Operand = null;
                    Console.WriteLine("  -> PATCHED (error path NOP'd)");
                }
                else Console.WriteLine("  -> Would NOP error return path");
                totalPatched++;
                patched = true;
                break;
            }
            if (!patched) Console.WriteLine("  Pattern not found!");
        }
    }
    else Console.WriteLine("  InstallToFolder state machine not found!");
}
else Console.WriteLine("  Executor type not found!");

// ---- FIX 11: TaskCanceledException -> not treated as cancellation ----
Console.WriteLine("\n=== ResultFactory.CreateFileIoResultFromException (TaskCanceledException bypass) ===");
var resultFactory = module.Types.FirstOrDefault(t => t.Name == "ResultFactory");
if (resultFactory != null)
{
    var method = resultFactory.Methods.FirstOrDefault(m => m.Name == "CreateFileIoResultFromException");
    if (method != null && method.HasBody)
    {
        var il = method.Body.Instructions;
        bool found = false;
        for (int i = 0; i < il.Count - 2; i++)
        {
            if (il[i].OpCode != OpCodes.Isinst) continue;
            var typeRef = il[i].Operand as TypeReference;
            if (typeRef == null || !typeRef.Name.Contains("TaskCanceledException")) continue;

            Console.WriteLine($"  Found TaskCanceledException check at [{i}]");
            if (il[i + 1].OpCode == OpCodes.Brfalse_S || il[i + 1].OpCode == OpCodes.Brfalse)
            {
                var target = (Instruction)il[i + 1].Operand;
                Console.WriteLine($"  brfalse at [{i+1}] -> [{il.IndexOf(target)}]");
                if (!dryRun)
                {
                    il[i].OpCode = OpCodes.Nop;
                    il[i].Operand = null;
                    il[i + 1].OpCode = il[i + 1].OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br;
                    Console.WriteLine("  -> PATCHED (TaskCanceledException treated as regular exception)");
                }
                else Console.WriteLine("  -> Would bypass TaskCanceledException");
                totalPatched++;
                found = true;
            }
            break;
        }
        if (!found) Console.WriteLine("  Pattern not found!");
    }
}

// ---- FIX 12: IsCancelledOperation checks ----
// These check if a Result was flagged as cancelled and propagate cancellation upward.
// Replace with ldc.i4.0 (always false = never cancelled).
Console.WriteLine("\n=== IsCancelledOperation checks ===");
int icoPatched = 0;
foreach (var type in module.Types)
{
    var allMethods = type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods));
    foreach (var method in allMethods)
    {
        if (!method.HasBody) continue;
        var il = method.Body.Instructions;
        for (int i = 1; i < il.Count; i++)
        {
            if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr == null || mr.Name != "IsCancelledOperation") continue;

            var typeName = method.DeclaringType.FullName;
            string nextInfo = (i + 1 < il.Count) ? $" + {il[i+1].OpCode}" : "";
            Console.WriteLine($"  {typeName}::{method.Name}[{i}]{nextInfo}");

            if (!dryRun)
            {
                // IsCancelledOperation is a static extension method taking 1 argument.
                // The argument is loaded by 1-3 preceding instructions depending on pattern.
                var prev = il[i - 1];
                if (prev.OpCode == OpCodes.Ldfld && i >= 3 && il[i - 2].OpCode == OpCodes.Ldfld)
                {
                    // ldarg.0 + ldfld + ldfld + call -> NOP 3
                    il[i - 3].OpCode = OpCodes.Nop; il[i - 3].Operand = null;
                    il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null;
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                else if (prev.OpCode == OpCodes.Ldfld && i >= 2)
                {
                    // ldarg.0 + ldfld + call -> NOP 2
                    il[i - 2].OpCode = OpCodes.Nop; il[i - 2].Operand = null;
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                else
                {
                    // ldloc / ldarg -> NOP 1
                    il[i - 1].OpCode = OpCodes.Nop; il[i - 1].Operand = null;
                }
                il[i].OpCode = OpCodes.Ldc_I4_0;
                il[i].Operand = null;
                Console.WriteLine("  -> PATCHED (always false)");
            }
            icoPatched++;
            totalPatched++;
        }
    }
}
Console.WriteLine(icoPatched > 0
    ? $"  Total: {(dryRun ? "Would patch" : "PATCHED")} {icoPatched} IsCancelledOperation check(s)"
    : "  No IsCancelledOperation patterns found");

// ---- FIX 13: PERFORMDOWNLOAD: skip PathExists check (always create new file) ----
// Wine's PathExists returns TRUE for non-existent files. This makes PerformDownload
// try to APPEND to a non-existent file instead of creating a new one.
Console.WriteLine("\n=== FileDownloader.PerformDownload (PathExists bypass) ===");
var fileDownloader = module.Types.FirstOrDefault(t => t.Name == "FileDownloader");
if (fileDownloader != null)
{
    var pdSM = fileDownloader.NestedTypes.FirstOrDefault(t => t.Name.Contains("PerformDownload"));
    if (pdSM != null)
    {
        var moveNext = pdSM.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext != null && moveNext.HasBody)
        {
            var il = moveNext.Body.Instructions;
            bool found = false;
            for (int i = 0; i < il.Count - 1; i++)
            {
                if (il[i].OpCode != OpCodes.Callvirt && il[i].OpCode != OpCodes.Call) continue;
                var mr = il[i].Operand as MethodReference;
                if (mr == null || mr.Name != "PathExists") continue;
                if (il[i + 1].OpCode != OpCodes.Brfalse_S && il[i + 1].OpCode != OpCodes.Brfalse) continue;

                var target = (Instruction)il[i + 1].Operand;
                Console.WriteLine($"  Found PathExists + brfalse at [{i}..{i+1}] -> [{il.IndexOf(target)}]");
                if (!dryRun)
                {
                    // NOP the 5 instructions loading args + calling PathExists
                    // Pattern: ldloc.1 + ldfld _fileIo + ldarg.0 + ldfld localPath + callvirt PathExists
                    for (int j = i - 4; j <= i; j++)
                    {
                        il[j].OpCode = OpCodes.Nop;
                        il[j].Operand = null;
                    }
                    // Change brfalse to unconditional branch (stack is now empty)
                    il[i + 1].OpCode = OpCodes.Br;
                    il[i + 1].Operand = target;
                    Console.WriteLine("  -> PATCHED (always create new file)");
                }
                else Console.WriteLine("  -> Would bypass PathExists (always create)");
                totalPatched++;
                found = true;
                break;
            }
            if (!found) Console.WriteLine("  Pattern not found!");
        }
    }
    else Console.WriteLine("  PerformDownload state machine not found!");
}
else Console.WriteLine("  FileDownloader type not found!");

// ---- FIX 14: FileAlreadyDownloaded — always return false ----
// FileAlreadyDownloaded calls CheckIntegrity which calls CreateReadStream.
// Wine's PathExists lies → CreateReadStream tries to open non-existent file → exception
// kills DownloadFilesInManifest before PerformDownload ever runs.
// Fix: replace the method body to return Task.FromResult(false) immediately.
Console.WriteLine("\n=== RemoteRepository.FileAlreadyDownloaded (always return false) ===");
var remoteRepo14 = module.Types.FirstOrDefault(t => t.Name == "RemoteRepository");
if (remoteRepo14 != null)
{
    var fad = remoteRepo14.Methods.FirstOrDefault(m => m.Name == "FileAlreadyDownloaded");
    if (fad != null && fad.HasBody)
    {
        Console.WriteLine($"  Found FileAlreadyDownloaded ({fad.Body.Instructions.Count} instructions)");
        if (!dryRun)
        {
            // Build Task.FromResult<bool> method reference
            var mscorlibRef = module.AssemblyReferences.First(a => a.Name == "mscorlib");
            var taskType = new TypeReference("System.Threading.Tasks", "Task", module, mscorlibRef);
            var fromResultOpen = new MethodReference("FromResult", module.TypeSystem.Void, taskType);
            var genParam = new GenericParameter("TResult", fromResultOpen);
            fromResultOpen.GenericParameters.Add(genParam);
            fromResultOpen.ReturnType = new GenericInstanceType(
                new TypeReference("System.Threading.Tasks", "Task`1", module, mscorlibRef))
            { GenericArguments = { genParam } };
            fromResultOpen.Parameters.Add(new ParameterDefinition(genParam));
            var fromResultBool = new GenericInstanceMethod(fromResultOpen);
            fromResultBool.GenericArguments.Add(module.TypeSystem.Boolean);
            var fromResultRef = module.ImportReference(fromResultBool);

            // Clear existing body and replace with: ldc.i4.0; call Task.FromResult<bool>; ret
            fad.Body.Instructions.Clear();
            fad.Body.ExceptionHandlers.Clear();
            fad.Body.Variables.Clear();
            var ilp = fad.Body.GetILProcessor();
            ilp.Append(ilp.Create(OpCodes.Ldc_I4_0));
            ilp.Append(ilp.Create(OpCodes.Call, fromResultRef));
            ilp.Append(ilp.Create(OpCodes.Ret));
            Console.WriteLine("  -> PATCHED (always returns Task<false>)");
        }
        else Console.WriteLine("  -> Would replace with Task.FromResult(false)");
        totalPatched++;
    }
    else Console.WriteLine("  FileAlreadyDownloaded not found!");
}
else Console.WriteLine("  RemoteRepository not found!");

Console.WriteLine();

if (dryRun)
{
    Console.WriteLine($"Dry run complete. {totalPatched} fix(es) identified.");
    Console.WriteLine("Run with --patch to apply.");
}
else
{
    if (totalPatched > 0)
    {
        var tempPath = dllPath + ".tmp";
        module.Write(tempPath);
        module.Dispose();
        File.Move(tempPath, dllPath, overwrite: true);
        Console.WriteLine($"\nSaved patched DLL to {dllPath}");
        return 0;
    }
    else Console.WriteLine("Nothing to patch.");
}

module.Dispose();
return 0;
