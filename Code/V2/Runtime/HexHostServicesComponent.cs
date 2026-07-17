#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Sandbox;
using ClientInventorySnapshot = Hexagon.V2.Networking.InventorySnapshot;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Single host-owned RPC surface for framework commands. Every entry point
/// derives its actor through RpcGuard before dispatching client intent.
/// </summary>
public sealed class HexHostServicesComponent : Component, IHexHostTransport
{
	internal HexagonRuntimeSystem? Runtime { get; set; }

	[Rpc.Host]
	public void ReportClientBootstrapDiagnostic(
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code,
		string detail )
	{
		var runtime = Runtime;
		var caller = Rpc.Caller;
		if ( runtime is null || caller is null || caller.SteamId.ValueUnsigned == 0 ) return;
		var admitted = runtime.TryAcceptClientBootstrapDiagnostic( caller, phase, code, detail );
		if ( !admitted.Accepted ) return;
		var diagnostic = admitted.Diagnostic;
		var safeDetail = ClientBootstrapDiagnosticContract.EscapeLogValue( diagnostic.Detail );
		Log.Error(
			$"HEXAGON_CLIENT_FAILED source=remote account={caller.SteamId.ValueUnsigned} " +
			$"connection={caller.Id} phase={diagnostic.Phase} code={diagnostic.Code} " +
			$"detail=\"{safeDetail}\"" );
	}

	[Rpc.Host]
	public void RequestEstablishSession( ClientSessionNonce nonce )
	{
		var runtime = Runtime;
		var caller = Rpc.Caller;
		if ( runtime is null || caller is null || caller.SteamId.ValueUnsigned == 0 ) return;
		var bound = runtime.BindClientSession( caller, nonce );
		if ( bound.Failed )
		{
			if ( bound.Error!.Code != ErrorCode.Conflict )
				Log.Warning( $"Hexagon rejected client session handshake: {bound.Error.Message}" );
			return;
		}
		using ( Rpc.FilterInclude( caller ) ) ReceiveSessionHello( new ClientSessionHello( bound.Value ) );
		var completed = runtime.CompleteClientSessionHello( caller, bound.Value );
		if ( completed.Failed )
		{
			using ( Rpc.FilterInclude( caller ) ) ReceiveSessionRevoked( bound.Value );
			Log.Warning( $"Hexagon client session hello could not complete: {completed.Error!.Message}" );
		}
	}

	[Rpc.Host]
	public void RequestCharacterList( ClientCommandHeader header ) =>
		Dispatch( header, 1, static () => new RequestCharacterListCommand() );

	[Rpc.Host]
	public void RequestCreateCharacter(
		ClientCommandHeader header,
		string name,
		string description,
		DefinitionId model,
		FactionId faction,
		ClassId? characterClass,
		Dictionary<string, SnapshotValue> fields ) =>
		Dispatch( header, 4, () => new CreateCharacterCommand(
			new CharacterCreationInput( name, description, model, faction, characterClass, fields ) ) );

	[Rpc.Host]
	public void RequestLoadCharacter( ClientCommandHeader header, CharacterId characterId ) =>
		Dispatch( header, 4, () => new LoadCharacterCommand( characterId ) );

	[Rpc.Host]
	public void RequestDeleteCharacter( ClientCommandHeader header, CharacterId characterId ) =>
		Dispatch( header, 4, () => new DeleteCharacterCommand( characterId ) );

	[Rpc.Host]
	public void RequestUnloadCharacter( ClientCommandHeader header ) =>
		Dispatch( header, 1, static () => new UnloadCharacterCommand() );

	[Rpc.Host]
	public void RequestMoveItem(
		ClientCommandHeader header,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position ) =>
		Dispatch( header, 4, () => new MoveInventoryItemCommand( sourceId, targetId, itemId, position ) );

	[Rpc.Host]
	public void RequestItemAction(
		ClientCommandHeader header,
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		Dictionary<string, SnapshotValue> arguments ) =>
		Dispatch( header, 4, () => new RunItemActionCommand( inventoryId, itemId, actionId, arguments ) );

