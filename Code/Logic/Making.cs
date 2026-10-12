#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// What something is made from, what it makes, and how long the work takes: a press striking tokens
/// from blanks, a die striking one by hand. Its inputs and output are item assets, by path.
/// <paramref name="Serial"/> numbers each thing it makes, as a banknote is. <paramref name="Kept"/> is
/// what it is worked from without being used up, such as the key a copy is cut from, and
/// <paramref name="Also"/> what else the same work makes, such as the keys that come with a lock;
/// <paramref name="Cut"/> says what the keys and locks it makes are cut to.
/// </summary>
public sealed record Recipe( string Title, IReadOnlyList<(string Definition, int Count)> Inputs, string Output, int Count, float Seconds, bool Serial = false,
	IReadOnlyList<(string Definition, int Count)>? Kept = null, IReadOnlyList<(string Definition, int Count)>? Also = null, MakingCut Cut = MakingCut.None );

/// <summary>What the keys and locks a piece of work makes are cut to.</summary>
public enum MakingCut
{
	/// <summary>Nothing: a key made so is a blank.</summary>
	None,
	/// <summary>A pattern nobody has cut before, the same for everything the work makes: a lock, and the keys that come with it.</summary>
	New,
	/// <summary>The pattern of what the work is worked from: a key copied, a key cut to fit a lock.</summary>
	Copied,
}

/// <summary>
/// Money is made by someone, from something. Whatever makes a thing leaves its mark on it, a press its
/// own and a die its own, and a banknote is printed with a serial besides. The mark never shows: a
/// trained eye with the right tool can tell only whether two things were made alike, never by what.
/// Whoever wants to know which notes are theirs keeps one to compare against, and the register of the
/// serials they issued; the game keeps neither for them.
/// </summary>
public static class Making
{
	/// <summary>How many digits a serial's number has, after its prefix.</summary>
	public const int SerialDigits = 7;

	public const string NothingToMakeFrom = "There is nothing here to make anything from.";
	public const string NoRoom = "There is no room for what it makes.";
	public const string NothingToCopy = "There is nothing here cut to a lock, to copy.";

	/// <summary>The serial printed on the <paramref name="number"/>th thing a press numbers: its prefix, then the number.</summary>
	public static string Serial( string prefix, long number ) => $"{prefix}{number.ToString().PadLeft( SerialDigits, '0' )}";

	/// <summary>
	/// The marks on <paramref name="count"/> things made by <paramref name="maker"/>: numbered from
	/// <paramref name="firstSerial"/> when the press numbers what it makes, and otherwise not.
	/// </summary>
	public static List<MadeMark> Marks( string maker, int count, string? serialPrefix = null, long firstSerial = 0 ) =>
		Enumerable.Range( 0, count )
			.Select( index => new MadeMark { By = maker, Serial = serialPrefix is null ? string.Empty : Serial( serialPrefix, firstSerial + index ) } )
			.ToList();

	/// <summary>Whether there is enough in the inventory to make it: what it uses up, and what it is worked from.</summary>
	public static bool Enough( Recipe recipe, InventoryData inventory ) =>
		recipe.Inputs.Concat( recipe.Kept ?? Array.Empty<(string, int)>() )
			.GroupBy( need => need.Item1 )
			.All( need => Has( inventory, need.Key ) >= need.Sum( value => value.Item2 ) );

	/// <summary>
	/// What the keys and locks a recipe makes are cut to, from what is there to make them: nothing, a
	/// new pattern, or the pattern of the first thing there it is worked from that is cut to one.
	/// </summary>
	public static Result<string?> Cut( Recipe recipe, InventoryData inventory )
	{
		if ( recipe.Cut == MakingCut.New ) return Result<string?>.Success( Keys.NewPattern() );
		if ( recipe.Cut != MakingCut.Copied ) return Result<string?>.Success( null );
		var model = (recipe.Kept ?? Array.Empty<(string Definition, int Count)>())
			.SelectMany( kept => inventory.Items.Where( item => !item.Worn && item.Definition == kept.Definition && item.Pattern is { Length: > 0 } ) )
			.FirstOrDefault();
		return model is null ? Result<string?>.Fail( ErrorCode.NotFound, NothingToCopy ) : Result<string?>.Success( model.Pattern );
	}

