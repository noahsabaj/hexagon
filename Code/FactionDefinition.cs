#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
namespace Hexagon;

/// <summary>A faction a character can belong to, authored as an asset.</summary>
[AssetType( Name = "Hexagon Faction", Extension = "faction", Category = "Hexagon" )]
public sealed class FactionDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Faction";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property] public Color Tint { get; set; } = Color.White;

	/// <summary>When set, an operator must whitelist the account before it can create a character here.</summary>
	[Property] public bool RequiresWhitelist { get; set; }

	/// <summary>What members are able to do, such as <c>door.lock</c>. See <see cref="Hexagon.Logic.Capabilities"/>.</summary>
	[Property] public List<string> Capabilities { get; set; } = new();

	/// <summary>Tokens a new character is issued.</summary>
	[Property] public long StartingTokens { get; set; } = 100;

	/// <summary>Tokens paid to each member in the city every wage period, from the wage source.</summary>
	[Property] public long Wage { get; set; }

	[Property] public List<StartingItem> StartingItems { get; set; } = new();

	/// <summary>What a new member is issued, and how many.</summary>
	public sealed class StartingItem
	{
		[Property] public ItemDefinition? Item { get; set; }
		[Property, Range( 1, 100 )] public int Count { get; set; } = 1;
	}

	public static FactionDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<FactionDefinition>( path, out var definition ) ? definition : null;

	public static IEnumerable<FactionDefinition> All => ResourceLibrary.GetAll<FactionDefinition>();
}
