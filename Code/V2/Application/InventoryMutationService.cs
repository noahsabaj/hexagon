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
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

public sealed record InventoryActor( ConnectionId ConnectionId, AccountId AccountId, CharacterId CharacterId );

public sealed record InventoryTransferContext(
	InventoryActor Actor,
	InventoryRecord Source,
	InventoryRecord Target,
	ItemRecord Item );

public sealed record ItemMovedEvent(
	ItemId ItemId,
	InventoryId SourceId,
	InventoryId TargetId,
	long CommitSequence );

/// <summary>
/// Authoritative item movement boundary. It proves source membership, checks both
/// transient capabilities, rejects bag cycles and commits both inventories once.
/// </summary>
public sealed class InventoryMutationService
{
	private readonly DomainRepositories _repositories;
	private readonly InventoryAccessService _access;
	private readonly InventoryLayoutService _layout;
	private readonly PolicyPipeline<InventoryTransferContext> _policy;
	private readonly PostCommitEventBus<ItemMovedEvent> _events;

	public InventoryMutationService(
		DomainRepositories repositories,
		InventoryAccessService access,
		InventoryLayoutService layout,
		PolicyPipeline<InventoryTransferContext> policy,
		PostCommitEventBus<ItemMovedEvent>? events = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_access = access ?? throw new ArgumentNullException( nameof(access) );
		_layout = layout ?? throw new ArgumentNullException( nameof(layout) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_events = events ?? new PostCommitEventBus<ItemMovedEvent>();
	}

	public ValueTask<OperationResult> MoveAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		int x,
		int y,
		CancellationToken cancellationToken = default ) =>
		MoveUntypedAsync( actor, sourceId, targetId, itemId, new InventoryGridPosition( x, y ), cancellationToken );

