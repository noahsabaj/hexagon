#nullable enable

using System;
using System.Collections.Generic;
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
	public void RequestCharacterList( Guid requestId ) => Dispatch( requestId, new RequestCharacterListCommand() );

	[Rpc.Host]
	public void RequestCreateCharacter( Guid requestId, CharacterCreationInput input ) =>
		Dispatch( requestId, new CreateCharacterCommand( input ) );

	[Rpc.Host]
	public void RequestLoadCharacter( Guid requestId, CharacterId characterId ) =>
		Dispatch( requestId, new LoadCharacterCommand( characterId ) );

	[Rpc.Host]
	public void RequestDeleteCharacter( Guid requestId, CharacterId characterId ) =>
		Dispatch( requestId, new DeleteCharacterCommand( characterId ) );

	[Rpc.Host]
	public void RequestUnloadCharacter( Guid requestId ) => Dispatch( requestId, new UnloadCharacterCommand() );

	[Rpc.Host]
	public void RequestMoveItem( Guid requestId, InventoryId sourceId, InventoryId targetId, ItemId itemId, int x, int y ) =>
		Dispatch( requestId, new MoveInventoryItemCommand( sourceId, targetId, itemId, x, y ) );

	[Rpc.Host]
	public void RequestItemAction(
		Guid requestId,
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		Dictionary<string, SnapshotValue> arguments ) =>
		Dispatch( requestId, new RunItemActionCommand( inventoryId, itemId, actionId, arguments ) );

	[Rpc.Host]
	public void RequestDropItem( Guid requestId, InventoryId sourceId, ItemId itemId ) =>
		Dispatch( requestId, new DropItemCommand( sourceId, itemId ) );

	[Rpc.Host]
	public void RequestPickupItem( Guid requestId, ItemId itemId, InventoryId destinationId ) =>
		Dispatch( requestId, new PickUpItemCommand( itemId, destinationId ) );

	[Rpc.Host]
	public void RequestChat( Guid requestId, string channelId, string text ) =>
		Dispatch( requestId, new SendChatCommand( channelId, text ) );

	[Rpc.Host]
	public void RequestCancelAction( Guid requestId, Guid instanceId ) =>
		Dispatch( requestId, new CancelActionCommand( instanceId ) );

	[Rpc.Host]
	public void RequestBeginInteraction( Guid requestId, InteractionTargetInput target ) =>
		Dispatch( requestId, new BeginInteractionCommand( target ) );

	[Rpc.Host]
	public void RequestContinueInteraction( Guid requestId, InteractionSessionId sessionId, InteractionTargetInput target ) =>
		Dispatch( requestId, new ContinueInteractionCommand( sessionId, target ) );

	[Rpc.Host]
	public void RequestCloseInteraction( Guid requestId, InteractionSessionId sessionId ) =>
		Dispatch( requestId, new CloseInteractionCommand( sessionId ) );

	[Rpc.Host]
	public void RequestSchemaCommand( Guid requestId, string commandId, Dictionary<string, SnapshotValue> arguments ) =>
		Dispatch( requestId, new RunSchemaCommandCommand( commandId, arguments ) );

	public void SendOperationResult( Connection recipient, CommandRequestId requestId, OperationResult result )
	{
		using ( Rpc.FilterInclude( recipient ) )
			ReceiveOperationResult( requestId.Value, result.Succeeded, (int)(result.Error?.Code ?? ErrorCode.None), result.Error?.Message ?? string.Empty );
	}

	public void SendCharacterList( Connection recipient, CharacterListSnapshot snapshot )
	{
		using ( Rpc.FilterInclude( recipient ) ) ReceiveCharacterList( snapshot );
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
		{
			player.HostApplyPublicSnapshot( publicSnapshot );
			if ( !publicSnapshot.HasCharacter ) player.HostClearCharacter();
		}
		using ( Rpc.FilterInclude( recipient ) ) ReceiveClientState( snapshot );
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
		using ( Rpc.FilterInclude( recipient ) ) ReceiveChat( snapshot );
	}

	private void Dispatch( Guid rawRequestId, ClientCommand command )
	{
		var runtime = Runtime;
		if ( runtime is null ) return;
		if ( rawRequestId == Guid.Empty )
		{
			Log.Warning( "Hexagon rejected a command with an empty request ID." );
			return;
		}
		var requestId = new CommandRequestId( rawRequestId );
		var actor = RpcGuard.Resolve( runtime, RequiresStableCharacter( command ) );
		if ( actor.Failed )
		{
			if ( Rpc.Caller is not null )
				SendOperationResult( Rpc.Caller, requestId, OperationResult.Failure( actor.Error!.Code, actor.Error.Message ) );
			return;
		}
		if ( !runtime.TryBeginCommand( actor.Value, requestId ) )
		{
			SendOperationResult( actor.Value.Connection, requestId,
				OperationResult.Failure( ErrorCode.Conflict, "The command request ID is duplicate or the connection has too many pending commands." ) );
			return;
		}
		_ = DispatchAsync( runtime, actor.Value, requestId, command );
	}

	private async Task DispatchAsync(
		HexagonRuntimeSystem runtime,
		RpcActor actor,
		CommandRequestId requestId,
		ClientCommand command )
	{
		var application = runtime.HostApplication;
		var outcome = application is null
			? RuntimeOperationOutcome<OperationResult>.Success(
				OperationResult.Failure( ErrorCode.InternalError, "Host application is not ready." ) )
			: await RuntimeAsyncOperation.Capture(
				() => application.HandleCommandAsync( actor, command, actor.CancellationToken ) );
		CompleteDispatch( runtime, actor, requestId, outcome );
	}

	private void CompleteDispatch(
		HexagonRuntimeSystem runtime,
		RpcActor actor,
		CommandRequestId requestId,
		RuntimeOperationOutcome<OperationResult> outcome )
	{
		try
		{
			var status = runtime.CompleteCommand( actor, requestId );
			if ( status == CommandCompletionStatus.Disconnected ) return;

			OperationResult result;
			if ( status == CommandCompletionStatus.Stale )
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

			SendOperationResult( actor.Connection, requestId, result );
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
	private void ReceiveOperationResult( Guid rawRequestId, bool succeeded, int errorCode, string message )
	{
		if ( rawRequestId == Guid.Empty ) return;
		var result = succeeded
			? OperationResult.Success()
			: OperationResult.Failure(
				errorCode >= (int)ErrorCode.InvalidArgument && errorCode <= (int)ErrorCode.InternalError
					? (ErrorCode)errorCode
					: ErrorCode.InternalError,
				string.IsNullOrWhiteSpace( message ) ? "The host denied the command." : message );
		HexagonRuntimeSystem.Current?.ClientTransport?.Complete( new CommandRequestId( rawRequestId ), result );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveCharacterList( CharacterListSnapshot snapshot ) =>
		HexagonRuntimeSystem.Current?.ClientStore?.ReplaceCharacterList( snapshot );

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveClientState( ClientStateSnapshot snapshot ) =>
		HexagonRuntimeSystem.Current?.ClientStore?.ApplyState( snapshot );

	[Rpc.Broadcast( NetFlags.HostOnly )]
	private void ReceiveChat( ChatSnapshot snapshot ) =>
		HexagonRuntimeSystem.Current?.ClientStore?.ReplaceChat( snapshot );

}

public sealed class SandboxClientCommandTransport : IClientCommandTransport, IDisposable
{
	private readonly Scene _scene;
	private readonly PendingCommandRegistry _pending;

	public SandboxClientCommandTransport( Scene scene, TimeSpan? timeout = null )
	{
		_scene = scene ?? throw new ArgumentNullException( nameof(scene) );
		_pending = new PendingCommandRegistry( timeout );
	}

	internal bool Complete( CommandRequestId requestId, OperationResult result ) =>
		_pending.Complete( requestId, result );

	public ValueTask<OperationResult> SendAsync(
		ClientCommand command,
		System.Threading.CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
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
			var requestId = pending.RequestId.Value;
			switch ( command )
			{
				case RequestCharacterListCommand:
					services.RequestCharacterList( requestId );
					break;
				case CreateCharacterCommand create:
					services.RequestCreateCharacter( requestId, create.Input );
					break;
				case LoadCharacterCommand load:
					services.RequestLoadCharacter( requestId, load.CharacterId );
					break;
				case DeleteCharacterCommand delete:
					services.RequestDeleteCharacter( requestId, delete.CharacterId );
					break;
				case UnloadCharacterCommand:
					services.RequestUnloadCharacter( requestId );
					break;
				case MoveInventoryItemCommand move:
					services.RequestMoveItem( requestId, move.SourceId, move.TargetId, move.ItemId, move.X, move.Y );
					break;
				case RunItemActionCommand action:
					services.RequestItemAction(
						requestId,
						action.InventoryId,
						action.ItemId,
						action.ActionId,
						new Dictionary<string, SnapshotValue>( action.Arguments, StringComparer.Ordinal ) );
					break;
				case DropItemCommand drop:
					services.RequestDropItem( requestId, drop.SourceId, drop.ItemId );
					break;
				case PickUpItemCommand pickup:
					services.RequestPickupItem( requestId, pickup.ItemId, pickup.DestinationId );
					break;
				case SendChatCommand chat:
					services.RequestChat( requestId, chat.ChannelId, chat.Text );
					break;
				case CancelActionCommand cancel:
					services.RequestCancelAction( requestId, cancel.InstanceId );
					break;
				case BeginInteractionCommand interaction:
					services.RequestBeginInteraction( requestId, interaction.Target );
					break;
				case ContinueInteractionCommand interaction:
					services.RequestContinueInteraction( requestId, interaction.SessionId, interaction.Target );
					break;
				case CloseInteractionCommand interaction:
					services.RequestCloseInteraction( requestId, interaction.SessionId );
					break;
				case RunSchemaCommandCommand schemaCommand:
					services.RequestSchemaCommand(
						requestId,
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

	public void Dispose() => _pending.Dispose();
}
