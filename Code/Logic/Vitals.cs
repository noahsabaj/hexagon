#nullable enable

using System;

namespace Hexagon.Logic;

public enum Harm
{
	/// <summary>Nothing happened: the target was already down.</summary>
	None,
	Hurt,
	Downed
}

/// <summary>
/// What violence does to a character. Harm never kills: at no health a character goes down, and
/// stays part of the scene. It can be helped up, searched, or finished, and finishing is a separate,
/// deliberate act. What death then means is the game's policy, not decided here: by default it is
/// final.
/// The state lives in the character's document, so leaving the server does not escape it.
/// </summary>
public static class Vitals
{
	public const int Full = 100;
	/// <summary>The health of a character who has just been helped up.</summary>
	public const int Revived = 25;

	public static Harm Damage( CharacterData character, int amount )
	{
		if ( amount <= 0 || character.IsDown ) return Harm.None;
		character.Health = Math.Max( 0, character.Health - amount );
		if ( character.Health > 0 ) return Harm.Hurt;
		character.IsDown = true;
		character.SecondsDown = 0;
		return Harm.Downed;
	}

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

	public static Result Heal( CharacterData character, int amount )
	{
		if ( character.IsDown ) return Result.Fail( ErrorCode.Conflict, "That will not help someone who is down." );
		if ( character.Health >= Full ) return Result.Fail( ErrorCode.Conflict, "There is nothing to treat." );
		character.Health = Math.Min( Full, character.Health + Math.Max( 0, amount ) );
		return Result.Success();
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

	/// <summary>Staff undo a death that broke the rules: the character gets up, barely, as if helped up.</summary>
	public static Result Restore( CharacterData character )
	{
		if ( !character.IsDead ) return Result.Fail( ErrorCode.Conflict, $"{character.Name} is not dead." );
		character.DiedAt = null;
		character.Health = Revived;
		return Result.Success();
	}
}
