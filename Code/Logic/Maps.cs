#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Planning on a map laid out on a table. Lines and short words go on in grease pencil, drawn with
/// something to write with, and anyone at the table can wipe one off again. The counters that come
/// with a map are moved by hand, onto it and back into their box. As on paper, nothing on a map says
/// whose hand put it there: the journal knows.
/// </summary>
public static class Maps
{
	/// <summary>A place on a map is in thousandths of its width and height, from its top left corner as printed.</summary>
	public const int Scale = 1000;
	/// <summary>How many lines and words a map takes before something must be wiped off.</summary>
	public const int MaximumMarks = 48;
	/// <summary>How many points one line keeps.</summary>
	public const int MaximumPoints = 64;
	/// <summary>The longest word written on a map, in letters.</summary>
	public const int WordLength = 24;

	public const string NotOnTheMap = "That is not on the map.";
	public const string Crowded = "There is no room left on it. Wipe something off first.";

	/// <summary>
	/// A line's points as a client sends them: x and y, one after another, separated by commas or
	/// semicolons. Anything that is not a place on the map is refused, and the whole line with it.
	/// </summary>
	public static Result<List<int>> Points( string? text )
	{
		var parts = (text ?? string.Empty).Split( new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 4 || parts.Length % 2 != 0 ) return Result<List<int>>.Fail( ErrorCode.Invalid, "A line runs from one place to another." );
		if ( parts.Length / 2 > MaximumPoints ) return Result<List<int>>.Fail( ErrorCode.Invalid, $"A line keeps {MaximumPoints} points at most." );
		var points = new List<int>( parts.Length );
		foreach ( var part in parts )
		{
			if ( !int.TryParse( part, NumberStyles.None, CultureInfo.InvariantCulture, out var value ) || value > Scale )
				return Result<List<int>>.Fail( ErrorCode.Invalid, NotOnTheMap );
			points.Add( value );
		}
		return Result<List<int>>.Success( points );
	}

	/// <summary>Draws a line through <paramref name="points"/>, x then y, in grease pencil the colour of <paramref name="ink"/>.</summary>
	public static Result Draw( ItemStack map, IReadOnlyList<int> points, string ink )
	{
		if ( points.Count < 4 || points.Count % 2 != 0 ) return Result.Fail( ErrorCode.Invalid, "A line runs from one place to another." );
		if ( points.Count / 2 > MaximumPoints ) return Result.Fail( ErrorCode.Invalid, $"A line keeps {MaximumPoints} points at most." );
		if ( points.Any( value => value < 0 || value > Scale ) ) return Result.Fail( ErrorCode.Invalid, NotOnTheMap );
		if ( map.Map is { Marks.Count: >= MaximumMarks } ) return Result.Fail( ErrorCode.Conflict, Crowded );
		Add( map, new MapMark { Points = points.ToList(), Ink = ink } );
		return Result.Success();
	}

	/// <summary>Writes a word on the map, starting at a place, in grease pencil the colour of <paramref name="ink"/>.</summary>
	public static Result Write( ItemStack map, int x, int y, string? text, string ink )
	{
		var word = Papers.Clean( text );
		if ( word.Length == 0 ) return Result.Fail( ErrorCode.Invalid, "Write something." );
		if ( word.Length > WordLength ) return Result.Fail( ErrorCode.Invalid, $"A word on a map takes {WordLength} letters at most." );
		if ( !On( x, y ) ) return Result.Fail( ErrorCode.Invalid, NotOnTheMap );
		if ( map.Map is { Marks.Count: >= MaximumMarks } ) return Result.Fail( ErrorCode.Conflict, Crowded );
		Add( map, new MapMark { Points = new List<int> { x, y }, Text = word, Ink = ink } );
		return Result.Success();
	}

	/// <summary>Wipes a line or a word off the map. Grease pencil leaves nothing behind.</summary>
	public static Result Wipe( ItemStack map, int mark )
	{
		if ( map.Map?.Marks.FirstOrDefault( value => value.Id == mark ) is not { } wiped ) return Result.Fail( ErrorCode.NotFound, "That is not on it any more." );
		map.Map.Marks.Remove( wiped );
		return Result.Success();
	}

	/// <summary>
	/// Stands one of the map's <paramref name="counters"/> on it at a place, or puts it back in its box
	/// beside the map when <paramref name="at"/> is null.
	/// </summary>
	public static Result Move( ItemStack map, int counters, int number, (int X, int Y)? at )
	{
		if ( number < 0 || number >= counters ) return Result.Fail( ErrorCode.Invalid, "There is no such counter." );
		if ( at is { } place && !On( place.X, place.Y ) ) return Result.Fail( ErrorCode.Invalid, NotOnTheMap );
		var data = Marked( map );
		data.Counters.RemoveAll( value => value.Number == number );
		if ( at is { } standing ) data.Counters.Add( new MapCounter { Number = number, X = standing.X, Y = standing.Y } );
		return Result.Success();
	}

	private static bool On( int x, int y ) => x is >= 0 and <= Scale && y is >= 0 and <= Scale;

	private static void Add( ItemStack map, MapMark mark )
	{
		var data = Marked( map );
		mark.Id = data.NextMark++;
		data.Marks.Add( mark );
	}

	/// <summary>A map with anything on it is one of a kind: it piles with nothing.</summary>
	private static MapData Marked( ItemStack map )
	{
		map.Max = 1;
		return map.Map ??= new MapData();
	}
}
