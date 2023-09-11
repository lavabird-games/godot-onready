using System;
using System.Collections.Generic;
using System.Linq;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

using Lavabird.SceneOnReady.Fody.Extensions;

using TypeSystem = Fody.TypeSystem;

namespace Lavabird.SceneOnReady.Fody;

/// <summary>
/// Class that injects the __SceneOnReady wrapper class into the target assembly. This is used to reduce the IL
/// added to each _Ready method by moving common parts into a new class.
/// </summary>
internal class WrapperInjector
{
	/// <summary>
	/// The WrapperClass that we inject into the assembly.
	/// </summary>
	public TypeDefinition WrapperClass { get; private set; }

	/// <summary>
	/// The wrapper method around GetNode that we add to the assembly.
	/// </summary>
	public MethodDefinition GetNodeWrapperMethod { get; private set; }

    private WrapperInjector(ModuleDefinition module, TypeSystem typeSystem, GodotTypes godotTypes)
    {
		// We create a new class (__SceneOnReady), then add 1 helper method:
		// - GetNodeForMember<T> - a wrapper around GetNode with nicer error messages

		WrapperClass = CreateWrapperClass(typeSystem);

		// Setup GetNode wrapper method used by Ready mappings
		GetNodeWrapperMethod = CreateGetNodeForMember(module, typeSystem, godotTypes);
		WrapperClass.Methods.Add(GetNodeWrapperMethod);
	}

	/// <summary>
	/// Creates a new instance of the Wrapper class and injects it into the given module.
	/// </summary>
    public static WrapperInjector InjectIntoModule(ModuleDefinition module, TypeSystem typeSystem, GodotTypes godotTypes)
	{
		var wrapper = new WrapperInjector(module, typeSystem, godotTypes);
		module.Types.Add(wrapper.WrapperClass);

		return wrapper;
	}

	/// <summary>
	/// Creates a container class to hold our extra method calls in.
	/// </summary>
	private TypeDefinition CreateWrapperClass(TypeSystem typeSystem)
	{
		// Create new wrapper class
		var type = new TypeDefinition("Lavabird.SceneOnReady", "__SceneOnReady",
			TypeAttributes.Public | TypeAttributes.Sealed, typeSystem.ObjectReference);

		return type;
	}

