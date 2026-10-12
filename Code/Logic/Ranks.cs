#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// One rank of a faction, as its asset authors it, and what someone holding it may do to those below
/// them. <paramref name="PromotesTo"/> is the highest rank it may give, by its place in the faction's
/// list, or -1 for any rank below its own.
/// </summary>
public sealed record RankRule( string Title, bool Enlists = false, bool Promotes = false, int PromotesTo = -1, bool Discharges = false );

/// <summary>
/// Rank is given by a superior's act, in person, and lives on the character. A faction lists its ranks
/// lowest first, and each says what someone holding it may do: enlist a newcomer at the lowest rank,
/// give someone below them a rank below their own, up to a limit, or discharge them. Nobody acts on
/// someone of their own rank or above it. Staff appoint a faction's head once; everyone below is
/// enlisted and promoted in the world. A rank grants nothing else: what a member can do still comes
/// from what they carry.
/// </summary>
public static class Ranks
{
	public const string NoPower = "Your rank does not let you do that.";
	public const string NotUnder = "They do not serve under you.";
	public const string NoSuchRank = "There is no such rank.";

	/// <summary>Where a rank stands in its faction, lowest first, or -1 when the faction has none of that title.</summary>
	public static int IndexOf( IReadOnlyList<RankRule> ranks, string? title )
	{
		for ( var index = 0; index < ranks.Count; index++ )
			if ( string.Equals( ranks[index].Title, title, StringComparison.OrdinalIgnoreCase ) ) return index;
		return -1;
	}

	/// <summary>
	/// Where a member stands. A member whose rank the faction no longer has stands at the bottom, so a
	/// superior can set it right; one whose faction has no ranks stands nowhere.
	/// </summary>
	public static int Standing( IReadOnlyList<RankRule> ranks, string? title ) => ranks.Count == 0 ? -1 : Math.Max( 0, IndexOf( ranks, title ) );

	/// <summary>Whether someone standing at <paramref name="actor"/> may enlist anyone.</summary>
	public static bool CanEnlist( IReadOnlyList<RankRule> ranks, int actor ) => actor >= 0 && actor < ranks.Count && ranks[actor].Enlists;

	/// <summary>Whether someone standing at <paramref name="actor"/> may discharge anyone.</summary>
	public static bool CanDischarge( IReadOnlyList<RankRule> ranks, int actor ) => actor >= 0 && actor < ranks.Count && ranks[actor].Discharges;

	/// <summary>The ranks someone standing at <paramref name="actor"/> may give, lowest first: below their own, up to their limit.</summary>
	public static IReadOnlyList<int> Giveable( IReadOnlyList<RankRule> ranks, int actor )
	{
		if ( actor < 0 || actor >= ranks.Count || !ranks[actor].Promotes ) return Array.Empty<int>();
		var top = ranks[actor].PromotesTo >= 0 ? Math.Min( ranks[actor].PromotesTo, actor - 1 ) : actor - 1;
		return Enumerable.Range( 0, Math.Max( 0, top + 1 ) ).ToArray();
	}

	/// <summary>Whether someone standing at <paramref name="actor"/> may enlist a recruit, who must belong to no faction yet. A recruit joins at the lowest rank.</summary>
	public static Result ToEnlist( IReadOnlyList<RankRule> ranks, int actor, bool recruitBelongs ) =>
		ranks.Count == 0 ? Result.Fail( ErrorCode.Invalid, "Your faction has no ranks to enlist anyone at." )
		: !CanEnlist( ranks, actor ) ? Result.Fail( ErrorCode.Denied, NoPower )
		: recruitBelongs ? Result.Fail( ErrorCode.Conflict, "They already belong to a faction." )
		: Result.Success();

	/// <summary>
	/// Whether someone standing at <paramref name="actor"/> may give a member of their own faction,
	/// standing at <paramref name="member"/>, the rank at <paramref name="to"/>.
	/// </summary>
	public static Result ToSetRank( IReadOnlyList<RankRule> ranks, int actor, bool sameFaction, int member, int to )
	{
		if ( Giveable( ranks, actor ) is not { Count: > 0 } giveable ) return Result.Fail( ErrorCode.Denied, NoPower );
		if ( !sameFaction || member < 0 || member >= actor ) return Result.Fail( ErrorCode.Denied, NotUnder );
		if ( to < 0 || to >= ranks.Count ) return Result.Fail( ErrorCode.Invalid, NoSuchRank );
		if ( !giveable.Contains( to ) ) return Result.Fail( ErrorCode.Denied, "You cannot give that rank." );
		if ( to == member ) return Result.Fail( ErrorCode.Conflict, "They already hold that rank." );
		return Result.Success();
	}

	// A rank whose first letter is a vowel can still begin with the sound of a consonant: a Unit, a
	// Eurasian, a One-Star.
	private static readonly string[] ConsonantSounds = { "uni", "use", "usu", "uti", "ure", "eu", "one" };

	/// <summary>A rank as it is said of one person: "a Unit", "an Officer".</summary>
	public static string Article( string rank, bool capital = false )
	{
		var lower = rank.ToLowerInvariant();
		var vowel = lower.Length > 0 && "aeiou".Contains( lower[0] ) && !ConsonantSounds.Any( lower.StartsWith );
		return $"{(capital ? "A" : "a")}{(vowel ? "n" : "")} {rank}";
	}

	/// <summary>Whether someone standing at <paramref name="actor"/> may discharge a member of their own faction standing at <paramref name="member"/>.</summary>
	public static Result ToDischarge( IReadOnlyList<RankRule> ranks, int actor, bool sameFaction, int member ) =>
		!CanDischarge( ranks, actor ) ? Result.Fail( ErrorCode.Denied, NoPower )
		: !sameFaction || member < 0 || member >= actor ? Result.Fail( ErrorCode.Denied, NotUnder )
		: Result.Success();
}
