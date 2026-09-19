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

	/// <summary>Using it uses it up: food, drink, a bandage.</summary>
	[Property] public bool Consumable { get; set; }
	/// <summary>The button for using it, such as "Eat".</summary>
	[Property] public string UseTitle { get; set; } = "Use";
	/// <summary>What those nearby see when it is used, such as "eats a ration".</summary>
	[Property] public string UseEmote { get; set; } = string.Empty;

	/// <summary>Health restored by using it. Only consumables heal.</summary>
	[Property] public int Heals { get; set; }

	/// <summary>Harm done per attack while it is in hand. Zero means it is not a weapon.</summary>
	[Property] public int Damage { get; set; }
	[Property] public float Range { get; set; } = 1500f;
	/// <summary>Seconds between attacks.</summary>
	[Property] public float Cooldown { get; set; } = 0.5f;
	/// <summary>The item each attack uses up, or none for a weapon that needs no ammunition.</summary>
	[Property] public ItemDefinition? Ammo { get; set; }

	/// <summary>What holding this lets a character do. A key is an item that grants <c>door.lock</c>.</summary>
	[Property] public List<string> Grants { get; set; } = new();

	public static ItemDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<ItemDefinition>( path, out var definition ) ? definition : null;
}
