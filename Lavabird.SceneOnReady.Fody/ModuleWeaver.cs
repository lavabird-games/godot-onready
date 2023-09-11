using System;
using System.Collections.Generic;
using System.Linq;

using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

using Lavabird.SceneOnReady.Fody.Extensions;

namespace Lavabird.SceneOnReady.Fody;

public class ModuleWeaver : BaseModuleWeaver
{
	/// <summary>
	/// The full name of the NodeAttribute class used to mark fields or properties for Node mappings.
	/// </summary>
	public const string NodeAttributeFullName = "Lavabird.SceneOnReady.OnReadyAttribute";

	/// <summary>
	/// Mappings for all the Godot types we use.
	/// </summary>
	private static GodotTypes? godotTypes;

	/// <summary>
	/// The class we throw our util methods onto before injecting into the assembly. 
	/// </summary>
	private WrapperInjector? wrapper;

	/// <inheritdoc />
	public override void Execute()
	{
		// Make sure we can resolve all the Godot method calls we need from the GodotSharp assembly
		godotTypes = new GodotTypes(ModuleDefinition);

		bool foundMappings = InjectReadyMappings();

		if (!foundMappings)
		{
			WriteWarning("No NodeAttribute mappings found. Nothing to do.");
		}
	}

	/// <summary>
	/// Parses the module for any NodeAttribute mappings and injects the GetNode calls into class _Ready methods.
	/// </summary>
	private bool InjectReadyMappings()
	{
		bool foundMappings = false;

		foreach (TypeDefinition type in ModuleDefinition.Types.ToList())    // Need to copy as we're going to mutate
		{
			// Test if this type is a Godot Node 
			if (IsDerivedFrom(type, godotTypes!.Node.Resolve()))
			{
				var members = type.Properties.Cast<IMemberDefinition>().Union(type.Fields.Cast<IMemberDefinition>());

				var toMap = members
					.Where(p =>
						p.DeclaringType == type && // Inject for the class a field is defined in, not child classes
						p.HasCustomAttributes &&
						p.CustomAttributes.Any(a => a.AttributeType.FullName == NodeAttributeFullName));

				if (toMap.Any())
				{
					if (!foundMappings)
					{
						// First time we see mappings we need to inject our wrapper class & methods
						wrapper = WrapperInjector.InjectIntoModule(ModuleDefinition, TypeSystem, godotTypes);
						foundMappings = true;
					}

					// Find the _Ready method
					var readyMethod = type.Methods.FirstOrDefault(m => m.Name == GodotTypes.GodotReadyMethodName && m.HasBody);
					if (readyMethod == null)
					{
						// We had mappings but no _Ready method. We need to inject one ourselves
						readyMethod = InjectReadyMethod(type);
					}

					// Let's go! Wire up all the GetNode calls
					InjectGetNodesIntoMethod(readyMethod, toMap);

					// Warn non unique mappings - these won't cause an error, but are normally a copy and paste mistake
					WarnIfDuplicateNodepathMappings(toMap);
				}
			}
		}

		return foundMappings;
	}

	/// <summary>
	/// Injects a call to GetNode for the given ready method.
	/// </summary>
	private void InjectGetNodesIntoMethod(MethodDefinition method, IEnumerable<IMemberDefinition> members)
	{
		WriteDebug($"GetNode injection on {method.FullName} ({members} members)");

		ILProcessor il = method.Body.GetILProcessor();

		foreach(var member in members)
		{
			var attributeNodePath = GetPathFromAttributeMapping(member);

			// Checks we are assignable from Node, and if property that we have a setter. Will throw on failure
			ValidateMemberType(member, godotTypes!.Node.Resolve());

			// Get the generic for the type that we are mapping to
			var type = member is FieldDefinition field ? field.FieldType : (member as PropertyDefinition)?.PropertyType;
			var getNodeWrapperMethod = new GenericInstanceMethod(wrapper!.GetNodeWrapperMethod).WithArgs(type!);

			var instructions = new List<Instruction>
			{
				// Nop to use as a base for the rest of instructions (for breakpoints - JIT or AOT will remove this later)
				il.Create(OpCodes.Nop),

				// Call the wrapper method with the nodepath, and assign the result to the mapping
				il.Create(OpCodes.Ldarg_0),
				il.Create(OpCodes.Ldarg_0),
				il.Create(OpCodes.Ldstr, attributeNodePath),
				il.Create(OpCodes.Ldstr, member.Name),
				il.Create(OpCodes.Call, getNodeWrapperMethod),
				// We can either be setting a field or a property
				member is FieldDefinition ?
					il.Create(OpCodes.Stfld, (FieldDefinition)member) :
					il.Create(OpCodes.Call, ((PropertyDefinition)member).SetMethod),
			};

			// Inject at the head of the method, leaving the rest of the original method after
			il.InsertFirst(method.Body, instructions);
			il.Body.OptimizeMacros();
		}
	}

