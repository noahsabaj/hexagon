#nullable enable

using System;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// A lock opens to a key, not to a team. Every lock has a pattern and every key is cut to one, which
/// it carries wherever it goes; a key fits every lock with its pattern, so a ministry's doors can
/// share one while each flat has its own. What a player sees of a key is its cut, which says nothing
/// of the door it opens: two keys can be compared, and a key is tried.
/// </summary>
public static class Keys
{
	public const int MaximumPattern = 32;

	/// <summary>A pattern as staff type it, or an error saying what one is.</summary>
	public static Result<string> Pattern( string? text )
	{
		var pattern = (text ?? string.Empty).Trim().ToLowerInvariant();
		if ( pattern.Length is 0 or > MaximumPattern || !pattern.All( value => value is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_' ) )
			return Result<string>.Fail( ErrorCode.Invalid, $"A pattern is 1 to {MaximumPattern} letters, digits, dots, dashes or underscores." );
		return Result<string>.Success( pattern );
	}

	/// <summary>Whether a key cut to <paramref name="key"/> turns a lock with <paramref name="pattern"/>. A lock with no pattern has no keyhole.</summary>
	public static bool Fits( string? key, string pattern ) =>
		key is { Length: > 0 } && pattern.Length > 0 && string.Equals( key, pattern, StringComparison.OrdinalIgnoreCase );

	/// <summary>Whether anything in the inventory is a key that fits.</summary>
	public static bool Opens( InventoryData inventory, string pattern ) =>
		inventory.Items.Any( item => Fits( item.Pattern, pattern ) );

	/// <summary>
	/// What a player sees of a key cut to this pattern: five depths, the same for every key of the
	/// pattern on every machine. Two patterns can share a cut by chance; a lock still knows the difference.
	/// </summary>
	public static string Cut( string pattern )
	{
		// FNV-1a, because string.GetHashCode differs from one run to the next.
		var hash = 2166136261u;
		foreach ( var character in pattern.ToLowerInvariant() )
		{
			hash ^= character;
			hash *= 16777619u;
		}
		var depths = new int[5];
		for ( var index = 0; index < depths.Length; index++ )
		{
			depths[index] = (int)(hash % 7) + 1;
			hash /= 7;
		}
		return string.Join( "-", depths );
	}
}
