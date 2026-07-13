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
using Hexagon.V2.Persistence;

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
		var result = await DropCommittedAsync( actor, sourceId, itemId, transform, cancellationToken );
		return Untyped( result );
	}

	public async ValueTask<OperationResult<CommitReceipt>> DropCommittedAsync(
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
				return Failure( ErrorCode.NotFound, "Inventory or item was not found." );
			var source = sourceDocument.Value;
			var item = itemDocument.Value;
			if ( source.Find( item.Id ) is null )
				return Failure( ErrorCode.NotFound, "Item is not a member of the claimed source inventory." );
			var accessProof = _access.Prove(
				actor.ConnectionId,
				actor.CharacterId,
				source.Id,
				InventoryCapability.Move | InventoryCapability.Drop );
			if ( accessProof is null )
				return Failure( ErrorCode.Unauthorized, "Drop capability is missing." );

			if ( !_schema.Items.TryGet( item.Definition.Value, out var definition ) )
				return Failure( ErrorCode.UnknownDefinition, "Item definition is not registered." );
			if ( !definition!.CanDrop || string.IsNullOrWhiteSpace( definition.WorldModel ) )
				return Failure( ErrorCode.PolicyDenied, "Item is explicitly non-droppable." );
			if ( !_models.IsValidModel( definition.WorldModel ) )
				return Failure( ErrorCode.InvalidArgument, "Item world model does not resolve." );
			if ( _repositories.WorldItems.Find( DomainKeys.WorldItem( item.Id ) ) is not null )
				return Failure( ErrorCode.Conflict, "Item already has a world location." );

			var policy = _dropPolicy.Evaluate( new WorldDropContext( actor, source, item, transform ) );
			if ( policy.Failed ) return Failure( policy.Error!.Code, policy.Error.Message );
			var removed = _layout.Remove( source, item.Id );
			if ( removed.Failed ) return Failure( removed.Error!.Code, removed.Error.Message );
			var worldItem = new WorldItemRecord { ItemId = item.Id, Transform = transform, Revision = 0 };

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			unitOfWork.Require( accessProof );
			var editor = unitOfWork.Edit( _repositories.Inventories, sourceDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return Failure( ErrorCode.Conflict, "Inventory changed." );
			}
			editor.Replace( removed.Value );
			unitOfWork.Save( editor );
			unitOfWork.Create( _repositories.WorldItems, DomainKeys.WorldItem( item.Id ), worldItem );
			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceFailure( committed.Error! );

			_droppedEvents.Publish( new WorldItemDroppedEvent( worldItem, definition.WorldModel, committed.Value!.Sequence ) );
			return OperationResult<CommitReceipt>.Success( committed.Value! );
	}

	public async ValueTask<OperationResult> PickUpAsync(
		InventoryActor actor,
		ItemId itemId,
		InventoryId destinationId,
		CancellationToken cancellationToken = default )
	{
		var result = await PickUpCommittedAsync( actor, itemId, destinationId, cancellationToken );
		return Untyped( result );
	}

	public async ValueTask<OperationResult<CommitReceipt>> PickUpCommittedAsync(
		InventoryActor actor,
		ItemId itemId,
		InventoryId destinationId,
		CancellationToken cancellationToken = default )
	{
		var worldDocument = _repositories.WorldItems.Find( DomainKeys.WorldItem( itemId ) );
			var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
			var destinationDocument = _repositories.Inventories.Find( DomainKeys.Inventory( destinationId ) );
			if ( worldDocument is null || itemDocument is null || destinationDocument is null )
				return Failure( ErrorCode.NotFound, "World item, item or destination was not found." );
			var item = itemDocument.Value;
			var destination = destinationDocument.Value;
			var accessProof = _access.Prove(
				actor.ConnectionId,
				actor.CharacterId,
				destination.Id,
				InventoryCapability.Move | InventoryCapability.TransferIn );
			if ( accessProof is null )
				return Failure( ErrorCode.Unauthorized, "Pickup destination capability is missing." );

			var policy = _pickupPolicy.Evaluate( new WorldPickupContext( actor, destination, item, worldDocument.Value ) );
			if ( policy.Failed ) return Failure( policy.Error!.Code, policy.Error.Message );
			var firstFit = _layout.FindFirstFit( destination, item );
			if ( firstFit.Failed ) return Failure( firstFit.Error!.Code, firstFit.Error.Message );
			var added = _layout.AddAt( destination, item, firstFit.Value.X, firstFit.Value.Y );
			if ( added.Failed ) return Failure( added.Error!.Code, added.Error.Message );

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			unitOfWork.Require( accessProof );
			var editor = unitOfWork.Edit( _repositories.Inventories, destinationDocument );
			if ( editor is null )
			{
				await unitOfWork.DisposeAsync();
				return Failure( ErrorCode.Conflict, "Destination inventory changed." );
			}
			editor.Replace( added.Value );
			unitOfWork.Save( editor );
			unitOfWork.Delete( _repositories.WorldItems, worldDocument );
			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceFailure( committed.Error! );

			_pickedUpEvents.Publish( new WorldItemPickedUpEvent( item.Id, destination.Id, committed.Value!.Sequence ) );
			return OperationResult<CommitReceipt>.Success( committed.Value! );
	}

	private static OperationResult Untyped( OperationResult<CommitReceipt> result ) => result.Succeeded
		? OperationResult.Success()
		: OperationResult.Failure( result.Error!.Code, result.Error.Message );

	private static OperationResult<CommitReceipt> Failure( ErrorCode code, string message ) =>
		OperationResult<CommitReceipt>.Failure( code, message );

	private static OperationResult<CommitReceipt> PersistenceFailure( PersistenceError error )
	{
		var mapped = PersistenceResultMapping.Failure( error );
		return Failure( mapped.Error!.Code, mapped.Error.Message );
	}
}
