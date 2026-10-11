#nullable enable

using System;
using System.Linq;
using Sandbox;

namespace Hexagon;

/// <summary>
/// How the host puts something into the world at run time, such as a body, a dropped item or someone
/// left lying, so that only those who could see it learn what it is.
///
/// The engine tells every client that a new networked object exists, wherever it is, and keeps
/// sending it for a moment before it stops sending it to those who cannot see it. So what the thing
/// is never goes out with it. Clients whose characters can see the place are told at once, by an
/// RPC to them alone; everyone else is sent it as synced state once <see cref="HoldSeconds"/> have
/// passed, by which time the engine sends it only to those who come near.
/// </summary>
internal static class Reveal
{
	/// <summary>Longer than the engine keeps sending a new object to those who cannot see it.</summary>
	public const float HoldSeconds = 3f;

	/// <summary>Spawn options for something only those near enough are sent.</summary>
	public static NetworkSpawnOptions Options
	{
		get
		{
			var options = NetworkSpawnOptions.Default;
			// Only the host knows where everyone is, so it alone decides which clients are sent this.
			options.AlwaysTransmit = false;
			return options;
		}
	}

	/// <summary>Host: the clients whose characters can see this place now.</summary>
	public static Connection[] Witnesses( Vector3 at ) =>
		HexagonServices.Current is { } game
			? Connection.All.Where( value => !value.IsHost && game.Sees( value, at ) ).ToArray()
			: Array.Empty<Connection>();

	/// <summary>
	/// For <see cref="Component.INetworkVisible"/>: the host decides, by where it believes each
	/// character is. The host has everything.
	/// </summary>
	public static bool IsVisibleTo( Connection connection, Vector3 at ) =>
		!Networking.IsHost || connection.IsHost || HexagonServices.Current?.Sees( connection, at ) == true;
}