	[Rpc.Host]
	public void RequestDropItem( ClientCommandHeader header, InventoryId sourceId, ItemId itemId ) =>
		Dispatch( header, 4, () => new DropItemCommand( sourceId, itemId ) );

	[Rpc.Host]
	public void RequestPickupItem( ClientCommandHeader header, ItemId itemId, InventoryId destinationId ) =>
		Dispatch( header, 4, () => new PickUpItemCommand( itemId, destinationId ) );

	[Rpc.Host]
	public void RequestChat( ClientCommandHeader header, string channelId, string text ) =>
		Dispatch( header, 2, () => new SendChatCommand( channelId, text ) );

	[Rpc.Host]
	public void RequestCancelAction( ClientCommandHeader header, Guid instanceId ) =>
		Dispatch( header, 1, () => new CancelActionCommand( instanceId ) );

	[Rpc.Host]
	public void RequestBeginInteraction(
		ClientCommandHeader header,
		InteractionTargetInputKind targetKind,
		Guid targetId ) =>
		Dispatch( header, 2, () => new BeginInteractionCommand(
			new InteractionTargetInput( targetKind, targetId ) ) );

	[Rpc.Host]
	public void RequestContinueInteraction(
		ClientCommandHeader header,
		InteractionSessionId sessionId,
		InteractionTargetInputKind targetKind,
		Guid targetId ) =>
		Dispatch( header, 1, () => new ContinueInteractionCommand(
			sessionId,
			new InteractionTargetInput( targetKind, targetId ) ) );

	[Rpc.Host]
	public void RequestCloseInteraction( ClientCommandHeader header, InteractionSessionId sessionId ) =>
		Dispatch( header, 1, () => new CloseInteractionCommand( sessionId ) );

	[Rpc.Host]
	public void RequestSchemaCommand( ClientCommandHeader header, string commandId, Dictionary<string, SnapshotValue> arguments )
	{
		var cost = Runtime?.GetSchemaCommandCost( commandId ) ?? 8;
		Dispatch( header, cost, () => new RunSchemaCommandCommand( commandId, arguments ) );
	}

	public void SendOperationResult(
		Connection recipient,
		ClientSessionScope scope,
		CommandRequestId requestId,
		OperationResult result )
	{
		var retryAfterMilliseconds = OperationResultWireContract.EncodeRetryAfterMilliseconds( result );
		using ( Rpc.FilterInclude( recipient ) )
			ReceiveOperationResult(
				scope,
				requestId.Value,
				result.Succeeded,
				(int)(result.Error?.Code ?? ErrorCode.None),
				result.Error?.Message ?? string.Empty,
				retryAfterMilliseconds );
	}

	public void SendCharacterList( Connection recipient, CharacterListSnapshot snapshot )
	{
		var scope = Runtime?.CaptureClientScope( recipient );
		if ( scope is null || scope.Value.Failed ) return;
		var encoded = SnapshotWireCodec.EncodeCharacterList( snapshot );
		if ( encoded.Failed )
		{
			LogSnapshotWireFailure( "ENCODE", "character-list", encoded.Error! );
			return;
		}
		using ( Rpc.FilterInclude( recipient ) ) ReceiveCharacterList( scope.Value.Value, encoded.Value );
	}

	public void SendClientState(
		Connection recipient,
		PlayerPublicSnapshot publicSnapshot,
		PlayerPrivateSnapshot? privateSnapshot,
		PlayerRosterSnapshot roster,
		IReadOnlyList<SchemaViewSnapshot> schemaViews,
		IReadOnlyList<ClientInventorySnapshot> inventories,
		ActionProgressSnapshot? activeAction )
	{
		var runtime = Runtime;
		if ( runtime is null ) return;
		ClientStateSnapshot.ValidatePayload(
			publicSnapshot,
			privateSnapshot,
			roster,
			schemaViews,
			inventories,
			activeAction );
		var unknownPanel = schemaViews.FirstOrDefault( view => !runtime.IsRegisteredPanel( view.PanelId ) );
		if ( unknownPanel is not null )
		{
			Log.Error( $"HEXAGON_CLIENT_STATE_FAILED unknown panel '{unknownPanel.PanelId}'." );
			return;
		}
		var epoch = runtime.PublishClientStateEpoch( recipient, publicSnapshot.CharacterId );
		if ( epoch.Failed )
		{
			Log.Error( $"HEXAGON_CLIENT_STATE_FAILED {epoch.Error!.Message}" );
			return;
		}

		var snapshot = new ClientStateSnapshot(
			epoch.Value,
			publicSnapshot,
			privateSnapshot,
			roster,
			schemaViews,
			inventories,
			activeAction );
		if ( runtime.TryGetPlayer( recipient.Id, out var player ) )
			player.HostApplyPublicSnapshot( publicSnapshot );
		var scope = runtime.CaptureClientScope( recipient );
		if ( scope.Failed ) return;
		var encoded = SnapshotWireCodec.EncodeClientState( snapshot );
		if ( encoded.Failed )
		{
			LogSnapshotWireFailure( "ENCODE", "client-state", encoded.Error! );
			return;
		}
		using ( Rpc.FilterInclude( recipient ) ) ReceiveClientState( scope.Value, encoded.Value );
	}

