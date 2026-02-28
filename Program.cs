using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

var dllPath = args.Length > 0 ? args[0] : null;
if (dllPath == null) { Console.WriteLine("Usage: cs2patcher <dll> [--patch]"); return 1; }

bool dryRun = args.Length < 2 || args[1] != "--patch";
Console.WriteLine($"Mode: {(dryRun ? "DRY RUN" : "PATCHING")}\nLoading: {dllPath}\n");

var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters { ReadingMode = Mono.Cecil.ReadingMode.Immediate });

var longDirType = module.Types.FirstOrDefault(t => t.Name == "LongDirectory");
if (longDirType == null) { Console.WriteLine("ERROR: LongDirectory not found!"); return 1; }

Console.WriteLine($"Found: {longDirType.FullName}");

int totalPatched = 0;

foreach (var nestedType in longDirType.NestedTypes)
{
    var moveNext = nestedType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
    if (moveNext == null) continue;

    var instructions = moveNext.Body.Instructions.ToList();
    Console.WriteLine($"\nInspecting: {nestedType.Name}::MoveNext ({instructions.Count} instructions)");

    for (int i = 0; i < instructions.Count; i++)
    {
        // Pattern: call GetExceptionFromWin32Error -> throw
        if (instructions[i].OpCode != OpCodes.Throw) continue;

        var prev = instructions[i - 1];
        if (prev.OpCode != OpCodes.Call) continue;
        var callTarget = prev.Operand as MethodReference;
        if (callTarget == null || !callTarget.Name.Contains("GetExceptionFromWin32Error")) continue;

        Console.WriteLine($"  Found GetExceptionFromWin32Error->throw at index {i - 1}..{i}");

        // Walk backwards to find GetLastWin32Error call
        int blockStart = -1;
        for (int j = i - 1; j >= Math.Max(0, i - 15); j--)
        {
            var ins = instructions[j];
            if (ins.OpCode == OpCodes.Call)
            {
                var m = ins.Operand as MethodReference;
                if (m != null && m.Name.Contains("GetLastWin32Error"))
                {
                    blockStart = j;
                    break;
                }
            }
        }

        if (blockStart == -1)
        {
            Console.WriteLine($"  WARNING: Could not find GetLastWin32Error before index {i}");
            continue;
        }

        Console.WriteLine($"  Block: [{blockStart}..{i}]");
        for (int j = blockStart; j <= i; j++)
            Console.WriteLine($"    [{j}] {instructions[j].OpCode,-15} {instructions[j].Operand}");

        if (!dryRun)
        {
            for (int j = blockStart; j <= i; j++)
            {
                instructions[j].OpCode = OpCodes.Nop;
                instructions[j].Operand = null;
            }
            Console.WriteLine($"  -> PATCHED ({i - blockStart + 1} instructions -> NOP)");
        }
        else
        {
            Console.WriteLine($"  -> Would NOP {i - blockStart + 1} instructions");
        }
        totalPatched++;
    }
}

Console.WriteLine();

if (dryRun)
{
    Console.WriteLine($"Dry run complete. Found {totalPatched} block(s) to patch.");
    Console.WriteLine("Run with --patch to apply changes.");
}
else
{
    if (totalPatched > 0)
    {
        // Write to temp file first, then atomically replace the target
        var tempPath = dllPath + ".tmp";
        module.Write(tempPath);
        module.Dispose();
        File.Move(tempPath, dllPath, overwrite: true);
        Console.WriteLine($"Saved patched DLL to {dllPath}");
        return 0;
    }
    else
    {
        Console.WriteLine("Nothing patched.");
    }
}

module.Dispose();
return 0;
