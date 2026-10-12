#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hexagon.Logic;

/// <summary>
/// Writing on paper. Ink stays: a line, once written, is there for good, and more go below it until
/// the sheet is full. Each blank on a printed form is filled in once. A stamp goes on top of whatever
/// is written by then, so a line below it was written after. Nothing on a sheet says who wrote it:
/// anyone can put any name at the bottom of a letter. The journal knows whose hand it was; a reader
/// has only the paper, and a stamp, which takes the stamp itself, is what makes it official.
/// </summary>
public static class Papers
{
	/// <summary>The longest line a sheet takes, in letters.</summary>
	public const int LineLength = 80;
	/// <summary>The most a blank on a form takes, in letters.</summary>
	public const int FieldLength = 48;
	/// <summary>How many stamps fit on one sheet.</summary>
	public const int MaximumStamps = 6;

	public const string NothingToWriteWith = "You have nothing to write with.";

	/// <summary>Words as they go on paper: one line, its spacing tidied, nothing in it that is not a letter, a mark or a space.</summary>
	public static string Clean( string? text )
	{
		var cleaned = new StringBuilder();
		var space = false;
		foreach ( var character in text ?? string.Empty )
		{
			if ( char.IsWhiteSpace( character ) || char.IsControl( character ) )
			{
				space = cleaned.Length > 0;
				continue;
			}
			if ( space ) cleaned.Append( ' ' );
			space = false;
			cleaned.Append( character );
		}
		return cleaned.ToString();
	}

	/// <summary>
	/// Writes a line below whatever is on the sheet, which takes <paramref name="lines"/> lines in all.
	/// One sheet is written on at a time, never a pile, and a written sheet piles with nothing.
	/// </summary>
	public static Result Write( ItemStack sheet, int lines, string? text )
	{
		var line = Clean( text );
		if ( lines <= 0 ) return Result.Fail( ErrorCode.Invalid, "There is nothing to write on." );
		if ( line.Length == 0 ) return Result.Fail( ErrorCode.Invalid, "Write something." );
		if ( line.Length > LineLength ) return Result.Fail( ErrorCode.Invalid, $"A line takes {LineLength} letters at most." );
		if ( sheet.Count != 1 ) return Result.Fail( ErrorCode.Conflict, "Take one sheet off the pile first." );
		if ( sheet.Paper is { } written && written.Lines.Count >= lines ) return Result.Fail( ErrorCode.Conflict, "There is no room left on it." );
		Mark( sheet ).Lines.Add( line );
		return Result.Success();
	}

	/// <summary>Fills in a blank printed on a form, among its <paramref name="fields"/>. A blank is filled once: ink stays.</summary>
	public static Result Fill( ItemStack sheet, IReadOnlyList<string> fields, string? field, string? text )
	{
		var label = fields.FirstOrDefault( value => string.Equals( value, field, StringComparison.OrdinalIgnoreCase ) );
		var value = Clean( text );
		if ( label is null ) return Result.Fail( ErrorCode.Invalid, "There is no such blank on it." );
		if ( value.Length == 0 ) return Result.Fail( ErrorCode.Invalid, "Write something." );
		if ( value.Length > FieldLength ) return Result.Fail( ErrorCode.Invalid, $"A blank takes {FieldLength} letters at most." );
		if ( sheet.Count != 1 ) return Result.Fail( ErrorCode.Conflict, "Take one sheet off the pile first." );
		if ( sheet.Paper?.Fields.ContainsKey( label ) == true ) return Result.Fail( ErrorCode.Conflict, $"The {label} is already filled in." );
		Mark( sheet ).Fields[label] = value;
		return Result.Success();
	}

	/// <summary>Puts a stamp's mark on the sheet, on top of what is written so far. <paramref name="stamp"/> is the stamp's item.</summary>
	public static Result Stamp( ItemStack sheet, string stamp )
	{
		if ( stamp.Length == 0 ) return Result.Fail( ErrorCode.Invalid, "That is not a stamp." );
		if ( sheet.Count != 1 ) return Result.Fail( ErrorCode.Conflict, "Take one sheet off the pile first." );
		if ( sheet.Paper is { } stamped && stamped.Stamps.Count >= MaximumStamps ) return Result.Fail( ErrorCode.Conflict, "There is no room left on it for another stamp." );
		var paper = Mark( sheet );
		paper.Stamps.Add( new StampMark { Stamp = stamp, AfterLine = paper.Lines.Count } );
		return Result.Success();
	}

	/// <summary>
	/// Why a sheet cannot be held up for someone to read now, or null when it can: they must be there,
	/// close enough to read it, and awake to it. Someone bound can still read what is held in front of them.
	/// </summary>
	public static string? ToShow( bool holderIncapable, bool readerThere, float distance, float reach, bool readerDown ) =>
		holderIncapable ? "You cannot do that now."
		: !readerThere ? "There is nobody there."
		: distance > reach ? "You are too far away."
		: readerDown ? "They are in no state to read it."
		: null;

	/// <summary>A sheet with something on it is one of a kind: it piles with nothing, not even another like it.</summary>
	private static PaperData Mark( ItemStack sheet )
	{
		sheet.Max = 1;
		return sheet.Paper ??= new PaperData();
	}
}
