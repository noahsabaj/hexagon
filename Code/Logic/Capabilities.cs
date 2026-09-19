#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>The capabilities Hexagon itself asks about. A game adds its own names in its assets.</summary>
public static class Capability
{
	public const string DoorLock = "door.lock";
	public const string Restrain = "person.restrain";
}

/// <summary>
/// What a character may do is one question with one answer, not a faction check scattered through
/// the code. Grants come from whatever the character is and holds: a faction, an item such as a
/// key. Denials come from what has been done to it, such as being restrained, and always win.
/// A grant ending in <c>.*</c> covers everything beneath it, and <c>*</c> covers everything.
/// </summary>
public static class Capabilities
{
	public static bool Can( string capability, IEnumerable<string> granted, IEnumerable<string>? denied = null )
	{
		if ( string.IsNullOrEmpty( capability ) ) return false;
		if ( denied?.Any( value => Covers( value, capability ) ) == true ) return false;
		return granted.Any( value => Covers( value, capability ) );
	}

	private static bool Covers( string grant, string capability )
	{
		if ( grant == "*" || grant.Equals( capability, StringComparison.Ordinal ) ) return true;
		return grant.EndsWith( ".*", StringComparison.Ordinal ) &&
			capability.StartsWith( grant[..^1], StringComparison.Ordinal );
	}
}
