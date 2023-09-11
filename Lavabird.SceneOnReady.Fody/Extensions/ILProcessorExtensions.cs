using System;
using System.Collections.Generic;
using System.Linq;

using Mono.Cecil.Cil;

namespace Lavabird.SceneOnReady.Fody.Extensions;

/// <summary>
/// Helper extensions to make writing IL a little cleaner.
/// </summary>
internal static class ILProcessorExtensions
{
    /// <summary>
    /// Helper method to generate a nop instruction for later as an out param. This can then be used as a target
    /// for the branch (which we might not know at the time we are creating the branch).
    /// </summary>
    public static Instruction Branch(this ILProcessor il, OpCode branchCode, out Instruction branchTarget)
    {
        // Nop label to use as the tag for the branch target (will be JIT'd out)
        branchTarget = il.Create(OpCodes.Nop);

        return il.Create(branchCode, branchTarget);
    }

    /// <summary>
    /// Inserts the given list of instructions at the start of the body. Will create a first instruction if the body
    /// is currently empty. Any existing instructions will be moved down.
    /// </summary>
    public static void InsertFirst(this ILProcessor il, MethodBody body, IList<Instruction> instructions)
    {
        if (body.Instructions.Count == 0)
        {
            // Special case for empty methods
            il.Append(instructions[0]);
        }
        else
        {
            il.InsertBefore(body.Instructions.First(), instructions[0]);
        }

        var last = instructions[0];
        for (var n = 1; n < instructions.Count; n++)
        {
            il.InsertAfter(last, instructions[n]);
            last = instructions[n];
        }
    }
}
