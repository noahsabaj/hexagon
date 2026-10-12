#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;
namespace Hexagon;

/// <summary>
/// A faction a character can belong to, authored as an asset. A faction is people, not powers or a
/// look: belonging to one lets a character do nothing and changes nothing about them. Everything a
/// member can do comes from what they carry, such as the keys their superiors issued them, and they
/// look like a member by putting on the uniform they were handed. Everyone is made belonging to none.
/// Its ranks say only what a superior may do to those below them: enlist, promote and discharge.
/// </summary>
[AssetType( Name = "Hexagon Faction", Extension = "faction", Category = "Hexagon" )]
public sealed class FactionDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Faction";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property] public Color Tint { get; set; } = Color.White;
	/// <summary>Its ranks, lowest first. A recruit joins at the first; staff appoint the head at the last.</summary>
	[Property] public List<FactionRank> Ranks { get; set; } = new();

	/// <summary>Its ranks as the rules weigh them. See <see cref="Logic.Ranks"/>.</summary>
	public IReadOnlyList<RankRule> Rules => Ranks.Select( value => new RankRule( value.Title, value.Enlists, value.Promotes,
		string.IsNullOrEmpty( value.PromotesTo ) ? -1 : Ranks.FindIndex( other => string.Equals( other.Title, value.PromotesTo, StringComparison.OrdinalIgnoreCase ) ),
		value.Discharges ) ).ToArray();

	/// <summary>The rank of that title, or null.</summary>
	public FactionRank? Rank( string? title ) =>
		Ranks.FirstOrDefault( value => string.Equals( value.Title, title, StringComparison.OrdinalIgnoreCase ) );

	public static FactionDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<FactionDefinition>( path, out var definition ) ? definition : null;

	public static IEnumerable<FactionDefinition> All => ResourceLibrary.GetAll<FactionDefinition>();
}

/// <summary>A rank in a faction, and what someone holding it may do to those below them.</summary>
public sealed class FactionRank
{
	[Property] public string Title { get; set; } = "Rank";
	/// <summary>
	/// What is worn to show it, such as shoulder boards. Nothing puts it on anyone: a superior hands it
	/// over with the rank, and it is put on by hand, so what others see is what someone wears.
	/// </summary>
	[Property] public ItemDefinition? Insignia { get; set; }
	/// <summary>Someone of this rank may offer a newcomer a place in the faction, at its lowest rank.</summary>
	[Property] public bool Enlists { get; set; }
	/// <summary>Someone of this rank may give those below them a rank below their own.</summary>
	[Property] public bool Promotes { get; set; }
	/// <summary>The highest rank it may give, by title; empty for any rank below its own.</summary>
	[Property] public string PromotesTo { get; set; } = string.Empty;
	/// <summary>Someone of this rank may discharge those below them.</summary>
	[Property] public bool Discharges { get; set; }
}
