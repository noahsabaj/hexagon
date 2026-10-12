#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Where on a body a blow lands, in words anyone would use. The engine's body models name their
/// parts, by the tags on their hitboxes and by their bones; both come down to these places.
/// </summary>
public static class Places
{
	public const string Head = "head";
	public const string Body = "body";
	public const string LeftArm = "left arm";
	public const string RightArm = "right arm";
	public const string LeftLeg = "left leg";
	public const string RightLeg = "right leg";

	/// <summary>The kind of wound an arm has, either one: what a treatment names.</summary>
	public const string Arm = "arm";
	/// <summary>The kind of wound a leg has, either one.</summary>
	public const string Leg = "leg";

	/// <summary>Every place a blow can land.</summary>
	public static IReadOnlyList<string> All { get; } = new[] { Head, Body, LeftArm, RightArm, LeftLeg, RightLeg };

	/// <summary>The kind of wound at a place: an arm, a leg, or the place itself.</summary>
	public static string KindOf( string place ) => place switch
	{
		LeftArm or RightArm => Arm,
		LeftLeg or RightLeg => Leg,
		_ => place
	};

	/// <summary>
	/// The place a hitbox stands for, by the tags the engine's body models give it: the neck counts
	/// as the head, a shoulder or a hand as its arm, a foot as its leg. Anything else is the body.
	/// </summary>
	public static string OfTags( IEnumerable<string> tags )
	{
		var set = tags.Select( value => value.ToLowerInvariant() ).ToHashSet();
		if ( set.Contains( "head" ) ) return Head;
		var side = set.Contains( "left" ) ? "left" : set.Contains( "right" ) ? "right" : null;
		if ( side is null ) return Body;
		if ( set.Contains( "arm" ) || set.Contains( "hand" ) ) return $"{side} arm";
		if ( set.Contains( "leg" ) || set.Contains( "foot" ) ) return $"{side} leg";
		return Body;
	}

	/// <summary>The place a bone is part of, by the names the engine's body models give their bones, as a limp body is hit.</summary>
	public static string OfBone( string bone )
	{
		var name = bone.ToLowerInvariant();
		if ( name == "head" || name.StartsWith( "neck", StringComparison.Ordinal ) ) return Head;
		var side = name.EndsWith( "_l", StringComparison.Ordinal ) ? "left" : name.EndsWith( "_r", StringComparison.Ordinal ) ? "right" : null;
		if ( side is null ) return Body;
		if ( new[] { "arm", "hand", "clavicle", "finger", "thumb", "hold" }.Any( name.StartsWith ) ) return $"{side} arm";
		if ( new[] { "leg", "ankle", "ball", "toe", "foot" }.Any( name.StartsWith ) ) return $"{side} leg";
		return Body;
	}
}

/// <summary>
/// What a weapon leaves behind. A wound to an arm or a leg stays until someone treats it: nothing heals
/// by itself, and leaving the city does not either. A wounded leg limps. A wounded arm lets go of what
/// was in hand, and holds nothing until it is treated. Fists bruise; they leave no wounds.
/// </summary>
public static class Wounds
{
	public const string CannotHoldMessage = "Your arm will not hold anything until the wound is treated.";

	/// <summary>Whether a weapon's blow here leaves a wound: to an arm or a leg. Elsewhere it is health alone.</summary>
	public static bool Stays( string place ) => Places.KindOf( place ) is Places.Arm or Places.Leg;

	/// <summary>A wounded leg: they walk, slowly, and cannot run.</summary>
	public static bool Limps( CharacterData character ) => character.Wounds.Any( value => Places.KindOf( value ) == Places.Leg );

	/// <summary>A wounded arm: nothing can be taken in hand.</summary>
	public static bool CannotHold( CharacterData character ) => character.Wounds.Any( value => Places.KindOf( value ) == Places.Arm );

	/// <summary>The wounds as a person would list them: "a wounded left leg and right arm".</summary>
	public static string Describe( IReadOnlyCollection<string> wounds ) => wounds.Count switch
	{
		0 => "no wounds",
		1 => $"a wounded {wounds.First()}",
		_ => $"a wounded {string.Join( ", ", wounds.Take( wounds.Count - 1 ) )} and {wounds.Last()}"
	};
}