	/// <summary>
	/// Injects a _Ready method for classes that have NodeAttribute mappings but no _Ready method.
	/// </summary>
	private MethodDefinition InjectReadyMethod(TypeDefinition type)
	{
		var attrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.ReuseSlot | MethodAttributes.HideBySig;
		var method = new MethodDefinition("_Ready", attrs, TypeSystem.VoidReference);

		type.Methods.Add(method);

		// We need to call our base._Ready and then return
		var il = method.Body.GetILProcessor();
		var instructions = new List<Instruction>
		{
			il.Create(OpCodes.Nop),
			il.Create(OpCodes.Ldarg_0),
			il.Create(OpCodes.Call, ModuleDefinition.ImportReference(FindBaseReady(type.BaseType.Resolve()))),
			il.Create(OpCodes.Ret),
		};
		il.InsertFirst(method.Body, instructions);

		return method;
	}

	/// <summary>
	/// Finds the base class implementation of _Ready (if it has one). Keeps going up the inheritance tree until we 
	/// find one. We run this on Node types so there should always be one found unless the API changes.
	/// </summary>
	private MethodDefinition FindBaseReady(TypeDefinition type)
	{
		while(type != null)
		{
			var baseReady = type.Methods.FirstOrDefault(m => m.Name == GodotTypes.GodotReadyMethodName && m.HasBody);
			if (baseReady != null)
			{
				return baseReady;
			}

			type = type.BaseType.Resolve();
		}

		throw new WeavingException($"Failed to find base class _Ready method. Has the API changed?");
	}

	/// <summary>
	/// Checks whether the given member is a valid type for a Node mapping.
	/// </summary>
	private void ValidateMemberType(IMemberDefinition member, TypeDefinition type)
	{
		var memberType = member is FieldDefinition field ? field.FieldType : (member as PropertyDefinition)?.PropertyType;
		
		if (!IsDerivedFrom(memberType!.Resolve(), type))
		{
			throw new WeavingException($"Member {member.DeclaringType.FullName}::{member.Name} does not inherit from Godot.Node");
		}

		if (member is PropertyDefinition prop && prop.SetMethod == null)
		{
			throw new WeavingException($"Property {member.DeclaringType.FullName}::{member.Name} has no set method");
		}
	}

	/// <inheritdoc />
	public override IEnumerable<string> GetAssembliesForScanning()
	{
		yield return "netstandard";
		yield return "mscorlib";
	}

	/// <summary>
	/// Gets the node path from the given field. The field must have a NodeAttribute applied to it.
	/// </summary>
	private string GetPathFromAttributeMapping(IMemberDefinition mapping)
	{
		var attribute = mapping.CustomAttributes.Where(a => a.AttributeType.FullName == NodeAttributeFullName).First();

		// Attributes will be using ctor args rather than the actual fields
		var pathField = attribute.ConstructorArguments.FirstOrDefault();

		// The attribute can have no path set, in which case we use the field name as a scene unique name
		var path = pathField.Value as string;
		if (string.IsNullOrEmpty(path))
		{
			// Falling back to field name. Remove any _'s for private fields, and convert first letter to Uppercase
			path = mapping.Name.TrimStart('_');
			path = "%" + path[0].ToString().ToUpperInvariant() + path.Substring(1);
		}

		return path!;
	}

	/// <summary>
	/// Checks if the a type is derived from (or is) the given base type.
	/// </summary>
	private bool IsDerivedFrom(TypeDefinition type, TypeDefinition baseType)
	{
		if (type == baseType) return true;
		if (type.BaseType == null) return false; // We've reached the top of the tree without a match
		return IsDerivedFrom(type.BaseType.Resolve(), baseType);
	}

	/// <summary>
	/// Checks the attribute mappings for any duplicates. These won't cause an error, but are normally a copy and 
	/// paste mistake that we want to warn about.
	/// </summary>
	private void WarnIfDuplicateNodepathMappings(IEnumerable<IMemberDefinition> members)
	{
		var nodePaths = members.Select(m => GetPathFromAttributeMapping(m));

		var set = new HashSet<string>();
		foreach (var member in members)
		{
			var path = GetPathFromAttributeMapping(member);
			if (!set.Add(path))    // Already filtered for null attrs and paths
			{
				WriteWarning($"Duplicate node path '{path}' found on member {member.DeclaringType.Name}::{member.Name}");
			}
		}
	}
}