	/// <summary>
	/// Injects a wraper around GetNode(NodePath) that we can then call from our injected _Ready code. Provides some 
	/// additional validation so we can give better error messages when nodes are configured incorrectly.
	/// </summary>
	private MethodDefinition CreateGetNodeForMember(ModuleDefinition module, TypeSystem typeSystem, GodotTypes godotTypes)
	{
		// We use String.Concat and the Excecption ctor so we need to import those types into the module
		var stringArrayType = new ArrayType(typeSystem.StringDefinition);
		var stringConcat = module.ImportReference(typeSystem.StringDefinition.GetMethod("Concat", stringArrayType));

		var exceptionType = typeSystem.ObjectDefinition.Module.Types.Where(t => t.Name == "Exception").Single();
		var exceptionCtor = module.ImportReference(exceptionType.GetConstructors()
				.Where(c => c.Parameters.HasArgs(typeSystem.StringDefinition)).Single());

		// IL for generated wrapper method:
		//
		// public static T GetNodeForMember<T>(Node scene, string nodePath, string memberName) where T : Node
		// {
		//   var node = scene.GetNodeOrNull(nodePath);
		//	 if (node == null)
		//	   throw new Exception($"SceneOnReady: Node '{nodePath}' was not found for member '{memberName}'");
		//   if (!(node is T))
		//     throw new Exception($"SceneOnReady: Node '{nodePath}' did not match the type of member '{memberName}' when instanced");
		//   return (T)node;
		// }

		var method = new MethodDefinition("GetNodeForMember",
			MethodAttributes.Public | MethodAttributes.Static, typeSystem.ObjectReference);

		var t = new GenericParameter("T", method);
		t.Constraints.Add(new GenericParameterConstraint(godotTypes!.Node));
		method.GenericParameters.Add(t);
		method.ReturnType = t;

		var argScene = method.AddParameter(new ParameterDefinition("scene", ParameterAttributes.None, godotTypes!.Node));
		var argNodePath = method.AddParameter(new ParameterDefinition("nodePath", ParameterAttributes.None, typeSystem.StringReference));
		var argMemberName = method.AddParameter(new ParameterDefinition("memberName", ParameterAttributes.None, typeSystem.StringReference));

		// We need some extra local vars to store the result of the GetNode call. This lets us validate before doing
		// the assignment so consumers can get a friendlier error message when nodes are configured incorrectly.

		var node = new VariableDefinition(godotTypes!.Node);
		method.Body.Variables.Add(node);

		var il = method.Body.GetILProcessor();

		var instructions = new List<Instruction>
		{
			// Nop to use as a base for the rest of instructions (for breakpoints - JIT or AOT will remove this later)
			il.Create(OpCodes.Nop),

			// Convert string to NodePath and make call to GetNode(NodePath)
			il.Create(OpCodes.Ldarg, argScene),
			il.Create(OpCodes.Ldarg, argNodePath),
			il.Create(OpCodes.Call, module.ImportReference(godotTypes!.NodePathOpImplicitMethod)),
			il.Create(OpCodes.Callvirt, module.ImportReference(godotTypes.NodeGetNodeMethod)),
			il.Create(OpCodes.Stloc, node),

			// Check if we got a node back, or if we got null (didn't find the node)
			il.Create(OpCodes.Ldloc, node),
			il.Create(OpCodes.Ldnull),
			il.Create(OpCodes.Ceq),
			il.Branch(OpCodes.Brfalse_S, out Instruction labelAfterNullCheck),

			// throw new Exception("SceneOnReady: Node '" + nodePath + "' was not found for member '" + memberName + "'");
			il.Create(OpCodes.Nop),
			il.Create(OpCodes.Ldc_I4_5),
			il.Create(OpCodes.Newarr, typeSystem.StringReference),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_0),
			il.Create(OpCodes.Ldstr, "SceneOnReady: Node '"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_1),
			il.Create(OpCodes.Ldarg, argNodePath),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_2),
			il.Create(OpCodes.Ldstr, "' was not found for member '"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_3),
			il.Create(OpCodes.Ldarg, argMemberName),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_4),
			il.Create(OpCodes.Ldstr, "'"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Call, stringConcat),
			il.Create(OpCodes.Newobj, exceptionCtor),
			il.Create(OpCodes.Throw),

			// Check if the node returned by Godot is of the correct type
			labelAfterNullCheck,
			il.Create(OpCodes.Ldloc, node),
			il.Create(OpCodes.Isinst, t),
			il.Create(OpCodes.Ldnull),
			il.Create(OpCodes.Cgt_Un),
			il.Create(OpCodes.Ldc_I4_0),
			il.Create(OpCodes.Ceq),
			il.Branch(OpCodes.Brfalse_S, out Instruction labelAfterTypeCheck),

			// throw new Exception("SceneOnReady: Node '" + nodePath + "' did not match the type of member '" + memberName + "' when instanced");
			il.Create(OpCodes.Nop),
			il.Create(OpCodes.Ldc_I4_5),
			il.Create(OpCodes.Newarr, typeSystem.StringReference),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_0),
			il.Create(OpCodes.Ldstr, "SceneOnReady: Node '"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_1),
			il.Create(OpCodes.Ldarg, argNodePath),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_2),
			il.Create(OpCodes.Ldstr, "' did not match the type of member '"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_3),
			il.Create(OpCodes.Ldarg, argMemberName),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Dup),
			il.Create(OpCodes.Ldc_I4_4),
			il.Create(OpCodes.Ldstr, "' when instanced"),
			il.Create(OpCodes.Stelem_Ref),
			il.Create(OpCodes.Call, stringConcat),
			il.Create(OpCodes.Newobj, exceptionCtor),
			il.Create(OpCodes.Throw),

			// Return the node now we know its type-safe
			labelAfterTypeCheck,
			il.Create(OpCodes.Ldloc, node),
			il.Create(OpCodes.Unbox_Any, t),
			il.Create(OpCodes.Ret),
		};

		il.InsertFirst(method.Body, instructions);
		il.Body.OptimizeMacros();

		return method;
	}
}
