#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Application;

public sealed record ItemActionContext(
	InventoryActor Actor,
	CharacterRecord Character,
	InventoryRecord Inventory,
	ItemRecord Item,
	ActionId ActionId,
	IReadOnlyDictionary<ItemId, ItemRecord> InventoryItems )
{
	public IReadOnlyDictionary<string, SnapshotValue> Arguments { get; init; } =
		new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
}

/// <summary>
/// Closed presentation categories for post-commit item-action receipts. The
/// receipt contains only client-safe scalar fields, never domain aggregates or
/// persisted payloads.
/// </summary>
public enum ItemActionPresentationKind
{
	IdentityDocument = 1,
	ReferenceDocument = 2,
	PersonalNote = 3,
	PermitCredential = 4
}

public sealed record ItemActionPresentationReceipt
{
	public const int MaximumFieldCount = 16;
	public const int MaximumTitleLength = 96;
	public const int MaximumTextLength = 4096;

	public ItemActionPresentationReceipt(
		ItemActionPresentationKind kind,
		string title,
		IReadOnlyDictionary<string, SnapshotValue>? fields = null )
	{
		if ( kind is < ItemActionPresentationKind.IdentityDocument or > ItemActionPresentationKind.PermitCredential )
			throw new ArgumentOutOfRangeException( nameof(kind) );
		if ( string.IsNullOrWhiteSpace( title ) || title.Length > MaximumTitleLength )
			throw new ArgumentException( $"Presentation title must contain 1-{MaximumTitleLength} characters.", nameof(title) );
		if ( fields?.Count > MaximumFieldCount )
			throw new ArgumentException( $"Presentation may contain at most {MaximumFieldCount} fields.", nameof(fields) );

		var copy = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		foreach ( var pair in fields ?? new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ) )
		{
			if ( !KernelIdentifier.IsValid( pair.Key ) )
				throw new ArgumentException( $"Presentation field '{pair.Key}' is not a stable identifier.", nameof(fields) );
			if ( pair.Value.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice &&
				pair.Value.StringValue.Length > MaximumTextLength )
				throw new ArgumentException(
					$"Presentation field '{pair.Key}' exceeds {MaximumTextLength} characters.", nameof(fields) );
			copy.Add( pair.Key, pair.Value );
		}

		Kind = kind;
		Title = title;
		Fields = new ReadOnlyDictionary<string, SnapshotValue>( copy );
	}

	public ItemActionPresentationKind Kind { get; }
	public string Title { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Fields { get; }
}

public sealed record ItemActionPlan
{
	public IReadOnlyDictionary<ItemId, ItemRecord> UpdatedItems { get; init; } =
		new Dictionary<ItemId, ItemRecord>();
	public IReadOnlySet<ItemId> DeletedItems { get; init; } = new HashSet<ItemId>();
	public CharacterRecord? UpdatedCharacter { get; init; }
	public InventoryRecord? UpdatedInventory { get; init; }
	public ItemActionPresentationReceipt? Presentation { get; init; }
}

public interface IItemActionHandler
{
	ActionId Id { get; }
	OperationResult<ItemActionPlan> Plan( ItemActionContext context );
}

public sealed class ItemActionRegistry
{
	private readonly IReadOnlyDictionary<ActionId, IItemActionHandler> _handlers;

	public ItemActionRegistry( IEnumerable<IItemActionHandler> handlers )
	{
		ArgumentNullException.ThrowIfNull( handlers );
		var map = new Dictionary<ActionId, IItemActionHandler>();
		foreach ( var handler in handlers.OrderBy( value => value.Id.Value, StringComparer.Ordinal ) )
		{
			if ( handler is null ) throw new ArgumentException( "Item action handler cannot be null.", nameof(handlers) );
			if ( !map.TryAdd( handler.Id, handler ) )
				throw new InvalidOperationException( $"Item action handler '{handler.Id}' is duplicated." );
		}
		_handlers = map;
	}

