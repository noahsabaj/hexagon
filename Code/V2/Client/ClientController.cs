#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Client;

public interface IClientCommandTransport
{
	ValueTask<OperationResult> SendAsync(
		ClientCommand command,
		CancellationToken cancellationToken = default);
}

public interface IHexClientController
{
	ValueTask<OperationResult> RequestCharactersAsync(CancellationToken cancellationToken = default);
	ValueTask<OperationResult> CreateCharacterAsync(CharacterCreationInput input, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> LoadCharacterAsync(CharacterId characterId, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> DeleteCharacterAsync(CharacterId characterId, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> UnloadCharacterAsync(CancellationToken cancellationToken = default);
	ValueTask<OperationResult> MoveItemAsync(InventoryId sourceId, InventoryId targetId, ItemId itemId, int x, int y,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> MoveItemAsync(
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> RunItemActionAsync(InventoryId inventoryId, ItemId itemId, ActionId actionId,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> RunItemActionAsync(InventoryId inventoryId, ItemId itemId, ActionId actionId,
		IReadOnlyDictionary<string, SnapshotValue> arguments,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> DropItemAsync(InventoryId sourceId, ItemId itemId, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> PickUpItemAsync(ItemId itemId, InventoryId destinationId,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> SendChatAsync(string channelId, string text, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> CancelActionAsync(Guid instanceId, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> BeginInteractionAsync(InteractionTargetInput target, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> ContinueInteractionAsync(InteractionSessionId sessionId, InteractionTargetInput target,
		CancellationToken cancellationToken = default);
	ValueTask<OperationResult> CloseInteractionAsync(InteractionSessionId sessionId, CancellationToken cancellationToken = default);
	ValueTask<OperationResult> RunSchemaCommandAsync(string commandId,
		IReadOnlyDictionary<string, SnapshotValue>? arguments = null,
		CancellationToken cancellationToken = default);
}

public sealed class HexClientController : IHexClientController, IDisposable
{
	private readonly IClientCommandTransport _transport;

	public HexClientController(IClientCommandTransport transport) =>
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));

	public ValueTask<OperationResult> RequestCharactersAsync(CancellationToken cancellationToken = default) =>
		Send(new RequestCharacterListCommand(), cancellationToken);

	public ValueTask<OperationResult> CreateCharacterAsync(
		CharacterCreationInput input,
		CancellationToken cancellationToken = default) =>
		Send(new CreateCharacterCommand(input ?? throw new ArgumentNullException(nameof(input))), cancellationToken);

	public ValueTask<OperationResult> LoadCharacterAsync(
		CharacterId characterId,
		CancellationToken cancellationToken = default) =>
		Send(new LoadCharacterCommand(characterId), cancellationToken);

	public ValueTask<OperationResult> DeleteCharacterAsync(
		CharacterId characterId,
		CancellationToken cancellationToken = default) =>
		Send(new DeleteCharacterCommand(characterId), cancellationToken);

	public ValueTask<OperationResult> UnloadCharacterAsync(CancellationToken cancellationToken = default) =>
		Send(new UnloadCharacterCommand(), cancellationToken);

	public ValueTask<OperationResult> MoveItemAsync(
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		int x,
		int y,
		CancellationToken cancellationToken = default) =>
		MoveItemAsync( sourceId, targetId, itemId, new InventoryGridPosition( x, y ), cancellationToken );

	public ValueTask<OperationResult> MoveItemAsync(
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position,
		CancellationToken cancellationToken = default) =>
		Send(new MoveInventoryItemCommand(sourceId, targetId, itemId, position), cancellationToken);

	public ValueTask<OperationResult> RunItemActionAsync(
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		CancellationToken cancellationToken = default) =>
		Send(new RunItemActionCommand(inventoryId, itemId, actionId), cancellationToken);

	public ValueTask<OperationResult> RunItemActionAsync(
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		IReadOnlyDictionary<string, SnapshotValue> arguments,
		CancellationToken cancellationToken = default) =>
		Send(new RunItemActionCommand(inventoryId, itemId, actionId,
			arguments ?? throw new ArgumentNullException(nameof(arguments))), cancellationToken);

	public ValueTask<OperationResult> DropItemAsync(
		InventoryId sourceId,
		ItemId itemId,
		CancellationToken cancellationToken = default) =>
		Send(new DropItemCommand(sourceId, itemId), cancellationToken);

	public ValueTask<OperationResult> PickUpItemAsync(
		ItemId itemId,
		InventoryId destinationId,
		CancellationToken cancellationToken = default) =>
		Send(new PickUpItemCommand(itemId, destinationId), cancellationToken);

	public ValueTask<OperationResult> SendChatAsync(
		string channelId,
		string text,
		CancellationToken cancellationToken = default) =>
		Send(new SendChatCommand(channelId ?? string.Empty, text ?? string.Empty), cancellationToken);

	public ValueTask<OperationResult> CancelActionAsync(
		Guid instanceId,
		CancellationToken cancellationToken = default) =>
		Send(new CancelActionCommand(instanceId), cancellationToken);

	public ValueTask<OperationResult> BeginInteractionAsync(
		InteractionTargetInput target,
		CancellationToken cancellationToken = default) =>
		Send(new BeginInteractionCommand(target), cancellationToken);

	public ValueTask<OperationResult> ContinueInteractionAsync(
		InteractionSessionId sessionId,
		InteractionTargetInput target,
		CancellationToken cancellationToken = default) =>
		Send(new ContinueInteractionCommand(sessionId, target), cancellationToken);

	public ValueTask<OperationResult> CloseInteractionAsync(
		InteractionSessionId sessionId,
		CancellationToken cancellationToken = default) =>
		Send(new CloseInteractionCommand(sessionId), cancellationToken);

	public ValueTask<OperationResult> RunSchemaCommandAsync(
		string commandId,
		IReadOnlyDictionary<string, SnapshotValue>? arguments = null,
		CancellationToken cancellationToken = default) =>
		Send(new RunSchemaCommandCommand(commandId, arguments), cancellationToken);

	private ValueTask<OperationResult> Send(ClientCommand command, CancellationToken cancellationToken) =>
		_transport.SendAsync(command, cancellationToken);

	public void Dispose()
	{
		if ( _transport is IDisposable disposable ) disposable.Dispose();
	}
}
