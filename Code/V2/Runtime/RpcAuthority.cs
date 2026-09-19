#nullable enable

using System;
using System.Threading;
using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Networking;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Immutable host-derived RPC identity. AccountId always comes from Rpc.Caller;
/// player synchronization fields are presentation-only.
/// </summary>
public readonly record struct RpcActor(
	Connection Connection,
	AccountId AccountId,
	HexPlayerBody Player,
	CharacterRecord? Character,
	ClientSessionScope ClientScope,
	CommandSessionLease Session )
{
	public ConnectionEpoch ConnectionEpoch => Session.Connection;
	public long CharacterEpoch => Session.Character;
	public CancellationToken CancellationToken => Session.CancellationToken;
}
