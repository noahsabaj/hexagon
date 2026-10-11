#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
namespace Hexagon;

/// <summary>
/// A faction a character can belong to, authored as an asset. A faction is people and a look, not
/// powers: belonging to one lets a character do nothing. Everything a member can do comes from what
/// they carry, such as the keys their superiors issued them.
/// </summary>
[AssetType( Name = "Hexagon Faction", Extension = "faction", Category = "Hexagon" )]
public sealed class FactionDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Faction";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>What members wear. Anyone can see it; see <see cref="Looks"/>.</summary>
	[Property] public List<Clothing> Outfit { get; set; } = new();
	/// <summary>The body members have, or none for the engine's citizen.</summary>
	[Property] public Model? Body { get; set; }

	/// <summary>When set, an operator must whitelist the account before it can create a character here.</summary>
	[Property] public bool RequiresWhitelist { get; set; }

	public static FactionDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<FactionDefinition>( path, out var definition ) ? definition : null;

	public static IEnumerable<FactionDefinition> All => ResourceLibrary.GetAll<FactionDefinition>();
}
