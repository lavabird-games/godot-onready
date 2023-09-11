using System;
using System.Collections.Generic;
using System.Linq;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Lavabird.SceneOnReady.Fody.Extensions;

/// <summary>
/// Helper extensions to make working with Cecil type collections a little easier.
/// </summary>
internal static class CecilExtensions
{
    /// <summary>
    /// Helper method to create and add a new parameter to the given method. We always want to do these together
    /// so its easier if they are in a single step with an assignment.
    /// </summary>
    public static ParameterDefinition AddParameter(this MethodDefinition method, ParameterDefinition newParam)
    {
        method.Parameters.Add(newParam);
        return newParam;
    }

    /// <summary>
    /// Adds the given args list to the GenericInstanceMethod. Allows for a fluent style of adding args, especially
    /// when there are multiple generic args to add
    /// </summary>
    public static GenericInstanceMethod WithArgs(this GenericInstanceMethod method, params TypeReference[] args)
    {
        foreach (var arg in args)
        {
            method.GenericArguments.Add(arg);
        }

        return method;
    }

    /// <summary>
    /// Checks if the given args type list matches the paramater list. Used so we can easily compare method signatures.
    /// </summary>
    public static bool HasArgs(this Mono.Collections.Generic.Collection<ParameterDefinition> argsList, params TypeReference[] args)
    {
        if (argsList.Count != args.Length) return false;
        for (var i = 0; i < argsList.Count; i++)
        {
            if (argsList[i].ParameterType.FullName != args[i].FullName) return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the list of methods that match the given name and args list.
    /// </summary>
    public static MethodDefinition? GetMethod(this TypeDefinition type, string methodName, params TypeReference[] args)
    {
        return type.GetMethods().Where(m => m.Name == methodName && m.Parameters.HasArgs(args)).SingleOrDefault();
	}
}
