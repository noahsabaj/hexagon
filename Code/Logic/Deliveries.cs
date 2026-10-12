#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Raw materials enter the world two ways besides staff and what the world was built holding: by
/// train, on a schedule, and from sites, which yield slowly to whoever works them. A train brings the
/// same goods each time, at set times of day on the server's clock, waits at its station a while,
/// and leaves with whatever is still on board and whatever was loaded for it. A train missed while
/// the server was down never comes: nothing stacks up.
/// </summary>
public static class Deliveries
{
	/// <summary>A time of day as an author writes it, such as "06:00".</summary>
	public static Result<TimeSpan> TimeOfDay( string? text )
	{
		if ( TimeSpan.TryParseExact( text?.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time ) && time < TimeSpan.FromDays( 1 ) )
			return Result<TimeSpan>.Success( time );
		return Result<TimeSpan>.Fail( ErrorCode.Invalid, $"'{text}' is not a time of day, such as 06:00." );
	}

	/// <summary>The first of the times of day after <paramref name="after"/>, that day or the next, or null when there are none.</summary>
	public static DateTime? Next( IEnumerable<TimeSpan> times, DateTime after )
	{
		DateTime? next = null;
		foreach ( var time in times )
		{
			var at = after.Date + time;
			if ( at <= after ) at = at.AddDays( 1 );
			if ( next is null || at < next ) next = at;
		}
		return next;
	}

	/// <summary>Goods as one line each, such as "Ration x20", the same kind added together, in the order first met.</summary>
	public static IReadOnlyList<string> Lines( IEnumerable<(string Title, int Count)> goods ) =>
		goods.GroupBy( value => value.Title ).Select( group => $"{group.Key} x{group.Sum( value => value.Count )}" ).ToList();

	/// <summary>Goods on one line, for the record: "Ration x20, Water x20", or "nothing".</summary>
	public static string Summary( IEnumerable<(string Title, int Count)> goods ) =>
		Lines( goods ) is { Count: > 0 } lines ? string.Join( ", ", lines ) : "nothing";
}

/// <summary>
/// A site in the world, such as a mine, a farm or a scrap yard, holds a stock that grows back over time
/// up to the most it holds. Each piece of work takes some out, and an empty site yields nothing until it
/// has grown back.
/// </summary>
public static class Sites
{
	public const string Empty = "There is nothing left to take here. It grows back in time.";

	/// <summary>What a site holds now: what it held, grown back since at its rate, up to its most.</summary>
	public static double Grown( double held, DateTime since, DateTime now, double perHour, int most ) =>
		Math.Min( most, held + Math.Max( 0d, (now - since).TotalHours ) * Math.Max( 0d, perHour ) );

	/// <summary>How many whole things one piece of work takes out of what a site holds.</summary>
	public static int Take( double held, int perWork ) => Math.Max( 0, Math.Min( perWork, (int)Math.Floor( held ) ) );
}
