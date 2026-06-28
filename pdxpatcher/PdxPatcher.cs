using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

var dllPath = args.Length > 0 ? args[0] : null;
if (dllPath == null) { Console.WriteLine("Usage: pdxpatcher <PDX.SDK.dll|Managed_dir> [--patch]"); return 1; }

// If given a directory, look for PDX.SDK.dll inside it
if (Directory.Exists(dllPath))
{
    var candidate = Path.Combine(dllPath, "PDX.SDK.dll");
    if (File.Exists(candidate)) dllPath = candidate;
    else { Console.WriteLine($"ERROR: PDX.SDK.dll not found in {dllPath}"); return 1; }
}

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
    ("DeleteFile",      "System.IO.File",      "Delete"),
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
        bool bodyReplaced = false;
        for (int i = 1; i < il.Count && !bodyReplaced; i++)
        {
            if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr == null || mr.Name != "get_IsCancellationRequested") continue;

            var typeName = method.DeclaringType.FullName;
            var prev = il[i - 1];
            string nextInfo = (i + 1 < il.Count) ? $" + {il[i+1].OpCode}" : "";
            Console.WriteLine($"  {typeName}::{method.Name}[{i}]{nextInfo} prev={prev.OpCode}");

            if (!dryRun)
            {
                // Wholesale-replace bool methods where prev is ret (null-check short-circuit path).
                // NOPing the ret would corrupt the stack — replace the entire body instead.
                if (prev.OpCode == OpCodes.Ret && method.ReturnType.MetadataType == MetadataType.Boolean)
                {
                    ReplaceWithReturnFalse(method);
                    Console.WriteLine("  -> PATCHED (wholesale-replace bool method)");
                    cancelPatched++;
                    totalPatched++;
                    bodyReplaced = true;
                    break;
                }
                // NOP preceding instructions based on pattern
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
        bool bodyReplaced = false;
        for (int i = 1; i < il.Count && !bodyReplaced; i++)
        {
            if (il[i].OpCode != OpCodes.Call && il[i].OpCode != OpCodes.Callvirt) continue;
            var mr = il[i].Operand as MethodReference;
            if (mr == null || mr.Name != "IsCancelledOperation") continue;

            var typeName = method.DeclaringType.FullName;
            string nextInfo = (i + 1 < il.Count) ? $" + {il[i+1].OpCode}" : "";
            Console.WriteLine($"  {typeName}::{method.Name}[{i}]{nextInfo}");

            if (!dryRun)
            {
                var prev = il[i - 1];
                if (prev.OpCode == OpCodes.Ret && method.ReturnType.MetadataType == MetadataType.Boolean)
                {
                    ReplaceWithReturnFalse(method);
                    Console.WriteLine("  -> PATCHED (wholesale-replace bool method)");
                    icoPatched++;
                    totalPatched++;
                    bodyReplaced = true;
                    break;
                }
                // IsCancelledOperation is a static extension method taking 1 argument.
                // The argument is loaded by 1-3 preceding instructions depending on pattern.
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

// ---- FIX 15: FileIO.GetLockToken — remove Win32 timer that fires in milliseconds under Wine ----
// CancellationTokenSource(TimeSpan.FromSeconds(10)) uses a Win32 waitable timer which Wine fires
// in milliseconds, cancelling all downloads before they begin.
// Fix: ignore the timeout entirely — pass the caller's token directly through (ldarg.1; ret).
Console.WriteLine("\n=== FileIO.GetLockToken (remove Win32 timer cancellation) ===");
int fix15Patched = 0;
if (fileIO != null)
{
    var getLockToken = fileIO.Methods.FirstOrDefault(m => m.Name == "GetLockToken");
    if (getLockToken?.HasBody == true)
    {
        Console.WriteLine($"  Found GetLockToken ({getLockToken.Body.Instructions.Count} instructions)");
        if (!dryRun)
        {
            getLockToken.Body.Instructions.Clear();
            getLockToken.Body.ExceptionHandlers.Clear();
            getLockToken.Body.Variables.Clear();
            var ilp15 = getLockToken.Body.GetILProcessor();
            ilp15.Append(ilp15.Create(OpCodes.Ldarg_1));
            ilp15.Append(ilp15.Create(OpCodes.Ret));
            Console.WriteLine("  -> PATCHED (ldarg.1; ret — pass-through)");
        }
        else Console.WriteLine("  -> Would replace with ldarg.1; ret");
        fix15Patched++;
        totalPatched++;
    }
    else Console.WriteLine("  GetLockToken not found!");
}
else Console.WriteLine("  FileIO type not found!");

// ---- FIX 16: FileIO.<CreateFileStream> — dispose lock on IOException to prevent semaphore deadlock ----
// Under Wine, PathExists returns true for non-existent files. CreateFileStream acquires a reader lock,
// then FileNotFoundException is thrown. The IOException catch block exits via leave without calling
// lockResult.Dispose(), leaving _readSemaphore at 0 and deadlocking all subsequent downloads.
Console.WriteLine("\n=== FileIO.<CreateFileStream> (dispose lock on IOException) ===");
int fix16Patched = 0;
if (fileIO != null)
{
    var createFileSM = fileIO.NestedTypes.FirstOrDefault(t => t.Name.Contains("CreateFileStream"));
    if (createFileSM != null)
    {
        var moveNext16 = createFileSM.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext16?.HasBody == true)
        {
            Console.WriteLine($"  Found {createFileSM.Name}::MoveNext ({moveNext16.Body.Instructions.Count} instructions)");
            var lockVar = moveNext16.Body.Variables.FirstOrDefault(v => v.VariableType.Name == "AcquireLockResult");
            Console.WriteLine(lockVar != null ? $"  Found AcquireLockResult local V_{lockVar.Index}" : "  AcquireLockResult local not found!");

            if (lockVar != null)
            {
                var lockResultTypeDef = lockVar.VariableType.Resolve();
                var disposeMethod = lockResultTypeDef?.Methods.FirstOrDefault(m => m.Name == "Dispose");
                Console.WriteLine(disposeMethod != null ? "  Found AcquireLockResult.Dispose" : "  AcquireLockResult.Dispose not found!");

                if (disposeMethod != null)
                {
                    var body16 = moveNext16.Body;
                    var ioHandlers = body16.ExceptionHandlers
                        .Where(h => h.HandlerType == ExceptionHandlerType.Catch)
                        .ToList();
                    Console.WriteLine($"  Found {ioHandlers.Count} catch block(s)");


                    foreach (var handler in ioHandlers)
                    {
                        var il16 = body16.Instructions;
                        bool hasCreateIo = false;
                        int hStart = il16.IndexOf(handler.HandlerStart);
                        int hEnd = il16.IndexOf(handler.HandlerEnd);
                        for (int j = hStart; j < hEnd; j++)
                        {
                            if ((il16[j].Operand as MethodReference)?.Name == "CreateIoResultFromException")
                            { hasCreateIo = true; break; }
                        }
                        if (!hasCreateIo) continue;

                        Console.WriteLine("  Found IOException catch with CreateIoResultFromException");
                        if (!dryRun)
                        {
                            var disposeRef = module.ImportReference(disposeMethod);
                            var ilp16 = body16.GetILProcessor();
                            // Iterate backwards so inserts don't shift later indices.
                            // AcquireLockResult is a reference type — load the reference (ldloc.s)
                            // and use callvirt, matching the SDK's own dispose pattern in this
                            // same method (the FileNotFound path: ldloc.s V_5; callvirt Dispose).
                            // IMPORTANT: capture the leave instruction in a local BEFORE inserting.
                            // Re-evaluating il16[j] after the first InsertBefore would point at the
                            // just-inserted instruction, reversing the order (call before ldloc).
                            for (int j = hEnd - 1; j >= hStart; j--)
                            {
                                if (il16[j].OpCode != OpCodes.Leave && il16[j].OpCode != OpCodes.Leave_S) continue;
                                var leaveInstr = il16[j];
                                var ldloc = ilp16.Create(OpCodes.Ldloc_S, lockVar);
                                var callDispose = ilp16.Create(OpCodes.Callvirt, disposeRef);
                                ilp16.InsertBefore(leaveInstr, ldloc);
                                ilp16.InsertBefore(leaveInstr, callDispose);
                                Console.WriteLine($"  -> Inserted ldloc.s; callvirt Dispose() before leave");
                                fix16Patched++;
                                totalPatched++;
                            }
                        }
                        else
                        {
                            int leaveCount = 0;
                            for (int j = hStart; j < hEnd; j++)
                                if (il16[j].OpCode == OpCodes.Leave || il16[j].OpCode == OpCodes.Leave_S) leaveCount++;
                            Console.WriteLine($"  -> Would insert Dispose() before {leaveCount} leave(s)");
                            fix16Patched += leaveCount;
                            totalPatched += leaveCount;
                        }
                    }
                    if (fix16Patched == 0) Console.WriteLine("  No qualifying IOException catch blocks found!");
                }
            }
        }
        else Console.WriteLine("  CreateFileStream MoveNext not found!");
    }
    else Console.WriteLine("  CreateFileStream state machine not found!");
}
else Console.WriteLine("  FileIO type not found (fix 16)!");

// ---- FIX 17: DiskIODefaultWindows ListFiles/ListDirectories/ListFilesRecursive — IOException wrap ----
// On fresh installs Wine's GetFileAttributes lies about non-existent directories.
// These methods throw IOException: "Success" when the dir doesn't exist.
// Fix: wrap each method body in try-catch(IOException){return new List<string>()}.
Console.WriteLine("\n=== DiskIODefaultWindows ListFiles/ListDirectories/ListFilesRecursive (IOException wrap) ===");
int fix17Patched = 0;
foreach (var listMethodName in new[] { "ListFiles", "ListDirectories", "ListFilesRecursive" })
{
    var listMethod = diskIO.Methods.FirstOrDefault(m => m.Name == listMethodName);
    if (listMethod?.HasBody != true) { Console.WriteLine($"  {listMethodName}: not found!"); continue; }

    // Reuse the List<T> constructor already referenced in the method body
    MethodReference? listCtor17 = null;
    foreach (var instr in listMethod.Body.Instructions)
    {
        if (instr.OpCode == OpCodes.Newobj && instr.Operand is MethodReference ctor17
            && ctor17.DeclaringType.Name.StartsWith("List`1") && ctor17.Name == ".ctor")
        { listCtor17 = ctor17; break; }
    }

    Console.WriteLine($"  {listMethodName} ({listMethod.Body.Instructions.Count} instr){(listCtor17 != null ? ", List ctor found" : ", List ctor NOT found — skipping")}");
    if (listCtor17 == null) continue;

    if (!dryRun)
        WrapBodyReturningListInTryCatch(listMethod, listCtor17, ioExceptionRef);
    else
        Console.WriteLine($"  -> Would wrap in try-catch(IOException){{return new List()}}");
    fix17Patched++;
    totalPatched++;
}
Console.WriteLine(fix17Patched > 0
    ? $"  Total: {(dryRun ? "Would patch" : "PATCHED")} {fix17Patched} list method(s)"
    : "  No list methods patched!");

Console.WriteLine();

if (dryRun)
{
    Console.WriteLine($"PDX.SDK: Dry run complete. {totalPatched} fix(es) identified.");
}
else if (totalPatched > 0)
{
    var tempPath = dllPath + ".tmp";
    module.Write(tempPath);
    module.Dispose();
    File.Move(tempPath, dllPath, overwrite: true);
    Console.WriteLine($"\nSaved patched PDX.SDK.dll ({totalPatched} fixes)");
}
else
{
    Console.WriteLine("PDX.SDK: Nothing to patch.");
    module.Dispose();
}

var managedDir = Path.GetDirectoryName(dllPath)!;

// ---- Colossal.IO.dll: LongDirectory FindNextFile error check ----
// Wine returns false from FindNextFile even on success, which triggers the error-check block
// (GetLastWin32Error → GetExceptionFromWin32Error → throw) in LongDirectory state machines.
// Fix: NOP that entire block in every LongDirectory nested-type MoveNext.
var colossalIoPath = Path.Combine(managedDir, "Colossal.IO.dll");
int colossalIoPatched = 0;

if (File.Exists(colossalIoPath))
{
    Console.WriteLine($"\n==== Colossal.IO.dll ====\n");
    var ioModule = ModuleDefinition.ReadModule(colossalIoPath, new ReaderParameters
    {
        ReadingMode = ReadingMode.Immediate,
        AssemblyResolver = new DefaultAssemblyResolver(),
        ReadSymbols = false
    });

    var longDirType = ioModule.Types.FirstOrDefault(t => t.Name == "LongDirectory");
    if (longDirType != null)
    {
        foreach (var nestedType in longDirType.NestedTypes)
        {
            var moveNext = nestedType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
            if (moveNext == null) continue;
            var instr = moveNext.Body.Instructions.ToList();
            for (int i = 0; i < instr.Count; i++)
            {
                if (instr[i].OpCode != OpCodes.Throw) continue;
                if (i < 1 || instr[i - 1].OpCode != OpCodes.Call) continue;
                var callTarget = instr[i - 1].Operand as MethodReference;
                if (callTarget == null || !callTarget.Name.Contains("GetExceptionFromWin32Error")) continue;

                int blockStart = -1;
                for (int j = i - 1; j >= Math.Max(0, i - 15); j--)
                {
                    if (instr[j].OpCode == OpCodes.Call)
                    {
                        var mr = instr[j].Operand as MethodReference;
                        if (mr != null && mr.Name.Contains("GetLastWin32Error")) { blockStart = j; break; }
                    }
                }
                if (blockStart == -1) continue;

                Console.WriteLine($"IO Fix: {nestedType.Name}::MoveNext — NOP GetLastWin32Error..throw at [{blockStart}..{i}]");
                if (!dryRun)
                {
                    for (int j = blockStart; j <= i; j++)
                    { instr[j].OpCode = OpCodes.Nop; instr[j].Operand = null; }
                    Console.WriteLine("  -> PATCHED");
                }
                else Console.WriteLine("  -> Would NOP");
                colossalIoPatched++;
            }
        }
    }
    else Console.WriteLine("  LongDirectory type not found!");

    if (!dryRun && colossalIoPatched > 0)
    {
        var bakPath = colossalIoPath + ".bak";
        if (!File.Exists(bakPath)) File.Copy(colossalIoPath, bakPath);
        var tmpPath = colossalIoPath + ".tmp";
        ioModule.Write(tmpPath);
        ioModule.Dispose();
        File.Move(tmpPath, colossalIoPath, overwrite: true);
        Console.WriteLine($"\nSaved patched Colossal.IO.dll ({colossalIoPatched} fixes)");
    }
    else
    {
        ioModule.Dispose();
        if (dryRun) Console.WriteLine($"\nColossal.IO: {colossalIoPatched} fix(es) identified.");
        else if (colossalIoPatched == 0) Console.WriteLine("  Colossal.IO.dll: already patched or pattern not found.");
    }
}
else Console.WriteLine($"\nColossal.IO.dll not found in {managedDir} (skipping)");

// ---- Colossal.IO.AssetDatabase.dll .priority File.Exists bypass ----
// Wine's GetFileAttributesW returns TRUE for non-existent files when parent dir exists.
// PopulateFromDirectory calls File.Exists(".priority") → true → File.ReadAllLines → FileNotFoundException
// Fix: NOP the File.Exists check, unconditional branch to skip .priority reading entirely.
var assetDbPath = Path.Combine(managedDir, "Colossal.IO.AssetDatabase.dll");
int assetDbPatched = 0;

if (File.Exists(assetDbPath))
{
    Console.WriteLine($"\n==== Colossal.IO.AssetDatabase.dll ====\n");
    var assetResolver = new DefaultAssemblyResolver();
    assetResolver.AddSearchDirectory(managedDir);
    var assetModule = ModuleDefinition.ReadModule(assetDbPath, new ReaderParameters
    {
        ReadingMode = ReadingMode.Immediate,
        AssemblyResolver = assetResolver,
        ReadSymbols = false
    });

    var fsd = assetModule.Types.FirstOrDefault(t => t.Name == "FileSystemDataSource");
    if (fsd != null)
    {
        var popDir = fsd.Methods.FirstOrDefault(m => m.Name == "PopulateFromDirectory");
        if (popDir != null)
        {
            var ail = popDir.Body.Instructions;

            // AssetDB Fix A: .priority File.Exists bypass
            // Wine's GetFileAttributesW returns TRUE for non-existent files when parent dir exists.
            // PopulateFromDirectory: File.Exists(".priority") → true → ReadAllLines → FileNotFoundException
            for (int i = 0; i < ail.Count - 5; i++)
            {
                if (ail[i].OpCode == OpCodes.Ldstr && (string)ail[i].Operand == ".priority")
                {
                    for (int j = i + 1; j < Math.Min(i + 10, ail.Count); j++)
                    {
                        if (ail[j].OpCode == OpCodes.Call && ail[j].Operand is MethodReference mr2
                            && mr2.Name == "Exists" && mr2.DeclaringType.Name == "File")
                        {
                            var brInst = ail[j + 1];
                            if (brInst.OpCode == OpCodes.Brfalse || brInst.OpCode == OpCodes.Brfalse_S)
                            {
                                var target = (Instruction)brInst.Operand;
                                Console.WriteLine($"AssetDB Fix A: .priority File.Exists bypass");
                                Console.WriteLine($"  [{ail.IndexOf(ail[j - 1])}] {ail[j - 1].OpCode} -> nop");
                                Console.WriteLine($"  [{ail.IndexOf(ail[j])}] call File::Exists -> nop");
                                Console.WriteLine($"  [{ail.IndexOf(brInst)}] {brInst.OpCode} -> br -> [{ail.IndexOf(target)}]");

                                if (!dryRun)
                                {
                                    ail[j - 1].OpCode = OpCodes.Nop;
                                    ail[j - 1].Operand = null;
                                    ail[j].OpCode = OpCodes.Nop;
                                    ail[j].Operand = null;
                                    brInst.OpCode = OpCodes.Br;
                                    Console.WriteLine("  -> PATCHED");
                                }
                                else Console.WriteLine("  -> Would patch");
                                assetDbPatched++;
                                break;
                            }
                        }
                    }
                    break;
                }
            }

            // AssetDB Fix B: NOP ThrowIfCancellationRequested in PopulateFromDirectory
            // Under Wine, CancellationToken appears spuriously cancelled (same root cause as FIX 7).
            // This call aborts the file enumeration loop before gameui.uiHost is processed,
            // causing the gameui AssetDatabase to never register → assetdb://gameui/index.html fails → black screen.
            for (int i = 1; i < ail.Count; i++)
            {
                if (ail[i].OpCode == OpCodes.Call && ail[i].Operand is MethodReference mrCancel
                    && mrCancel.Name == "ThrowIfCancellationRequested"
                    && mrCancel.DeclaringType.Name == "CancellationToken")
                {
                    var loadInst = ail[i - 1];
                    Console.WriteLine($"AssetDB Fix B: ThrowIfCancellationRequested NOP");
                    Console.WriteLine($"  [{i - 1}] {loadInst.OpCode} -> nop");
                    Console.WriteLine($"  [{i}] call CancellationToken::ThrowIfCancellationRequested -> nop");

                    if (!dryRun)
                    {
                        loadInst.OpCode = OpCodes.Nop;
                        loadInst.Operand = null;
                        ail[i].OpCode = OpCodes.Nop;
                        ail[i].Operand = null;
                        Console.WriteLine("  -> PATCHED");
                    }
                    else Console.WriteLine("  -> Would patch");
                    assetDbPatched++;
                    break;
                }
            }
        }
        else Console.WriteLine("  PopulateFromDirectory not found!");
    }
    else Console.WriteLine("  FileSystemDataSource not found!");

    if (!dryRun && assetDbPatched > 0)
    {
        var tempPath = assetDbPath + ".tmp";
        assetModule.Write(tempPath);
        assetModule.Dispose();
        File.Move(tempPath, assetDbPath, overwrite: true);
        Console.WriteLine($"\nSaved patched Colossal.IO.AssetDatabase.dll ({assetDbPatched} fix)");
    }
    else
    {
        assetModule.Dispose();
        if (dryRun) Console.WriteLine($"\nAssetDatabase: {assetDbPatched} fix(es) identified.");
    }
}
else Console.WriteLine($"\nColossal.IO.AssetDatabase.dll not found in {managedDir} (skipping AssetDB fixes)");

// ---- PSI Fix: Colossal.PSI.Common.dll DlcHelper.GetDlcAttributes — skip CorruptedContentException throw ----
// Wine's AES decryption fails with Bad PKCS7 padding on .ntl DLC attribute files.
// DlcHelper.GetDlcAttributes has a catch(Exception) block that throws CorruptedContentException
// when the failed DLC is named "Game" — this propagates as FATAL and kills the game before
// AssetDatabase scanning even starts.
// Fix: change brfalse.s to br.s in the "Game" check so ALL failures fall to the softer path
// that logs an error and continues the loop (same path used for non-Game DLCs).
var psiCommonPath = Path.Combine(managedDir, "Colossal.PSI.Common.dll");
int psiPatched = 0;

if (File.Exists(psiCommonPath))
{
    Console.WriteLine($"\n==== Colossal.PSI.Common.dll ====\n");
    var psiResolver = new DefaultAssemblyResolver();
    psiResolver.AddSearchDirectory(managedDir);
    var psiModule = ModuleDefinition.ReadModule(psiCommonPath, new ReaderParameters
    {
        ReadingMode = ReadingMode.Immediate,
        AssemblyResolver = psiResolver,
        ReadSymbols = false
    });

    var dlcHelper = psiModule.Types.FirstOrDefault(t => t.Name == "DlcHelper");
    if (dlcHelper != null)
    {
        var getDlcAttr = dlcHelper.Methods.FirstOrDefault(m => m.Name == "GetDlcAttributes");
        if (getDlcAttr?.HasBody == true)
        {
            var pil = getDlcAttr.Body.Instructions;
            for (int i = 0; i < pil.Count - 2; i++)
            {
                if (pil[i].OpCode != OpCodes.Ldstr || (string)pil[i].Operand != "Game") continue;
                if (pil[i + 1].OpCode != OpCodes.Call) continue;
                var mr = pil[i + 1].Operand as MethodReference;
                if (mr?.Name != "op_Equality" || mr.DeclaringType.FullName != "System.String") continue;
                var brInst = pil[i + 2];
                if (brInst.OpCode != OpCodes.Brfalse && brInst.OpCode != OpCodes.Brfalse_S) continue;

                Console.WriteLine($"PSI Fix: DlcHelper.GetDlcAttributes — skip CorruptedContentException throw for 'Game' DLC");
                Console.WriteLine($"  [{i}] ldstr \"Game\"");
                Console.WriteLine($"  [{i + 1}] call String::op_Equality");
                Console.WriteLine($"  [{i + 2}] {brInst.OpCode} -> {(brInst.OpCode == OpCodes.Brfalse_S ? "br.s" : "br")} (always skip throw)");

                if (!dryRun)
                {
                    brInst.OpCode = brInst.OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br;
                    Console.WriteLine("  -> PATCHED");
                }
                else Console.WriteLine("  -> Would patch");
                psiPatched++;
                break;
            }
            if (psiPatched == 0) Console.WriteLine("  Pattern not found in GetDlcAttributes!");
        }
        else Console.WriteLine("  GetDlcAttributes not found!");
    }
    else Console.WriteLine("  DlcHelper not found!");

    // HashHelper Fix: DISABLED — modifying ComputeHash via Mono.Cecil corrupts branch offsets
    // after multiple write cycles. The PSI Fix (brfalse→br) is sufficient to prevent crashes;
    // .DS_Store files must be deleted manually before launching (see install instructions).
#if HASHHELPER_FIX
    // HashHelper Fix: ComputeHash — skip hidden files (starting with '.') like .DS_Store
    // macOS creates .DS_Store files in Content/Game/ directories which Wine exposes.
    // ComputeHash enumerates all files (excluding only .ntl) and hashes their paths + contents.
    // These extra files change the xxHash3 key → wrong AES key → DecryptFile fails → Game DLC missing.
    // Fix: after the existing .ntl skip, also skip any file whose name starts with '.'.
    // Uses Path.GetFileName(relPath).StartsWith(".") — safe for empty strings and all path depths.
    var hashHelper = psiModule.Types.FirstOrDefault(t => t.Name == "HashHelper");
    if (hashHelper != null)
    {
        var computeHash = hashHelper.Methods.FirstOrDefault(m => m.Name == "ComputeHash");
        if (computeHash?.HasBody == true)
        {
            var chil = computeHash.Body.Instructions;
            bool chPatched = false;
            // Pattern: ldloc.s <relPath> + call Path.GetExtension + <ldarg> + call String.op_Equality + brtrue[.s] <continue>
            for (int i = 3; i < chil.Count - 1; i++)
            {
                if (chil[i].OpCode != OpCodes.Brtrue_S && chil[i].OpCode != OpCodes.Brtrue) continue;
                var eqMr = chil[i - 1].Operand as MethodReference;
                if (eqMr?.Name != "op_Equality" || eqMr.DeclaringType.FullName != "System.String") continue;
                var extMr = chil[i - 3].Operand as MethodReference;
                if (extMr?.Name != "GetExtension") continue;
                if (chil[i - 4].OpCode != OpCodes.Ldloc_S && chil[i - 4].OpCode != OpCodes.Ldloc) continue;
                var relPathVar = chil[i - 4].Operand as VariableDefinition;
                if (relPathVar == null) continue;

                var continueTarget = (Instruction)chil[i].Operand;

                // Check if the correct fix (StartsWith) is already applied → skip
                bool alreadyCorrect = i + 5 < chil.Count
                    && (chil[i + 1].OpCode == OpCodes.Ldloc_S || chil[i + 1].OpCode == OpCodes.Ldloc)
                    && (chil[i + 2].Operand as MethodReference)?.Name == "GetFileName"
                    && chil[i + 3].OpCode == OpCodes.Ldstr && (string?)chil[i + 3].Operand == "."
                    && (chil[i + 4].Operand as MethodReference)?.Name == "StartsWith"
                    && (chil[i + 5].OpCode == OpCodes.Brtrue || chil[i + 5].OpCode == OpCodes.Brtrue_S);

                if (alreadyCorrect)
                {
                    Console.WriteLine("HashHelper Fix: already correctly applied (StartsWith), skipping.");
                    chPatched = true;
                    break;
                }

                // Check if the old broken fix (get_Chars) is present → remove it first
                bool hasBrokenFix = i + 7 < chil.Count
                    && (chil[i + 1].OpCode == OpCodes.Ldloc_S || chil[i + 1].OpCode == OpCodes.Ldloc)
                    && (chil[i + 2].Operand as MethodReference)?.Name == "GetFileName"
                    && chil[i + 3].OpCode == OpCodes.Ldc_I4_0
                    && chil[i + 4].OpCode == OpCodes.Callvirt
                    && (chil[i + 4].Operand as MethodReference)?.Name == "get_Chars";

                Console.WriteLine($"HashHelper Fix: ComputeHash — skip hidden files (name starts with '.')");
                if (hasBrokenFix)
                    Console.WriteLine("  Detected old broken fix (get_Chars) — will remove and replace with StartsWith");
                Console.WriteLine($"  Insert after [{i}] brtrue: Path.GetFileName(relPath).StartsWith(\".\") → skip");

                if (!dryRun)
                {
                    var ilp = computeHash.Body.GetILProcessor();

                    // Remove old broken fix if present (7 instructions: ldloc + GetFileName + ldc.i4.0 + callvirt + ldc.i4.s + ceq + brtrue)
                    if (hasBrokenFix)
                    {
                        for (int r = 0; r < 7; r++)
                            ilp.Remove(chil[i + 1]); // after each removal, [i+1] shifts to the next
                        Console.WriteLine("  -> Removed 7 broken instructions");
                    }

                    // Build Path.GetFileName reference
                    MethodReference? getFileNameRef = chil
                        .Select(x => x.Operand as MethodReference)
                        .FirstOrDefault(m => m?.Name == "GetFileName" && m.DeclaringType.Name == "Path");
                    if (getFileNameRef == null)
                    {
                        var mscorlibRef2 = psiModule.AssemblyReferences.First(a => a.Name == "mscorlib");
                        var pathType = new TypeReference("System.IO", "Path", psiModule, mscorlibRef2);
                        var mr = new MethodReference("GetFileName", psiModule.TypeSystem.String, pathType);
                        mr.Parameters.Add(new ParameterDefinition(psiModule.TypeSystem.String));
                        getFileNameRef = psiModule.ImportReference(mr);
                    }

                    // Build String.StartsWith(string) reference
                    var mscorlibRef3 = psiModule.AssemblyReferences.First(a => a.Name == "mscorlib");
                    var strType2 = new TypeReference("System", "String", psiModule, mscorlibRef3);
                    var startsWithMr = new MethodReference("StartsWith", psiModule.TypeSystem.Boolean, strType2) { HasThis = true };
                    startsWithMr.Parameters.Add(new ParameterDefinition(psiModule.TypeSystem.String));
                    var startsWithRef = psiModule.ImportReference(startsWithMr);

                    // Insert: ldloc relPath → call GetFileName → ldstr "." → callvirt StartsWith → brtrue skip
                    var insertBefore = chil[i + 1];
                    var newBrtrue = ilp.Create(OpCodes.Brtrue, continueTarget);
                    ilp.InsertBefore(insertBefore, ilp.Create(OpCodes.Ldloc_S, relPathVar));
                    ilp.InsertBefore(insertBefore, ilp.Create(OpCodes.Call, getFileNameRef));
                    ilp.InsertBefore(insertBefore, ilp.Create(OpCodes.Ldstr, "."));
                    ilp.InsertBefore(insertBefore, ilp.Create(OpCodes.Callvirt, startsWithRef));
                    ilp.InsertBefore(insertBefore, newBrtrue);
                    Console.WriteLine("  -> PATCHED (StartsWith)");
                }
                else Console.WriteLine("  -> Would insert hidden-file skip (StartsWith)");
                psiPatched++;
                chPatched = true;
                break;
            }
            if (!chPatched) Console.WriteLine("  HashHelper Fix: pattern not found in ComputeHash!");
        }
        else Console.WriteLine("  HashHelper.ComputeHash not found!");
    }
    else Console.WriteLine("  HashHelper type not found!");
#endif // HASHHELPER_FIX

    if (!dryRun && psiPatched > 0)
    {
        var tempPath = psiCommonPath + ".tmp";
        psiModule.Write(tempPath);
        psiModule.Dispose();
        File.Move(tempPath, psiCommonPath, overwrite: true);
        Console.WriteLine($"\nSaved patched Colossal.PSI.Common.dll ({psiPatched} fix)");
    }
    else
    {
        psiModule.Dispose();
        if (dryRun) Console.WriteLine($"\nPSI Common: {psiPatched} fix(es) identified.");
    }
}
else Console.WriteLine($"\nColossal.PSI.Common.dll not found in {managedDir} (skipping PSI fix)");

Console.WriteLine($"\n==== Summary ====");
Console.WriteLine($"PDX.SDK.dll: {totalPatched} fixes");
Console.WriteLine($"Colossal.IO.dll: {colossalIoPatched} fixes");
Console.WriteLine($"Colossal.IO.AssetDatabase.dll: {assetDbPatched} fixes");
Console.WriteLine($"Colossal.PSI.Common.dll: {psiPatched} fixes");
Console.WriteLine($"Total: {totalPatched + colossalIoPatched + assetDbPatched + psiPatched} fixes");
if (dryRun) Console.WriteLine("Run with --patch to apply.");
return 0;

// ---- Helper: wholesale-replace a bool-returning method with "return false" ----
// Used when prev opcode is ret (null-check short-circuit) — NOPing ret corrupts the stack.
void ReplaceWithReturnFalse(MethodDefinition method)
{
    method.Body.Instructions.Clear();
    method.Body.ExceptionHandlers.Clear();
    method.Body.Variables.Clear();
    var ilp = method.Body.GetILProcessor();
    ilp.Append(ilp.Create(OpCodes.Ldc_I4_0));
    ilp.Append(ilp.Create(OpCodes.Ret));
}

// ---- Helper: wrap a list-returning method in try-catch(IOException){return new List()} ----
// All ret instructions become stloc+leave; after the try block the catch returns an empty list.
void WrapBodyReturningListInTryCatch(MethodDefinition method, MethodReference listCtor, TypeReference ioException)
{
    var body = method.Body;
    var ilp = body.GetILProcessor();
    var instructions = body.Instructions;

    var resultVar = new VariableDefinition(method.ReturnType);
    body.Variables.Add(resultVar);
    body.InitLocals = true;

    var afterTry = ilp.Create(OpCodes.Nop);

    // Replace all ret with stloc + leave.s afterTry (iterate backwards to keep indices stable)
    for (int i = instructions.Count - 1; i >= 0; i--)
    {
        if (instructions[i].OpCode != OpCodes.Ret) continue;
        instructions[i].OpCode = OpCodes.Stloc;
        instructions[i].Operand = resultVar;
        ilp.InsertAfter(instructions[i], ilp.Create(OpCodes.Leave_S, afterTry));
    }

    // Catch block: pop exception, create empty list, stloc, leave
    var catchPop  = ilp.Create(OpCodes.Pop);
    var catchNew  = ilp.Create(OpCodes.Newobj, listCtor);
    var catchStloc = ilp.Create(OpCodes.Stloc, resultVar);
    var catchLeave = ilp.Create(OpCodes.Leave_S, afterTry);
    ilp.Append(catchPop);
    ilp.Append(catchNew);
    ilp.Append(catchStloc);
    ilp.Append(catchLeave);

    // afterTry: ldloc + ret
    ilp.Append(afterTry);
    ilp.Append(ilp.Create(OpCodes.Ldloc, resultVar));
    ilp.Append(ilp.Create(OpCodes.Ret));

    body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
    {
        TryStart     = instructions[0],
        TryEnd       = catchPop,
        HandlerStart = catchPop,
        HandlerEnd   = afterTry,
        CatchType    = ioException
    });
    Console.WriteLine($"  -> PATCHED (try-catch(IOException) wrap)");
}
