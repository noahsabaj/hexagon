#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hexagon.Logic;

/// <summary>How a staff command is typed: words, with a "quoted name" kept as one.</summary>
public static class CommandLine
{
	/// <summary>Splits a typed line into words, keeping a "quoted name" as one.</summary>
	public static string[] Words( string line )
	{
		var words = new List<string>();
		var current = new StringBuilder();
		var quoted = false;
		foreach ( var character in line )
		{
			if ( character == '"' ) quoted = !quoted;
			else if ( character == ' ' && !quoted )
			{
				if ( current.Length > 0 ) words.Add( current.ToString() );
				current.Clear();
			}
			else current.Append( character );
		}
		if ( current.Length > 0 ) words.Add( current.ToString() );
		return words.ToArray();
	}

	/// <summary>The words as they would be typed, quoted where they need to be.</summary>
	public static string Line( IEnumerable<string> words ) =>
		string.Join( " ", words.Select( word => word.Length == 0 || word.Contains( ' ' ) ? $"\"{word}\"" : word ) );

	/// <summary>
	/// Takes up to the asked number of something, pile by pile, all of it when no number is asked.
	/// <paramref name="take"/> takes some of one pile and says whether it could; the first that cannot
	/// stops the rest. Returns how many went.
	/// </summary>
	public static int TakeFromPiles( string asked, IReadOnlyList<ItemStack> piles, Func<ItemStack, int, bool> take )
	{
		var wanted = int.TryParse( asked, out var count ) && count > 0 ? count : piles.Sum( pile => pile.Count );
		var removed = 0;
		foreach ( var pile in piles )
		{
			if ( removed >= wanted ) break;
			var some = Math.Min( wanted - removed, pile.Count );
			if ( !take( pile, some ) ) break;
			removed += some;
		}
		return removed;
	}
}