	public void SendChat(
		Connection recipient,
		long revision,
		IReadOnlyList<ChatMessageSnapshot> messages )
	{
		var epoch = Runtime?.CaptureChatDeliveryEpoch( recipient );
		if ( epoch is null || epoch.Value.Failed )
		{
			Log.Error( $"HEXAGON_CHAT_DELIVERY_FAILED {epoch?.Error?.Message ?? "Host runtime is unavailable."}" );
			return;
		}
		var snapshot = new ChatSnapshot( epoch.Value.Value, revision, messages );
		var scope = Runtime!.CaptureClientScope( recipient );
		if ( scope.Failed ) return;
		var encoded = SnapshotWireCodec.EncodeChat( snapshot );
		if ( encoded.Failed )
		{
			LogSnapshotWireFailure( "ENCODE", "chat", encoded.Error! );
			return;
		}
		using ( Rpc.FilterInclude( recipient ) ) ReceiveChat( scope.Value, encoded.Value );
	}

	private void Dispatch( ClientCommandHeader header, int cost, Func<ClientCommand> commandFactory )
	{
		var runtime = Runtime;
		if ( runtime is null ) return;
		var requestId = header.RequestId;
		var caller = Rpc.Caller;
		var ingress = CommandIngressAdmission.Evaluate(
			caller is not null && caller.SteamId.ValueUnsigned != 0,
			() => runtime.TryBeginCommand( caller!, requestId, cost ),
			() => runtime.ResolveActor( caller!, header.Scope, false ),
			() => runtime.FinishRejectedCommand( caller!, requestId ) );
		if ( !ingress.Authenticated )
		{
			if ( caller is not null )
				SendOperationResult( caller, header.Scope, requestId,
					OperationResult.Failure( ErrorCode.Unauthorized, "RPC caller is not authenticated." ) );
			return;
		}
		var admitted = ingress.Admission;
		if ( !admitted.Accepted )
		{
			var result = admitted.Failure switch
			{
				CommandAdmissionFailure.RateLimited => OperationResultWireContract.RateLimited(
					"The connection command budget is exhausted.", admitted.RetryAfter ),
				CommandAdmissionFailure.Duplicate => OperationResult.Failure(
					ErrorCode.Conflict, "The command request ID is a duplicate." ),
				_ => OperationResult.Failure(
					ErrorCode.Unauthorized, "The host connection session is unavailable." )
			};
			SendOperationResult( caller!, header.Scope, requestId, result );
			return;
		}
		var resolved = ingress.Session!.Value;
		if ( resolved.Failed )
		{
			SendOperationResult( caller!, header.Scope, requestId,
				OperationResult.Failure( resolved.Error!.Code, resolved.Error.Message ) );
			return;
		}
		var actor = resolved.Value;
		if ( requestId.Value == Guid.Empty )
		{
			CompleteDispatch( runtime, actor, requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InvalidArgument, "The command request ID is empty." ) ) );
			return;
		}

		ClientCommand command;
		OperationResult payload;
		try
		{
			command = commandFactory();
			payload = ClientPayloadLimits.Validate( command );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, "Hexagon rejected a malformed client command payload." );
			CompleteDispatch( runtime, actor, requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InvalidArgument, "The command payload is malformed." ) ) );
			return;
		}
		if ( payload.Failed )
		{
			CompleteDispatch( runtime, actor, requestId,
				OperationOutcome<OperationResult>.Success( payload ) );
			return;
		}
		if ( RequiresStableCharacter( command ) )
		{
			var stableActor = RpcGuard.Resolve( runtime, header.Scope, true );
			if ( stableActor.Failed )
			{
				CompleteDispatch( runtime, actor, requestId,
					OperationOutcome<OperationResult>.Success(
						OperationResult.Failure( stableActor.Error!.Code, stableActor.Error.Message ) ) );
				return;
			}
			actor = stableActor.Value;
		}
		if ( !runtime.TryStartHostOperation(
			$"command:{requestId.Value:D}",
			() => DispatchAsync( runtime, actor, requestId, command ) ) )
		{
			CompleteDispatch(
				runtime,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.Conflict, "The host is draining and no longer accepts commands." ) ) );
		}
	}

	private async Task DispatchAsync(
		HexagonRuntimeSystem runtime,
		RpcActor actor,
		CommandRequestId requestId,
		ClientCommand command )
	{
		if ( !runtime.IsCommandLeaseCurrent( actor ) )
		{
			CompleteDispatch(
				runtime,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure(
						ErrorCode.Unauthorized,
						"The connection or active character changed before command execution." ) ) );
			return;
		}

		var application = runtime.HostApplication;
		if ( application is null )
		{
			CompleteDispatch(
				runtime,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InternalError, "Host application is not ready." ) ) );
			return;
		}

		var outcome = await AsyncOperation.Capture(
			() => application.HandleCommandAsync( actor, command, actor.CancellationToken ) );
		CompleteDispatch( runtime, actor, requestId, outcome, executionStarted: true );
	}

	private void CompleteDispatch(
		HexagonRuntimeSystem runtime,
		RpcActor actor,
		CommandRequestId requestId,
		OperationOutcome<OperationResult> outcome,
		bool executionStarted = false )
	{
		try
		{
			var status = runtime.CompleteCommand( actor, requestId );
			if ( status == CommandCompletionStatus.Disconnected ) return;

			OperationResult result;
			var executionSucceeded = executionStarted && outcome.Succeeded && outcome.Value.Succeeded;
			if ( CommandCompletionPolicy.ShouldRejectAsStale(
				status == CommandCompletionStatus.Current,
				executionStarted,
				executionSucceeded ) )
			{
				result = OperationResult.Failure( ErrorCode.Unauthorized, "The connection or active character changed before the command completed." );
			}
			else if ( !outcome.Succeeded )
			{
				Log.Error( outcome.Exception!, $"Hexagon command '{requestId}' failed unexpectedly." );
				result = actor.CancellationToken.IsCancellationRequested
					? OperationResult.Failure( ErrorCode.Unauthorized, "The command session ended before completion." )
					: OperationResult.Failure( ErrorCode.InternalError, "The host command failed unexpectedly." );
			}
			else
			{
				result = outcome.Value;
			}

			SendOperationResult( actor.Connection, actor.ClientScope, requestId, result );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon could not complete command '{requestId}'." );
		}
	}

	private static bool RequiresStableCharacter( ClientCommand command ) => command is not (
		RequestCharacterListCommand or
		CreateCharacterCommand or
		LoadCharacterCommand or
		DeleteCharacterCommand or
		UnloadCharacterCommand );

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveSessionHello( ClientSessionHello hello )
	{
		var runtime = HexagonRuntimeSystem.Current;
		if ( runtime?.ClientStore?.AcceptHello( hello ) != true ) return;
		var transport = runtime.ClientTransport;
		if ( transport is null ) return;
		if ( transport.BindScope( hello.Scope, out var newlyBound ) && newlyBound )
			Log.Info( $"HEXAGON_SESSION_READY client epoch={hello.Scope.Connection}" );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveSessionRevoked( ClientSessionScope scope )
	{
		var runtime = HexagonRuntimeSystem.Current;
		if ( runtime?.ClientStore?.RevokeSession( scope ) != true ) return;
		runtime.ClientTransport?.RevokeScope( scope );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveOperationResult(
		ClientSessionScope scope,
		Guid rawRequestId,
		bool succeeded,
		int errorCode,
		string message,
		int retryAfterMilliseconds )
	{
		if ( rawRequestId == Guid.Empty ) return;
		var runtime = HexagonRuntimeSystem.Current;
		if ( runtime?.ClientStore?.Accepts( scope ) != true ) return;
		var result = OperationResultWireContract.Decode(
			succeeded, errorCode, message, retryAfterMilliseconds );
		runtime.ClientTransport?.Complete( new CommandRequestId( rawRequestId ), result );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveCharacterList( ClientSessionScope scope, byte[] payload )
	{
		var store = HexagonRuntimeSystem.Current?.ClientStore;
		if ( store?.Accepts( scope ) != true ) return;
		var decoded = SnapshotWireCodec.DecodeCharacterList( payload );
		if ( decoded.Failed )
		{
			LogSnapshotWireFailure( "DECODE", "character-list", decoded.Error! );
			return;
		}
		store.ReplaceCharacterList( scope, decoded.Value );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveClientState( ClientSessionScope scope, byte[] payload )
	{
		var store = HexagonRuntimeSystem.Current?.ClientStore;
		if ( store?.Accepts( scope ) != true ) return;
		var decoded = SnapshotWireCodec.DecodeClientState( payload );
		if ( decoded.Failed )
		{
			LogSnapshotWireFailure( "DECODE", "client-state", decoded.Error! );
			return;
		}
		store.ApplyState( scope, decoded.Value );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveChat( ClientSessionScope scope, byte[] payload )
	{
		var store = HexagonRuntimeSystem.Current?.ClientStore;
		if ( store?.Accepts( scope ) != true ) return;
		var decoded = SnapshotWireCodec.DecodeChat( payload );
		if ( decoded.Failed )
		{
			LogSnapshotWireFailure( "DECODE", "chat", decoded.Error! );
			return;
		}
		store.ReplaceChat( scope, decoded.Value );
	}

	private static void LogSnapshotWireFailure( string direction, string kind, OperationError error ) =>
		Log.Error(
			$"HEXAGON_SNAPSHOT_WIRE_{direction}_FAILED kind={kind} code={error.Code} message={error.Message}" );

}

public sealed class SandboxClientCommandTransport : IClientCommandTransport, IDisposable
{
	private readonly Scene _scene;
	private readonly HexClientStore _store;
	private readonly ClientSessionNonce _nonce;
	private readonly PendingCommandRegistry _pending;
	private ClientSessionRetrySchedule? _sessionRetry;
	private ClientSessionScope? _scope;
	private ClientSessionRetrySchedule SessionRetry =>
		_sessionRetry ??= new ClientSessionRetrySchedule( Stopwatch.Frequency );

	public SandboxClientCommandTransport(
		Scene scene,
		HexClientStore store,
		ClientSessionNonce nonce,
		TimeSpan? timeout = null )
	{
		_scene = scene ?? throw new ArgumentNullException( nameof(scene) );
		_store = store ?? throw new ArgumentNullException( nameof(store) );
		_nonce = nonce;
		_pending = new PendingCommandRegistry( timeout );
		_sessionRetry = new ClientSessionRetrySchedule( Stopwatch.Frequency );
	}

	internal void PollSession( long timestamp )
	{
		if ( !SessionRetry.TryBeginAttempt( timestamp ) ) return;
		var services = _scene.GetAll<HexHostServicesComponent>().FirstOrDefault();
		if ( services is null ) return;
		services.RequestEstablishSession( _nonce );
	}

	internal bool BindScope( ClientSessionScope scope, out bool newlyBound )
	{
		newlyBound = false;
		if ( scope.Nonce != _nonce || !_store.Accepts( scope ) ) return false;
		if ( _scope is not null )
		{
			if ( _scope.Value != scope ) return false;
			SessionRetry.Complete();
			return true;
		}
		_scope = scope;
		SessionRetry.Complete();
		newlyBound = true;
		return true;
	}

	internal bool RevokeScope( ClientSessionScope scope )
	{
		if ( _scope is null || _scope.Value != scope ) return false;
		_scope = null;
		SessionRetry.Complete();
		_pending.Dispose();
		return true;
	}

	internal bool Complete( CommandRequestId requestId, OperationResult result ) =>
		_pending.Complete( requestId, result );

	public ValueTask<OperationResult> SendAsync(
		ClientCommand command,
		System.Threading.CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ( _scope is null || !_store.Accepts( _scope.Value ) )
			return ValueTask.FromResult( OperationResult.Failure(
				ErrorCode.Unauthorized, "The client session handshake is not complete." ) );
		var registered = _pending.Register( cancellationToken );
		if ( registered.Failed ) return ValueTask.FromResult(
			OperationResult.Failure( registered.Error!.Code, registered.Error.Message ) );
		var pending = registered.Value;
		var services = _scene.GetAll<HexHostServicesComponent>().FirstOrDefault();
		if ( services is null )
		{
			_pending.Complete( pending.RequestId,
				OperationResult.Failure( ErrorCode.InternalError, "Host services are not ready." ) );
			return pending.Completion;
		}
		if ( !_pending.IsPending( pending.RequestId ) ) return pending.Completion;

		try
		{
			var header = new ClientCommandHeader( _scope.Value, pending.RequestId );
			switch ( command )
			{
				case RequestCharacterListCommand:
					services.RequestCharacterList( header );
					break;
				case CreateCharacterCommand create:
					services.RequestCreateCharacter(
						header,
						create.Input.Name,
						create.Input.Description,
						create.Input.Model,
						create.Input.Faction,
						create.Input.Class,
						new Dictionary<string, SnapshotValue>( create.Input.Fields, StringComparer.Ordinal ) );
					break;
				case LoadCharacterCommand load:
					services.RequestLoadCharacter( header, load.CharacterId );
					break;
				case DeleteCharacterCommand delete:
					services.RequestDeleteCharacter( header, delete.CharacterId );
					break;
				case UnloadCharacterCommand:
					services.RequestUnloadCharacter( header );
					break;
				case MoveInventoryItemCommand move:
					services.RequestMoveItem( header, move.SourceId, move.TargetId, move.ItemId, move.Position );
					break;
				case RunItemActionCommand action:
					services.RequestItemAction(
						header,
						action.InventoryId,
						action.ItemId,
						action.ActionId,
						new Dictionary<string, SnapshotValue>( action.Arguments, StringComparer.Ordinal ) );
					break;
				case DropItemCommand drop:
					services.RequestDropItem( header, drop.SourceId, drop.ItemId );
					break;
				case PickUpItemCommand pickup:
					services.RequestPickupItem( header, pickup.ItemId, pickup.DestinationId );
					break;
				case SendChatCommand chat:
					services.RequestChat( header, chat.ChannelId, chat.Text );
					break;
				case CancelActionCommand cancel:
					services.RequestCancelAction( header, cancel.InstanceId );
					break;
				case BeginInteractionCommand interaction:
					services.RequestBeginInteraction( header, interaction.Target.Kind, interaction.Target.Id );
					break;
				case ContinueInteractionCommand interaction:
					services.RequestContinueInteraction(
						header,
						interaction.SessionId,
						interaction.Target.Kind,
						interaction.Target.Id );
					break;
				case CloseInteractionCommand interaction:
					services.RequestCloseInteraction( header, interaction.SessionId );
					break;
				case RunSchemaCommandCommand schemaCommand:
					services.RequestSchemaCommand(
						header,
						schemaCommand.CommandId,
						new Dictionary<string, SnapshotValue>( schemaCommand.Arguments, StringComparer.Ordinal ) );
					break;
				default:
					_pending.Complete( pending.RequestId,
						OperationResult.Failure( ErrorCode.InvalidArgument, "Unknown client command." ) );
					break;
			}
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon could not send command '{pending.RequestId}'." );
			_pending.Complete( pending.RequestId,
				OperationResult.Failure( ErrorCode.InternalError, "The command could not be sent to the host." ) );
		}

		return pending.Completion;
	}

	public void Dispose()
	{
		SessionRetry.Complete();
		_pending.Dispose();
	}
}
