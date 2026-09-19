#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Staff work in game through the same commands as the server console, as themselves. Every line
// they type is journaled before it runs, allowed or not, so what staff did, and what they looked
// at, can always be answered later.
public sealed partial class Player
{
	/// <summary>Owner-side.</summary>
	public bool IsStaff { get; private set; }
	public IReadOnlyList<string> StaffLines { get; private set; } = Array.Empty<string>();

	/// <summary>Host: tells the owner what its account may do.</summary>
	public void HostSendAccount()
	{
		if ( !Networking.IsHost || Network.Owner is not { } owner || GameManager.Instance is not { } game ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveAccount( game.Account( SteamIdOf( owner ) ).IsStaff );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveAccount( bool staff )
	{
		IsStaff = staff;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveStaffLines( string[] lines )
	{
		StaffLines = lines;
		PrivateVersion++;
	}

	[Rpc.Host]
	public void RequestStaff( string line )
	{
		if ( !Authorize( out var caller, out var game ) ) return;
		line = (line ?? string.Empty).Trim();
		if ( line.Length is 0 or > 256 ) return;
		var steamId = SteamIdOf( caller );
		var allowed = game.Account( steamId ).IsStaff;
		var by = _character is not null ? Actor.Of( _character ) : new Actor( steamId, Name: caller.DisplayName );
		game.Journal!.Record( "staff.command", by, ok: allowed, data: ("line", line) );
		var reply = allowed ? Operators.Execute( game, by, this, Operators.Words( line ) ) : new[] { "You are not staff." };
		using ( Rpc.FilterInclude( caller ) ) ReceiveStaffLines( reply.ToArray() );
	}
}