	/// <summary>How many of a thing an inventory holds, worn or carried loose.</summary>
	public static int Has( InventoryData inventory, string definition ) =>
		inventory.Items.Where( item => !item.Worn && item.Definition == definition ).Sum( item => item.Count );

	/// <summary>
	/// The marks of the top <paramref name="count"/> things of a pile, the last on top, as they would be
	/// taken off it. Null when nothing in the pile was made with a mark.
	/// </summary>
	public static List<MadeMark>? Top( ItemStack pile, int count )
	{
		if ( pile.Made is null ) return null;
		var marks = Filled( pile );
		return marks.Skip( Math.Max( 0, marks.Count - count ) ).Select( mark => mark.Copy() ).ToList();
	}

	/// <summary>Takes the marks of the top <paramref name="count"/> things off a pile as they leave it, before its count goes down.</summary>
	public static void TakeTop( ItemStack pile, int count )
	{
		if ( pile.Made is null ) return;
		var marks = Filled( pile );
		marks.RemoveRange( Math.Max( 0, marks.Count - count ), Math.Min( count, marks.Count ) );
		pile.Made = marks;
	}

	/// <summary>
	/// Puts the marks of <paramref name="count"/> things onto the top of a pile as they join it, before its
	/// count goes up. Things made without a mark pile with those made with one, and look no different.
	/// </summary>
	public static void PutOnTop( ItemStack pile, IReadOnlyList<MadeMark>? marks, int count )
	{
		if ( marks is null && pile.Made is null ) return;
		var into = Filled( pile );
		for ( var index = 0; index < count; index++ ) into.Add( marks is not null && index < marks.Count ? marks[index].Copy() : new MadeMark() );
		pile.Made = into;
	}

	/// <summary>A pile's marks, one for each thing in it, those made without a mark blank and at the bottom.</summary>
	private static List<MadeMark> Filled( ItemStack pile )
	{
		var marks = pile.Made?.Select( mark => mark.Copy() ).ToList() ?? new List<MadeMark>();
		// Before anything was taken off or put on through here, a pile counted without its marks counts first.
		var blank = Math.Max( 0, pile.Count - marks.Count );
		marks.InsertRange( 0, Enumerable.Range( 0, blank ).Select( _ => new MadeMark() ) );
		return marks;
	}

	/// <summary>
	/// What a trained eye makes of a pile, held under the right tool: whether its things were made
	/// alike, and if not, which go together, by their serials where they have them. Never what made them.
	/// </summary>
	public static string Compare( ItemStack pile )
	{
		if ( pile.Count < 2 ) return "There is only one, with nothing to compare it to.";
		var marks = Filled( pile );
		var groups = marks.GroupBy( mark => mark.By ).ToList();
		if ( groups.Count == 1 ) return $"All {pile.Count} were made alike.";
		var described = groups.Select( ( group, index ) => $"{Described( group.ToList() )} {Way( index )}" );
		return $"They were made {groups.Count} ways: {string.Join( "; ", described )}.";
	}

	// The things made one way: their serials where they all have one, or else how many.
	private static string Described( IReadOnlyList<MadeMark> marks )
	{
		if ( marks.Any( mark => mark.Serial.Length == 0 ) ) return marks.Count.ToString();
		const int listed = 5;
		var serials = string.Join( ", ", marks.Take( listed ).Select( mark => mark.Serial ) );
		return marks.Count > listed ? $"{serials} and {marks.Count - listed} more" : serials;
	}

	private static string Way( int index ) => index switch
	{
		0 => "one way",
		1 => "another",
		2 => "a third",
		3 => "a fourth",
		4 => "a fifth",
		_ => $"a {index + 1}th"
	};
}
