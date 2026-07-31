#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

/// <summary>
/// Marker for client intent messages. Infrastructure adapters translate these
/// immutable commands to host RPCs; the host must still authenticate and validate them.
/// </summary>
public abstract record ClientCommand;

public sealed record RequestCharacterListCommand : ClientCommand;

public sealed record CharacterCreationInput
{
	public CharacterCreationInput(
		string name,
		string description,
		DefinitionId model,
		FactionId faction,
		ClassId? characterClass,
		IReadOnlyDictionary<string, SnapshotValue>? fields = null)
	{
		Name = name ?? string.Empty;
		Description = description ?? string.Empty;
		Model = model;
		Faction = faction;
		Class = characterClass;
		Fields = new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>(
				fields ?? new Dictionary<string, SnapshotValue>(),
				StringComparer.Ordinal));
	}

	public string Name { get; }
	public string Description { get; }
	public DefinitionId Model { get; }
	public FactionId Faction { get; }
	public ClassId? Class { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Fields { get; }
}

public sealed record CreateCharacterCommand(CharacterCreationInput Input) : ClientCommand;
public sealed record LoadCharacterCommand(CharacterId CharacterId) : ClientCommand;
public sealed record DeleteCharacterCommand(CharacterId CharacterId) : ClientCommand;
public sealed record UnloadCharacterCommand : ClientCommand;

public sealed record MoveInventoryItemCommand(
	InventoryId SourceId,
	InventoryId TargetId,
	ItemId ItemId,
	InventoryGridPosition Position
) : ClientCommand
{
	public int X => Position.X;
	public int Y => Position.Y;

	public MoveInventoryItemCommand(
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		int x,
		int y ) : this( sourceId, targetId, itemId, new InventoryGridPosition( x, y ) )
	{
	}
}

public sealed record RunItemActionCommand(
	InventoryId InventoryId,
	ItemId ItemId,
	ActionId ActionId
) : ClientCommand
{
	public IReadOnlyDictionary<string, SnapshotValue> Arguments { get; init; } =
		new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) );

	public RunItemActionCommand(
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		IReadOnlyDictionary<string, SnapshotValue>? arguments )
		: this( inventoryId, itemId, actionId )
	{
		Arguments = new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>(
				arguments ?? new Dictionary<string, SnapshotValue>(),
				StringComparer.Ordinal ) );
	}
}

public sealed record DropItemCommand(InventoryId SourceId, ItemId ItemId) : ClientCommand;
public sealed record PickUpItemCommand(ItemId ItemId, InventoryId DestinationId) : ClientCommand;
public sealed record SendChatCommand(string ChannelId, string Text) : ClientCommand;
public sealed record CancelActionCommand(Guid InstanceId) : ClientCommand;

public enum InteractionTargetInputKind
{
	SceneEntity = 0,
	Inventory = 1,
	Item = 2,
	Character = 3
}

public sealed record InteractionTargetInput(InteractionTargetInputKind Kind, Guid Id);
public sealed record BeginInteractionCommand(InteractionTargetInput Target) : ClientCommand;
public sealed record ContinueInteractionCommand(
	InteractionSessionId SessionId,
	InteractionTargetInput Target) : ClientCommand;
public sealed record CloseInteractionCommand(InteractionSessionId SessionId) : ClientCommand;

public sealed record RunSchemaCommandCommand : ClientCommand
{
	public RunSchemaCommandCommand(string commandId, IReadOnlyDictionary<string, SnapshotValue>? arguments = null)
	{
		CommandId = commandId ?? string.Empty;
		Arguments = new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>(arguments ?? new Dictionary<string, SnapshotValue>(), StringComparer.Ordinal));
	}

	public string CommandId { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Arguments { get; }
}
