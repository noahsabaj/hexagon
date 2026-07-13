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

	public async ValueTask<OperationResult> MoveAsync(
		InventoryActor actor,
		InventoryId sourceId,
		InventoryId targetId,
		ItemId itemId,
		int x,
		int y,
		CancellationToken cancellationToken = default )
	{
		var sourceDocument = _repositories.Inventories.Find( DomainKeys.Inventory( sourceId ) );
			var targetDocument = sourceId == targetId
				? sourceDocument
				: _repositories.Inventories.Find( DomainKeys.Inventory( targetId ) );
			var itemDocument = _repositories.Items.Find( DomainKeys.Item( itemId ) );
			if ( sourceDocument is null || targetDocument is null || itemDocument is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Inventory or item was not found." );

			var source = sourceDocument.Value;
			var target = targetDocument.Value;
			var item = itemDocument.Value;
			if ( source.Find( item.Id ) is null )
				return OperationResult.Failure( ErrorCode.NotFound, "Item is not a member of the claimed source inventory." );

			var sourceCapability = source.Id == target.Id
				? InventoryCapability.Move
				: InventoryCapability.Move | InventoryCapability.TransferOut;
			if ( !_access.Has( actor.ConnectionId, actor.CharacterId, source.Id, sourceCapability ) )
				return OperationResult.Failure( ErrorCode.Unauthorized, "Source inventory capability is missing." );
			if ( source.Id != target.Id &&
				!_access.Has( actor.ConnectionId, actor.CharacterId, target.Id, InventoryCapability.TransferIn ) )
				return OperationResult.Failure( ErrorCode.Unauthorized, "Destination inventory capability is missing." );

			if ( source.Id != target.Id && WouldCreateBagCycle( item.Id, target ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, "A bag cannot be placed inside itself or one of its descendants." );

			var policy = _policy.Evaluate( new InventoryTransferContext( actor, source, target, item ) );
			if ( policy.Failed ) return policy;

			var changed = _layout.Transfer( source, target, item, x, y );
			if ( changed.Failed ) return OperationResult.Failure( changed.Error!.Code, changed.Error.Message );

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			var sourceEditor = unitOfWork.Edit( _repositories.Inventories, sourceDocument );
			if ( sourceEditor is null )
			{
				await unitOfWork.DisposeAsync();
				return OperationResult.Failure( ErrorCode.Conflict, "Source inventory changed." );
			}
			sourceEditor.Replace( changed.Value.Source );
			unitOfWork.Save( sourceEditor );

			if ( source.Id != target.Id )
			{
				var targetEditor = unitOfWork.Edit( _repositories.Inventories, targetDocument );
				if ( targetEditor is null )
				{
					await unitOfWork.DisposeAsync();
					return OperationResult.Failure( ErrorCode.Conflict, "Destination inventory changed." );
				}
				targetEditor.Replace( changed.Value.Target );
				unitOfWork.Save( targetEditor );
			}

			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceResultMapping.Failure( committed.Error! );
			_events.Publish( new ItemMovedEvent( item.Id, source.Id, target.Id, committed.Value!.Sequence ) );
			return OperationResult.Success();
	}

	private bool WouldCreateBagCycle( ItemId movingItem, InventoryRecord destination )
	{
		var current = destination;
		var visited = new HashSet<InventoryId>();
		while ( visited.Add( current.Id ) && current.Owner.Kind == InventoryOwnerKind.ParentItem )
		{
			var parentItem = new ItemId( current.Owner.OwnerId );
			if ( parentItem == movingItem ) return true;
			var parentInventory = _repositories.Inventories.All()
				.Select( document => document.Value )
				.FirstOrDefault( inventory => inventory.Find( parentItem ) is not null );
			if ( parentInventory is null ) break;
			current = parentInventory;
		}

		return false;
	}
}
