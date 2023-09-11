using System;

namespace Lavabird.SceneOnReady;

/// <summary>
/// Indiciates that this field of property is associated with a Node in the scene. Will automatically set the
/// value of the field to a node based on the node path specified.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class OnReadyAttribute : Attribute
{
	/// <summary>
	/// The path to the node (relative to the scene root). If not specified the name of the field will be used as 
	/// a scene-unique path instead (i.e. %FieldName).
	/// </summary>
	public string? Path { get; set; }

	/// <summary>
	/// Specifies a path to Node to be resolved when instancing a scene.
	/// </summary>
	public OnReadyAttribute()
	{
		// No path specified. We will use the field name instead
	}

	/// <summary>
	/// Specifies a path to Node to be resolved when instancing a scene.
	/// </summary>
	/// <param name="path">The path to the node to be resolved.</param>
	public OnReadyAttribute(string path)
	{
		Path = path;
	}
}