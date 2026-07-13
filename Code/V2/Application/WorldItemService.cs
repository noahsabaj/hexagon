#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Application;

public interface IWorldModelCatalog
{
	bool IsValidModel( string modelPath );
}

public sealed record WorldDropContext(
	InventoryActor Actor,
	InventoryRecord Source,
	ItemRecord Item,
	WorldTransformRecord Transform );

public sealed record WorldPickupContext(
	InventoryActor Actor,
	InventoryRecord Destination,
	ItemRecord Item,
	WorldItemRecord WorldItem );

public sealed record WorldItemDroppedEvent( WorldItemRecord WorldItem, string ModelPath, long CommitSequence );
public sealed record WorldItemPickedUpEvent( ItemId ItemId, InventoryId InventoryId, long CommitSequence );

/// <summary>
/// Atomic world transition service. A failed durability operation leaves the
/// committed inventory/world view untouched.
/// </summary>
public sealed class WorldItemService
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly InventoryAccessService _access;
	private readonly InventoryLayoutService _layout;
	private readonly IWorldModelCatalog _models;
	private readonly PolicyPipeline<WorldDropContext> _dropPolicy;
	private readonly PolicyPipeline<WorldPickupContext> _pickupPolicy;
	private readonly PostCommitEventBus<WorldItemDroppedEvent> _droppedEvents;
	private readonly PostCommitEventBus<WorldItemPickedUpEvent> _pickedUpEvents;

	public WorldItemService(
		DomainRepositories repositories,
		CompiledSchema schema,
		InventoryAccessService access,
		InventoryLayoutService layout,
		IWorldModelCatalog models,
		PolicyPipeline<WorldDropContext> dropPolicy,
		PolicyPipeline<WorldPickupContext> pickupPolicy,
		PostCommitEventBus<WorldItemDroppedEvent>? droppedEvents = null,
		PostCommitEventBus<WorldItemPickedUpEvent>? pickedUpEvents = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_access = access ?? throw new ArgumentNullException( nameof(access) );
		_layout = layout ?? throw new ArgumentNullException( nameof(layout) );
		_models = models ?? throw new ArgumentNullException( nameof(models) );
		_dropPolicy = dropPolicy ?? throw new ArgumentNullException( nameof(dropPolicy) );
		_pickupPolicy = pickupPolicy ?? throw new ArgumentNullException( nameof(pickupPolicy) );
		_droppedEvents = droppedEvents ?? new PostCommitEventBus<WorldItemDroppedEvent>();
		_pickedUpEvents = pickedUpEvents ?? new PostCommitEventBus<WorldItemPickedUpEvent>();
	}

	public IReadOnlyList<WorldItemRecord> LoadWorldItems() =>
		_repositories.WorldItems.All().Select( document => document.Value ).ToArray();

	public async ValueTask<OperationResult> DropAsync(
		InventoryActor actor,
		InventoryId sourceId,
		ItemId itemId,
		WorldTransformRecord transform,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( transform );
		var sourceDocument = _repositories.Inventories.Find( DomainKeys.Inventory( sourceId ) );
			var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
			if ( sourceDocument is null || itemDocument is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Inventory or item was not found." );
			var source = sourceDocument.Value;
			var item = itemDocument.Value;
			if ( source.Find( item.Id ) is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Item is not a member of the claimed source inventory." );
			if ( !_access.Has(
				actor.ConnectionId,
				actor.CharacterId,
				source.Id,
				InventoryCapability.Move | InventoryCapability.Drop ) )
				return OperationResult.Failure( ErrorCode.Unauthorized, "Drop capability is missing." );

			if ( !_schema.Items.TryGet( item.Definition.Value, out var definition ) )
				return OperationResult.Failure( ErrorCode.UnknownDefinition, "Item definition is not registered." );
			if ( !definition!.CanDrop || string.IsNullOrWhiteSpace( definition.WorldModel ) )
				return OperationResult.Failure( ErrorCode.PolicyDenied, "Item is explicitly non-droppable." );
			if ( !_models.IsValidModel( definition.WorldModel ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "Item world model does not resolve." );
			if ( _repositories.WorldItems.Find( DomainKeys.WorldItem( item.Id ) ) is not null )
				return OperationResult.Failure( ErrorCode.Conflict, "Item already has a world location." );

			var policy = _dropPolicy.Evaluate( new WorldDropContext( actor, source, item, transform ) );
			if ( policy.Failed ) return policy;
			var removed = _layout.Remove( source, item.Id );
			if ( removed.Failed ) return OperationResult.Failure( removed.Error!.Code, removed.Error.Message );
			var worldItem = new WorldItemRecord { ItemId = item.Id, Transform = transform, Revision = 0 };

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			var editor = unitOfWork.Edit( _repositories.Inventories, sourceDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "Inventory changed." );
			}
			editor.Replace( removed.Value );
			unitOfWork.Save( editor );
			unitOfWork.Create( _repositories.WorldItems, DomainKeys.WorldItem( item.Id ), worldItem );
			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceResultMapping.Failure( committed.Error! );

			_droppedEvents.Publish( new WorldItemDroppedEvent( worldItem, definition.WorldModel, committed.Value!.Sequence ) );
			return OperationResult.Success();
	}

	public async ValueTask<OperationResult> PickUpAsync(
		InventoryActor actor,
		ItemId itemId,
		InventoryId destinationId,
		CancellationToken cancellationToken = default )
	{
		var worldDocument = _repositories.WorldItems.Find( DomainKeys.WorldItem( itemId ) );
			var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
			var destinationDocument = _repositories.Inventories.Find( DomainKeys.Inventory( destinationId ) );
			if ( worldDocument is null || itemDocument is null || destinationDocument is null )
				return OperationResult.Failure( ErrorCode.NotFound, "World item, item or destination was not found." );
			var item = itemDocument.Value;
			var destination = destinationDocument.Value;
			if ( !_access.Has(
				actor.ConnectionId,
				actor.CharacterId,
				destination.Id,
				InventoryCapability.Move | InventoryCapability.TransferIn ) )
				return OperationResult.Failure( ErrorCode.Unauthorized, "Pickup destination capability is missing." );

			var policy = _pickupPolicy.Evaluate( new WorldPickupContext( actor, destination, item, worldDocument.Value ) );
			if ( policy.Failed ) return policy;
			var firstFit = _layout.FindFirstFit( destination, item );
			if ( firstFit.Failed ) return OperationResult.Failure( firstFit.Error!.Code, firstFit.Error.Message );
			var added = _layout.AddAt( destination, item, firstFit.Value.X, firstFit.Value.Y );
			if ( added.Failed ) return OperationResult.Failure( added.Error!.Code, added.Error.Message );

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			var editor = unitOfWork.Edit( _repositories.Inventories, destinationDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "Destination inventory changed." );
			}
			editor.Replace( added.Value );
			unitOfWork.Save( editor );
			unitOfWork.Delete( _repositories.WorldItems, worldDocument );
			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceResultMapping.Failure( committed.Error! );

			_pickedUpEvents.Publish( new WorldItemPickedUpEvent( item.Id, destination.Id, committed.Value!.Sequence ) );
			return OperationResult.Success();
	}
}