	public OperationResult<IItemActionHandler> Require( ActionId id ) =>
		_handlers.TryGetValue( id, out var handler )
			? OperationResult<IItemActionHandler>.Success( handler )
			: OperationResult<IItemActionHandler>.Failure(
				ErrorCode.UnknownDefinition, $"Item action handler '{id}' is not registered." );
}

public sealed record ItemActionCommittedEvent(
	InventoryActor Actor,
	ItemId ItemId,
	ActionId ActionId,
	long CommitSequence,
	ItemActionPresentationReceipt? Presentation );

/// <summary>
/// Atomic host boundary for all item actions. Handlers are pure planners; this
/// service alone resolves membership, validates the complete plan and commits it.
/// </summary>
public sealed class ItemActionService
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly InventoryAccessService _access;
	private readonly IItemShapeCatalog _shapes;
	private readonly ItemActionRegistry _handlers;
	private readonly PolicyPipeline<ItemActionContext> _policy;
	private readonly PostCommitEventBus<ItemActionCommittedEvent> _events;
	private readonly Action<Exception>? _diagnostics;

	public ItemActionService(
		DomainRepositories repositories,
		CompiledSchema schema,
		InventoryAccessService access,
		IItemShapeCatalog shapes,
		ItemActionRegistry handlers,
		PolicyPipeline<ItemActionContext> policy,
		PostCommitEventBus<ItemActionCommittedEvent>? events = null,
		Action<Exception>? diagnostics = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_access = access ?? throw new ArgumentNullException( nameof(access) );
		_shapes = shapes ?? throw new ArgumentNullException( nameof(shapes) );
		_handlers = handlers ?? throw new ArgumentNullException( nameof(handlers) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_events = events ?? new PostCommitEventBus<ItemActionCommittedEvent>();
		_diagnostics = diagnostics;
	}

	public async ValueTask<OperationResult> ExecuteAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		CancellationToken cancellationToken = default ) =>
		await ExecuteAsync( actor, inventoryId, itemId, actionId,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal ), cancellationToken );

	public async ValueTask<OperationResult> ExecuteAsync(
		InventoryActor actor,
		InventoryId inventoryId,
		ItemId itemId,
		ActionId actionId,
		IReadOnlyDictionary<string, SnapshotValue> arguments,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( arguments );
		var inventoryDocument = _repositories.Inventories.Find( DomainKeys.Inventory( inventoryId ) );
		var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		var characterDocument = _repositories.Characters.Find( DomainKeys.Character( actor.CharacterId ) );
		if ( inventoryDocument is null || itemDocument is null || characterDocument is null )
			return OperationResult.Failure( ErrorCode.NotFound, "Character, inventory or item was not found." );
		var inventory = inventoryDocument.Value.DeepCopy();
		var item = itemDocument.Value.DeepCopy();
		var character = characterDocument.Value.DeepCopy();
		if ( character.AccountId != actor.AccountId )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Active character does not belong to the authenticated actor." );
		if ( inventory.Find( item.Id ) is null )
			return OperationResult.Failure( ErrorCode.NotFound, "Item is not a member of the claimed inventory." );
		if ( !_access.Has(
			actor.ConnectionId, actor.CharacterId, inventory.Id,
			InventoryCapability.View | InventoryCapability.Use ) )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Use capability is missing." );
		if ( !_schema.Actions.Contains( actionId.Value ) )
			return OperationResult.Failure( ErrorCode.UnknownDefinition, "Action definition is not registered." );
		if ( !_schema.Items.TryGet( item.Definition.Value, out var definition ) ||
			!definition!.ActionIds.Contains( actionId.Value, StringComparer.Ordinal ) )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Action is not registered for this item definition." );
		var handler = _handlers.Require( actionId );
		if ( handler.Failed ) return OperationResult.Failure( handler.Error!.Code, handler.Error.Message );

		var inventoryDocuments = inventory.Placements
			.Select( placement => _repositories.Items.Find( DomainKeys.Item( placement.ItemId ) ) )
			.Where( document => document is not null )
			.ToDictionary( document => document!.Value.Id, document => document! );
		if ( inventoryDocuments.Count != inventory.Placements.Count )
			return OperationResult.Failure( ErrorCode.Conflict, "Inventory references a missing item." );
		var inventoryItems = inventoryDocuments.ToDictionary(
			pair => pair.Key,
			pair => pair.Value.Value.DeepCopy() );
		var context = new ItemActionContext( actor, character, inventory, item, actionId, inventoryItems )
		{
			Arguments = new Dictionary<string, SnapshotValue>( arguments, StringComparer.Ordinal )
		};
		var policy = _policy.Evaluate( context );
		if ( policy.Failed ) return policy;
		OperationResult<ItemActionPlan> planned;
		try
		{
			planned = handler.Value.Plan( context );
		}
		catch ( Exception exception )
		{
			_diagnostics?.Invoke( exception );
			return OperationResult.Failure( ErrorCode.InternalError, $"Item action handler '{actionId}' failed closed." );
		}
		if ( planned.Failed ) return OperationResult.Failure( planned.Error!.Code, planned.Error.Message );
		if ( planned.Value is null )
			return OperationResult.Failure( ErrorCode.InternalError, "Item action handler returned no mutation plan." );
		var validation = ValidatePlan( context, planned.Value );
		if ( validation.Failed ) return validation;

		var finalInventory = planned.Value.UpdatedInventory ?? inventory;
		if ( planned.Value.DeletedItems.Count > 0 )
			finalInventory = finalInventory with
			{
				Placements = finalInventory.Placements
					.Where( placement => !planned.Value.DeletedItems.Contains( placement.ItemId ) )
					.ToArray()
			};

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		foreach ( var updated in planned.Value.UpdatedItems.Values )
		{
			var editor = unitOfWork.Edit( _repositories.Items, inventoryDocuments[updated.Id] );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "An action item changed before commit." );
			}
			editor.Replace( updated );
			unitOfWork.Save( editor );
		}
		foreach ( var deleted in planned.Value.DeletedItems )
			unitOfWork.Delete( _repositories.Items, inventoryDocuments[deleted] );
		if ( finalInventory != inventory )
		{
			var editor = unitOfWork.Edit( _repositories.Inventories, inventoryDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "Action inventory changed before commit." );
			}
			editor.Replace( finalInventory );
			unitOfWork.Save( editor );
		}
		if ( planned.Value.UpdatedCharacter is not null )
		{
			var editor = unitOfWork.Edit( _repositories.Characters, characterDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "Action character changed before commit." );
			}
			editor.Replace( planned.Value.UpdatedCharacter );
			unitOfWork.Save( editor );
		}

		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded ) return PersistenceResultMapping.Failure( committed.Error! );
		_events.Publish( new ItemActionCommittedEvent(
			actor,
			item.Id,
			actionId,
			committed.Value!.Sequence,
			planned.Value.Presentation ) );
		return OperationResult.Success();
	}

	private OperationResult ValidatePlan( ItemActionContext context, ItemActionPlan plan )
	{
		if ( plan.UpdatedInventory is not null && plan.UpdatedInventory.Id != context.Inventory.Id )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action cannot replace a different inventory." );
		if ( plan.UpdatedInventory is not null &&
			(plan.UpdatedInventory.Owner != context.Inventory.Owner ||
			 plan.UpdatedInventory.Width != context.Inventory.Width ||
			 plan.UpdatedInventory.Height != context.Inventory.Height) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action cannot change inventory ownership or dimensions." );
		if ( plan.UpdatedCharacter is not null && plan.UpdatedCharacter.Id != context.Character.Id )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action cannot replace a different character." );
		if ( plan.UpdatedCharacter is not null &&
			(plan.UpdatedCharacter.AccountId != context.Character.AccountId ||
			 plan.UpdatedCharacter.Slot != context.Character.Slot ||
			 plan.UpdatedCharacter.Name != context.Character.Name ||
			 plan.UpdatedCharacter.Description != context.Character.Description ||
			 plan.UpdatedCharacter.Model != context.Character.Model ||
			 plan.UpdatedCharacter.Faction != context.Character.Faction ||
			 plan.UpdatedCharacter.Class != context.Character.Class ||
			 plan.UpdatedCharacter.CreatedAt != context.Character.CreatedAt ||
			 plan.UpdatedCharacter.IsBanned != context.Character.IsBanned ||
			 plan.UpdatedCharacter.BanExpiresAt != context.Character.BanExpiresAt) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action attempted to change protected character identity or lifecycle state." );
		foreach ( var updated in plan.UpdatedItems )
		{
			if ( updated.Key != updated.Value.Id || !context.InventoryItems.TryGetValue( updated.Key, out var original ) ||
				plan.DeletedItems.Contains( updated.Key ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "Action item update is outside the proven inventory or also deleted." );
			if ( updated.Value.Definition != original.Definition )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "Action cannot change an item's definition." );
			foreach ( var trait in updated.Value.Traits.Values )
			{
				if ( !_schema.PersistedTypes.TryGet( trait.TypeId.Value, out var registration ) ||
					trait.TypeVersion <= 0 || trait.TypeVersion > registration!.Version )
					return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Action produced an unregistered item trait." );
			}
		}
		if ( plan.DeletedItems.Any( id => !context.InventoryItems.ContainsKey( id ) ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action attempted to delete an item outside the proven inventory." );
		if ( plan.DeletedItems.Any( id => _repositories.Inventories.All().Any( document =>
			document.Value.Owner.Kind == InventoryOwnerKind.ParentItem && document.Value.Owner.OwnerId == id.Value ) ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action cannot delete a container item without an explicit cascade operation." );
		var finalInventory = plan.UpdatedInventory ?? context.Inventory;
		var expectedItems = context.Inventory.Placements
			.Select( placement => placement.ItemId )
			.Where( id => !plan.DeletedItems.Contains( id ) )
			.ToHashSet();
		var actualItems = finalInventory.Placements
			.Where( placement => !plan.DeletedItems.Contains( placement.ItemId ) )
			.Select( placement => placement.ItemId )
			.ToArray();
		if ( actualItems.Distinct().Count() != actualItems.Length || !expectedItems.SetEquals( actualItems ) )
			return OperationResult.Failure( ErrorCode.InvalidArgument, "Action inventory placements do not preserve the proven item set." );
		var effectiveItems = context.InventoryItems.ToDictionary( pair => pair.Key, pair => pair.Value );
		foreach ( var updated in plan.UpdatedItems ) effectiveItems[updated.Key] = updated.Value;
		var occupied = new HashSet<(int X, int Y)>();
		foreach ( var placement in finalInventory.Placements.Where( value => !plan.DeletedItems.Contains( value.ItemId ) ) )
		{
			if ( !effectiveItems.TryGetValue( placement.ItemId, out var placedItem ) ||
				!_shapes.TryGetShape( placedItem.Definition, out var shape ) ||
				placement.X < 0 || placement.Y < 0 ||
				placement.X + shape.Width > finalInventory.Width ||
				placement.Y + shape.Height > finalInventory.Height )
				return OperationResult.Failure( ErrorCode.Conflict, "Action produced an invalid inventory placement." );
			for ( var y = placement.Y; y < placement.Y + shape.Height; y++ )
			for ( var x = placement.X; x < placement.X + shape.Width; x++ )
				if ( !occupied.Add( (x, y) ) )
					return OperationResult.Failure( ErrorCode.Conflict, "Action produced overlapping inventory placements." );
		}
		if ( plan.UpdatedCharacter is not null )
		{
			var state = plan.UpdatedCharacter.SchemaState;
			if ( !_schema.PersistedTypes.TryGet( state.TypeId.Value, out var registration ) ||
				state.TypeVersion <= 0 || state.TypeVersion > registration!.Version )
				return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Action produced unregistered character state." );
		}
		return OperationResult.Success();
	}
}