	public ValueTask<OperationResult> MoveAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position,
		CancellationToken cancellationToken = default ) =>
		MoveUntypedAsync( actor, sourceId, targetId, itemId, position, cancellationToken );

	/// <summary>
	/// Performs the move and returns the provider-issued receipt verbatim. Runtime
	/// projection layers use this boundary so they never reconstruct commit metadata
	/// from command arguments or post-commit repository state.
	/// </summary>
	public ValueTask<OperationResult<CommitReceipt>> MoveCommittedAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		int x,
		int y,
		CancellationToken cancellationToken = default ) =>
		MoveCommittedAsync( actor, sourceId, targetId, itemId,
			new InventoryGridPosition( x, y ), cancellationToken );

	public async ValueTask<OperationResult<CommitReceipt>> MoveCommittedAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position,
		CancellationToken cancellationToken = default )
	{
		var sourceDocument = _repositories.Inventories.Find( DomainKeys.Inventory( sourceId ) );
			var targetDocument = sourceId == targetId
				? sourceDocument
				: _repositories.Inventories.Find( DomainKeys.Inventory( targetId ) );
			var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
			if ( sourceDocument is null || targetDocument is null || itemDocument is null )
				return Failure( ErrorCode.NotFound, "Inventory or item was not found." );

			var source = sourceDocument.Value;
			var target = targetDocument.Value;
			var item = itemDocument.Value;
			if ( source.Find( item.Id ) is null )
				return Failure( ErrorCode.NotFound, "Item is not a member of the claimed source inventory." );

			var sourceCapability = source.Id == target.Id
				? InventoryCapability.Move
				: InventoryCapability.Move | InventoryCapability.TransferOut;
			var sourceAccess = _access.Prove(
				actor.ConnectionId, actor.CharacterId, source.Id, sourceCapability );
			if ( sourceAccess is null )
				return Failure( ErrorCode.Unauthorized, "Source inventory capability is missing." );
			InventoryAccessProof? targetAccess = null;
			if ( source.Id != target.Id )
			{
				targetAccess = _access.Prove(
					actor.ConnectionId, actor.CharacterId, target.Id, InventoryCapability.TransferIn );
				if ( targetAccess is null )
					return Failure( ErrorCode.Unauthorized, "Destination inventory capability is missing." );
			}

			if ( source.Id != target.Id && WouldCreateBagCycle( item.Id, target ) )
				return Failure( ErrorCode.InvalidArgument, "A bag cannot be placed inside itself or one of its descendants." );

			var policy = _policy.Evaluate( new InventoryTransferContext( actor, source, target, item ) );
			if ( policy.Failed ) return Failure( policy.Error!.Code, policy.Error.Message );

			var changed = _layout.Transfer( source, target, item, position );
			if ( changed.Failed ) return Failure( changed.Error!.Code, changed.Error.Message );

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			unitOfWork.Require( sourceAccess );
			if ( targetAccess is not null ) unitOfWork.Require( targetAccess );
			var sourceEditor = unitOfWork.Edit( _repositories.Inventories, sourceDocument );
			if ( sourceEditor is null )
			{
				await unitOfWork.DisposeAsync();
				return Failure( ErrorCode.Conflict, "Source inventory changed." );
			}
			sourceEditor.Replace( changed.Value.Source );
			unitOfWork.Save( sourceEditor );

			if ( source.Id != target.Id )
			{
				var targetEditor = unitOfWork.Edit( _repositories.Inventories, targetDocument );
				if ( targetEditor is null )
				{
					await unitOfWork.DisposeAsync();
					return Failure( ErrorCode.Conflict, "Destination inventory changed." );
				}
				targetEditor.Replace( changed.Value.Target );
				unitOfWork.Save( targetEditor );
			}

			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded )
				return Failure( PersistenceResultMapping.Failure( committed.Error! ).Error! );
			_events.Publish( new ItemMovedEvent( item.Id, source.Id, target.Id, committed.Value!.Sequence ) );
			return OperationResult<CommitReceipt>.Success( committed.Value );
	}

	private async ValueTask<OperationResult> MoveUntypedAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		InventoryGridPosition position,
		CancellationToken cancellationToken )
	{
		var result = await MoveCommittedAsync(
			actor, sourceId, targetId, itemId, position, cancellationToken );
		return result.Succeeded
			? OperationResult.Success()
			: OperationResult.Failure( result.Error!.Code, result.Error.Message );
	}

	private static OperationResult<CommitReceipt> Failure( ErrorCode code, string message ) =>
		OperationResult<CommitReceipt>.Failure( code, message );

	private static OperationResult<CommitReceipt> Failure( OperationError error ) =>
		Failure( error.Code, error.Message );

	private bool WouldCreateBagCycle( ItemId movingItem, InventoryRecord destination )
	{
		// Only a bag inventory can sit inside the moving item's subtree; character and
		// scene-entity inventories are roots and can never close a cycle.
		if ( destination.Owner.Kind != InventoryOwnerKind.ParentItem ) return false;

		// A cycle arises exactly when the destination lies in the moving item's own bag
		// subtree, so descend from the moving item with keyed owner-index probes (one
		// canonical owner record per inventory) instead of scanning the whole store to
		// walk ancestors upward.
		var pendingContainers = new Stack<ItemId>();
		pendingContainers.Push( movingItem );
		var visited = new HashSet<ItemId>();
		while ( pendingContainers.Count > 0 )
		{
			var containerItem = pendingContainers.Pop();
			if ( !visited.Add( containerItem ) ) continue;
			var ownerIndex = _repositories.OwnerInventories.Find( DomainKeys.OwnerInventory(
				InventoryOwner.ParentItem( containerItem ), InventoryRoles.Bag ) );
			if ( ownerIndex is null ) continue;
			if ( ownerIndex.Value.InventoryId == destination.Id ) return true;
			var bag = _repositories.Inventories.Find( DomainKeys.Inventory( ownerIndex.Value.InventoryId ) );
			if ( bag is null ) continue;
			foreach ( var placement in bag.Value.Placements )
				pendingContainers.Push( placement.ItemId );
		}

		return false;
	}
}
