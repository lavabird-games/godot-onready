using System;
using System.Linq;
using System.Reflection;

using Godot;

using Fody;
using Mono.Cecil;

namespace Lavabird.SceneOnReady.Fody;

/// <summary>
/// Fetches references to the Godot types we need to use in the weaving process.
/// </summary>
internal class GodotTypes
{
	/// <summary>
	/// The name of the _Ready method in Godot. This is the method we will inject GetNode(...) calls into.
	/// </summary>
	public const string GodotReadyMethodName = "_Ready";

	/// <summary>
	/// The type used for Godot Nodes.
	/// </summary>
	public TypeReference Node { get; private set; } = null!; // We throw if not found

	/// <summary>
	/// Reference to the MethodInfo for Godot's GetNode(NodePath nodePath) method.
	/// </summary>
	public MethodInfo NodeGetNodeMethod { get; private set; } = null!; // We throw if not found

	/// <summary>
	/// Reference to the MethodInfo for Godot's NodePath implicit conversion from string.
	/// </summary>
	public MethodInfo NodePathOpImplicitMethod { get; private set; } = null!; // We throw if not found

	public GodotTypes(ModuleDefinition module)
    {
		ResolveMethodCalls(module);
	}

	/// <summary>
	/// Resolves the external calls and types that we need to make to fetch nodes.
	/// </summary>
	private void ResolveMethodCalls(ModuleDefinition module)
	{
		Node = GetTypeOrThrow(typeof(Godot.Node), module);

		// Node.GetNodeOrNull(NodePath)
		NodeGetNodeMethod = GetMethodOrThrow(typeof(Godot.Node), "GetNodeOrNull", 
			BindingFlags.Instance | BindingFlags.Public, false, typeof(Godot.NodePath));

		// NodePath.op_Implicit(string) (implicit conversion from string)
		NodePathOpImplicitMethod = GetMethodOrThrow(typeof(Godot.NodePath), "op_Implicit", 
			BindingFlags.Static | BindingFlags.Public, false, typeof(string));
	}

	/// <summary>
	/// Attempts to resolve the given type, throwing a WeavingException if it fails.
	/// </summary>
	private TypeReference GetTypeOrThrow(Type type, ModuleDefinition module)
	{
		var typeRef = module.ImportReference(type);
		if (typeRef == null)
		{
			throw new WeavingException($"Failed to resolve '{type.FullName}' type.");
		}
		return typeRef;
	}

	/// <summary>
	/// Attempts to resolve the given method on the given type, throwing a WeavingException if it fails.
	/// </summary>
	private MethodInfo GetMethodOrThrow(Type type, string methodName, 
		BindingFlags bindingFlags, bool isGeneric, params Type[] parameterTypes)
	{
		var method = type
			.GetMethods(bindingFlags)
			.Where(m => m.Name == methodName && m.ContainsGenericParameters == isGeneric &&
				m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes));

		if (!method.Any())
		{
			throw new WeavingException($"Failed to resolve '{methodName}' method. Method not found.");
		}
		if (method.Count() > 1)
		{
			throw new WeavingException(
				$"Failed to resolve '{methodName}' method. Ambiguous between multiple matches ({method.Count()}).");
		}

		return method.First();
	}
}
