#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
namespace Hexagon;

/// <summary>An item type, authored as an asset. Inventories store the asset path, never a copy of this.</summary>
[AssetType( Name = "Hexagon Item", Extension = "item", Category = "Hexagon" )]
public sealed class ItemDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Item";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property, Range( 1, 4 )] public int Width { get; set; } = 1;
	[Property, Range( 1, 4 )] public int Height { get; set; } = 1;
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>What holding this lets a character do. A key is an item that grants <c>door.lock</c>.</summary>
	[Property] public List<string> Grants { get; set; } = new();

	public static ItemDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<ItemDefinition>( path, out var definition ) ? definition : null;
}
