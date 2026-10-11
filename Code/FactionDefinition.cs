#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
namespace Hexagon;

/// <summary>
/// A faction a character can belong to, authored as an asset. A faction is people, not powers or a
/// look: belonging to one lets a character do nothing and changes nothing about them. Everything a
/// member can do comes from what they carry, such as the keys their superiors issued them, and they
/// look like a member by putting on the uniform they were handed. Everyone is made belonging to none.
/// </summary>
[AssetType( Name = "Hexagon Faction", Extension = "faction", Category = "Hexagon" )]
public sealed class FactionDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Faction";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property] public Color Tint { get; set; } = Color.White;

	public static FactionDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<FactionDefinition>( path, out var definition ) ? definition : null;

	public static IEnumerable<FactionDefinition> All => ResourceLibrary.GetAll<FactionDefinition>();
}
