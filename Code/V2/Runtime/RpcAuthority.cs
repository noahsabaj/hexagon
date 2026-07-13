#nullable enable

using System;
using System.Threading;
using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Kernel;
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
	CommandSessionLease Session )
{
	public ConnectionEpoch ConnectionEpoch => Session.Connection;
	public long CharacterEpoch => Session.Character;
	public CancellationToken CancellationToken => Session.CancellationToken;
}

public static class RpcGuard
{
	public static OperationResult<RpcActor> Resolve(
		HexagonRuntimeSystem runtime,
		bool requiresStableCharacter )
	{
		if ( runtime is null )
			return OperationResult<RpcActor>.Failure( ErrorCode.InternalError, "Hexagon runtime is unavailable." );
		var caller = Rpc.Caller;
		if ( caller is null || caller.SteamId.ValueUnsigned == 0 )
			return OperationResult<RpcActor>.Failure( ErrorCode.Unauthorized, "RPC caller is not authenticated." );
		return runtime.ResolveActor( caller, requiresStableCharacter );
	}

	public static OperationResult RequireActiveCharacter( RpcActor actor ) => actor.Character is null
		? OperationResult.Failure( ErrorCode.Unauthorized, "An active character is required." )
		: OperationResult.Success();
}
