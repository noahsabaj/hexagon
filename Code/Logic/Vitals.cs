#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

public enum Harm
{
	/// <summary>Nothing happened: the target was already down, or the blow was nothing.</summary>
	None,
	Hurt,
	Downed,
	/// <summary>A weapon made to kill, to the head. Carrying out the death is the caller's.</summary>
	Killed
}

/// <summary>
/// A blow as it lands: how hard, where on the body (see <see cref="Places"/>), whether a weapon dealt
/// it, which wounds where a fist only bruises, and whether that weapon is made to kill.
/// </summary>
public readonly record struct Blow( int Amount, string Place = Places.Body, bool Wounding = false, bool Lethal = false );

/// <summary>What a blow did: the harm, and the wound it left, if any.</summary>
public readonly record struct Struck( Harm Harm, string? Wound );

/// <summary>What a treatment did: the wound it closed, if any, and the health it gave back.</summary>
public readonly record struct Treated( string? Wound, int Healed );

/// <summary>
/// What violence does to a character. Harm alone never kills: at no health a character goes down, and
/// stays part of the scene. It can be helped up, searched, or finished, and finishing is a separate,
/// deliberate act. The one blow that kills is a weapon made to kill, to the head. What death then
/// means is the game's policy, not decided here: by default it is final.
/// The state lives in the character's document, so leaving the server does not escape it, and wounds
/// stay in it until someone treats them: nothing heals by itself.
/// </summary>
public static class Vitals
{
	public const int Full = 100;
	/// <summary>The health of a character who has just been helped up.</summary>
	public const int Revived = 25;
	/// <summary>Below this a character is badly hurt, and breathes like it.</summary>
	public const int BadlyHurt = Full / 2;

	/// <summary>Harm to no place in particular, such as from staff: it wounds nothing.</summary>
	public static Harm Damage( CharacterData character, int amount ) => Strike( character, new Blow( amount ) ).Harm;

	public static Struck Strike( CharacterData character, Blow blow )
	{
		if ( blow.Amount <= 0 ) return new Struck( Harm.None, null );
		if ( blow.Lethal && blow.Place == Places.Head ) return new Struck( Harm.Killed, null );
		if ( character.IsDown ) return new Struck( Harm.None, null );
		string? wound = null;
		// A wound is there or it is not: a second shot to a wounded leg makes it no more wounded.
		if ( blow.Wounding && Wounds.Stays( blow.Place ) && !character.Wounds.Contains( blow.Place ) )
		{
			wound = blow.Place;
			character.Wounds.Add( wound );
		}
		character.Health = Math.Max( 0, character.Health - blow.Amount );
		if ( character.Health > 0 ) return new Struck( Harm.Hurt, wound );
		character.IsDown = true;
		character.SecondsDown = 0;
		return new Struck( Harm.Downed, wound );
	}

	/// <summary>Whether the character breathes hard, as anyone close can hear: badly hurt, or down.</summary>
	public static bool BreathesHard( CharacterData character ) =>
		!character.IsDead && (character.IsDown || character.Health < BadlyHurt);

	/// <summary>
	/// Time passes for someone who is down, whether or not their player is still connected. True once
	/// they have been down for <paramref name="limit"/> seconds of the city running; a limit of zero
	/// or less never ends it.
	/// </summary>
	public static bool Bleed( CharacterData character, double seconds, double limit )
	{
		if ( !character.IsDown ) return false;
		character.SecondsDown += Math.Max( 0, seconds );
		return limit > 0 && character.SecondsDown >= limit;
	}

	/// <summary>
	/// Treats a character with something that closes one wound of the kinds it names, the oldest
	/// first, and gives back up to <paramref name="heals"/> health. When it would do neither, it fails,
	/// so nothing is used up for nothing.
	/// </summary>
	public static Result<Treated> Treat( CharacterData character, IEnumerable<string> treats, int heals )
	{
		if ( character.IsDown ) return Result<Treated>.Fail( ErrorCode.Conflict, "That will not help someone who is down." );
		var kinds = treats.ToHashSet();
		var wound = character.Wounds.FirstOrDefault( value => kinds.Contains( Places.KindOf( value ) ) || kinds.Contains( value ) );
		var healed = Math.Clamp( heals, 0, Math.Max( 0, Full - character.Health ) );
		if ( wound is null && healed == 0 ) return Result<Treated>.Fail( ErrorCode.Conflict, "There is nothing to treat." );
		if ( wound is not null ) character.Wounds.Remove( wound );
		character.Health += healed;
		return Result<Treated>.Success( new Treated( wound, healed ) );
	}

	public static Result Revive( CharacterData character )
	{
		if ( !character.IsDown ) return Result.Fail( ErrorCode.Conflict, "They are not down." );
		character.IsDown = false;
		character.SecondsDown = 0;
		character.Health = Revived;
		return Result.Success();
	}

	/// <summary>Only someone who is down can be finished.</summary>
	public static Result CanFinish( CharacterData character ) =>
		character.IsDown ? Result.Success() : Result.Fail( ErrorCode.Conflict, "They are still standing." );

	/// <summary>A character returns whole: after death under a respawn policy.</summary>
	public static void Respawn( CharacterData character )
	{
		character.Health = Full;
		character.IsDown = false;
		character.SecondsDown = 0;
		character.IsRestrained = false;
		character.Wounds.Clear();
	}

	/// <summary>The character is dead, or retired. It is kept, so staff can restore it if the death broke the rules.</summary>
	public static void Die( CharacterData character, DateTimeOffset now )
	{
		character.DiedAt = now;
		character.Health = 0;
		character.IsDown = false;
		character.SecondsDown = 0;
		character.IsRestrained = false;
		character.Equipped = null;
	}

	/// <summary>Staff undo a death that broke the rules: the character gets up, barely, as if helped up, wounds and all.</summary>
	public static Result Restore( CharacterData character )
	{
		if ( !character.IsDead ) return Result.Fail( ErrorCode.Conflict, $"{character.Name} is not dead." );
		character.DiedAt = null;
		character.Health = Revived;
		return Result.Success();
	}
}
