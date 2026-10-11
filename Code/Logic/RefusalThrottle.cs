#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Some refusals cost a client nothing to cause: a request made on someone else's pawn, one over
/// the rate limit. They are journaled like every other refusal, but a flood of them must not fill
/// the disk. Each caller gets at most one entry per kind per interval; the rest are counted, and the
/// count goes into the next entry, or is reported by <see cref="Due"/> once the flood stops.
/// </summary>
public sealed class RefusalThrottle
{
	private readonly double _interval;
	private readonly Dictionary<(long Account, string Kind), (double At, int Held)> _recent = new();

	public RefusalThrottle( double interval = 1.0 ) => _interval = interval;

	/// <summary>True when this refusal is to be written now. <paramref name="held"/> is how many like it were not.</summary>
	public bool Admit( long account, string kind, double now, out int held )
	{
		var found = _recent.TryGetValue( (account, kind), out var last );
		if ( found && now - last.At < _interval )
		{
			_recent[(account, kind)] = (last.At, last.Held + 1);
			held = 0;
			return false;
		}
		held = found ? last.Held : 0;
		_recent[(account, kind)] = (now, 0);
		return true;
	}

	/// <summary>The counts held back by floods that have since stopped, each reported once. Callers who have gone quiet are forgotten.</summary>
	public IReadOnlyList<(long Account, string Kind, int Held)> Due( double now )
	{
		var due = new List<(long Account, string Kind, int Held)>();
		foreach ( var (key, value) in _recent.Where( pair => now - pair.Value.At >= _interval ).ToArray() )
		{
			if ( value.Held > 0 ) due.Add( (key.Account, key.Kind, value.Held) );
			_recent.Remove( key );
		}
		return due;
	}
}
