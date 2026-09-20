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
/// deliberate act. What death then means is the game's policy, not decided here.
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
		return Harm.Downed;
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
		character.Health = Revived;
		return Result.Success();
	}

	/// <summary>Only someone who is down can be finished.</summary>
	public static Result CanFinish( CharacterData character ) =>
		character.IsDown ? Result.Success() : Result.Fail( ErrorCode.Conflict, "They are still standing." );

	/// <summary>A character returns whole: after death under a respawn policy.</summary>
	public static void Restore( CharacterData character )
	{
		character.Health = Full;
		character.IsDown = false;
		character.IsRestrained = false;
	}
}
