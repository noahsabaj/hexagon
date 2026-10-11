#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What a new character may be made with: a body the game offers, hair, and clothes from its
/// wardrobe. The body is the character's for life. The clothes are items it arrives wearing, from the
/// named source <see cref="Sources.Arrival"/>; after that, nothing about how it looks changes unless
/// someone in the world changes it, by putting on what they were handed or taking off what they wear.
/// </summary>
public static class Appearance
{
	/// <summary>How many pieces of hair and clothing a new character may choose, together.</summary>
	public const int MaximumPieces = 12;

	/// <summary>Pieces as a request carries them: their paths, joined by ';', which no path holds.</summary>
	public static string Join( IEnumerable<string> pieces ) => string.Join( ';', pieces );

	public static string[] Split( string? pieces ) =>
		(pieces ?? string.Empty).Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );

	/// <summary>A piece on offer, as the engine's clothing: hair as it is, an item as what it is worn as.</summary>
	public static Clothing? ClothingOf( string piece ) =>
		ItemDefinition.Find( piece )?.Wearable ?? (piece.EndsWith( ".clothing", StringComparison.OrdinalIgnoreCase ) ? ResourceLibrary.Get<Clothing>( piece ) : null);

	/// <summary>A picture of a body or piece on offer, if the engine made one.</summary>
	public static string? IconOf( string piece ) =>
		ItemDefinition.Find( piece )?.IconPath ?? (ClothingOf( piece )?.Icon.Path is { Length: > 0 } icon ? icon : null);

	/// <summary>The bodies on offer, by path: the engine's citizen as the empty path, then the human skins the game lists.</summary>
	public static IEnumerable<string> Bodies( HexagonServices game ) =>
		(game.OffersCitizen || !game.Bodies.Any( value => value.IsValid() ) ? new[] { string.Empty } : Array.Empty<string>())
			.Concat( game.Bodies.Where( value => value.IsValid() && value.HasHumanSkin ).Select( value => value.ResourcePath ) );

	/// <summary>The hair and facial hair on offer.</summary>
	public static IEnumerable<Clothing> Hair( HexagonServices game ) => game.Hair.Where( value => value.IsValid() );

	/// <summary>The clothes a new character may arrive in: every item the game marks as wardrobe.</summary>
	public static IEnumerable<ItemDefinition> Wardrobe =>
		ResourceLibrary.GetAll<ItemDefinition>().Where( value => value.Wardrobe && value.Wearable.IsValid() ).OrderBy( value => value.Title );

	/// <summary>
	/// Host: checks what a new character is to be made with, as asked by its player: a body on offer,
	/// and pieces each either hair on offer or clothing from the wardrobe, which can all be worn together.
	/// </summary>
	public static Result<(List<string> Body, List<ItemDefinition> Clothes)> Choose( HexagonServices game, string body, IReadOnlyList<string> pieces )
	{
		static Result<(List<string>, List<ItemDefinition>)> Refuse( string why ) => Result<(List<string>, List<ItemDefinition>)>.Fail( ErrorCode.Invalid, why );
		if ( !Bodies( game ).Contains( body ) ) return Refuse( "That body is not on offer." );
		if ( pieces.Count > MaximumPieces ) return Refuse( $"Choose at most {MaximumPieces} pieces." );
		if ( pieces.Distinct().Count() != pieces.Count ) return Refuse( "Each piece is chosen once." );
		var hair = new List<Clothing>();
		var clothes = new List<ItemDefinition>();
		foreach ( var piece in pieces )
		{
			if ( Hair( game ).FirstOrDefault( value => value.ResourcePath == piece ) is { } style ) hair.Add( style );
			else if ( Wardrobe.FirstOrDefault( value => value.ResourcePath == piece ) is { } item ) clothes.Add( item );
			else return Refuse( "That is not on offer." );
		}
		// What the engine says cannot be worn together. A hat over hair is fine: it hides it.
		for ( var a = 0; a < hair.Count; a++ )
		for ( var b = a + 1; b < hair.Count; b++ )
			if ( !hair[a].CanBeWornWith( hair[b] ) ) return Refuse( $"{hair[a].Title} and {hair[b].Title} cannot go together." );
		for ( var a = 0; a < clothes.Count; a++ )
		for ( var b = a + 1; b < clothes.Count; b++ )
			if ( !clothes[a].Wearable!.CanBeWornWith( clothes[b].Wearable ) )
				return Refuse( $"The {clothes[a].InSentence} cannot be worn with the {clothes[b].InSentence}." );
		var parts = new List<string>();
		if ( body.Length > 0 ) parts.Add( body );
		parts.AddRange( hair.Select( value => value.ResourcePath ) );
		return Result<(List<string>, List<ItemDefinition>)>.Success( (parts, clothes) );
	}
}
