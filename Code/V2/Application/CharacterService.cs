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

/// <summary>
/// Owns the complete character creation/deletion transaction. It publishes only
/// immutable post-commit facts and never trusts an account identifier from a request.
/// </summary>
public sealed class CharacterService
{
	private const string MainInventoryRole = "main";
	private const string BagInventoryRole = "bag";
	private const string ClassCapacityReservationNamespace = "hexagon.class-capacity";

	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly ICharacterModelCatalog _models;
	private readonly ICharacterStateFactory _stateFactory;
	private readonly IReadOnlyList<ICharacterInitializer> _initializers;
	private readonly IAggregateIdGenerator _ids;
	private readonly IHexClock _clock;
	private readonly InventoryLayoutService _layout;
	private readonly PolicyPipeline<CharacterCreationContext> _creationPolicy;
	private readonly PolicyPipeline<CharacterDeletionContext> _deletionPolicy;
	private readonly PostCommitEventBus<CharacterCreatedEvent> _createdEvents;
	private readonly PostCommitEventBus<CharacterDeletedEvent> _deletedEvents;
	private readonly int _inventoryWidth;
	private readonly int _inventoryHeight;

	public CharacterService(
		DomainRepositories repositories,
		CompiledSchema schema,
		ICharacterModelCatalog models,
		ICharacterStateFactory stateFactory,
		IEnumerable<ICharacterInitializer> initializers,
		IAggregateIdGenerator ids,
		IHexClock clock,
		InventoryLayoutService layout,
		PolicyPipeline<CharacterCreationContext> creationPolicy,
		PolicyPipeline<CharacterDeletionContext> deletionPolicy,
		PostCommitEventBus<CharacterCreatedEvent>? createdEvents = null,
		PostCommitEventBus<CharacterDeletedEvent>? deletedEvents = null,
		int inventoryWidth = 8,
		int inventoryHeight = 6 )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_models = models ?? throw new ArgumentNullException( nameof(models) );
		_stateFactory = stateFactory ?? throw new ArgumentNullException( nameof(stateFactory) );
		_initializers = (initializers ?? throw new ArgumentNullException( nameof(initializers) ))
			.OrderBy( initializer => initializer.Order )
			.ThenBy( initializer => initializer.Id, StringComparer.Ordinal )
			.ToArray();
		_ids = ids ?? throw new ArgumentNullException( nameof(ids) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_layout = layout ?? throw new ArgumentNullException( nameof(layout) );
		_creationPolicy = creationPolicy ?? throw new ArgumentNullException( nameof(creationPolicy) );
		_deletionPolicy = deletionPolicy ?? throw new ArgumentNullException( nameof(deletionPolicy) );
		_createdEvents = createdEvents ?? new PostCommitEventBus<CharacterCreatedEvent>();
		_deletedEvents = deletedEvents ?? new PostCommitEventBus<CharacterDeletedEvent>();
		if ( inventoryWidth <= 0 ) throw new ArgumentOutOfRangeException( nameof(inventoryWidth) );
		if ( inventoryHeight <= 0 ) throw new ArgumentOutOfRangeException( nameof(inventoryHeight) );
		_inventoryWidth = inventoryWidth;
		_inventoryHeight = inventoryHeight;
	}

	public IReadOnlyList<CharacterRecord> ListForAccount( AccountId accountId ) =>
		_repositories.Characters.All()
			.Select( document => document.Value )
			.Where( character => character.AccountId == accountId )
			.OrderBy( character => character.Slot )
			.ToArray();

	public ValueTask<OperationResult<CharacterCreationReceipt>> CreateAsync(
		AccountId authenticatedAccount,
		CharacterCreationRequest request,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( request );
		return CreateCoreAsync( authenticatedAccount, request, cancellationToken );
	}

	public async ValueTask<OperationResult> DeleteAsync(
		AccountId authenticatedAccount,
		CharacterId characterId,
		CancellationToken cancellationToken = default )
	{
		var characterDocument = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
			if ( characterDocument is null || characterDocument.Value.AccountId != authenticatedAccount )
				return OperationResult.Failure( ErrorCode.NotFound, "Character was not found for the authenticated account." );

			var character = characterDocument.Value;
			var policy = _deletionPolicy.Evaluate( new CharacterDeletionContext( authenticatedAccount, character ) );
			if ( policy.Failed ) return policy;

			var inventories = FindOwnedInventoryGraph( character.Id );
			var ownedItemIds = inventories
				.SelectMany( inventory => inventory.Value.Placements )
				.Select( placement => placement.ItemId )
				.ToHashSet();
			var slotDocument = _repositories.CharacterSlots.Find(
				DomainKeys.CharacterSlot( character.AccountId, character.Slot ) );
			if ( slotDocument is null )
				return OperationResult.Failure( ErrorCode.Conflict, "Character owner-slot guard is missing." );
			var ownerIndexes = inventories
				.Select( inventory =>
				{
					var role = inventory.Value.Owner.Kind == InventoryOwnerKind.Character ? MainInventoryRole : BagInventoryRole;
					return _repositories.OwnerInventories.Find(
						DomainKeys.OwnerInventory( inventory.Value.Owner, role ) );
				} )
				.Where( document => document is not null )
				.Select( document => document! )
				.ToArray();
			var itemDocuments = ownedItemIds
				.Select( itemId => _repositories.Items.Find( DomainKeys.Item( itemId ) ) )
				.Where( document => document is not null )
				.Select( document => document! )
				.ToArray();
			var reservations = _repositories.UniqueReservations.All()
				.Where( document => document.Value.CharacterId == character.Id )
				.ToArray();
			var references = _repositories.CharacterReferences.All()
				.Where( document => document.Value.CharacterId == character.Id ||
					document.Value.RelatedCharacterId == character.Id )
				.ToArray();

			var unitOfWork = _repositories.Provider.BeginUnitOfWork();
			unitOfWork.Delete( _repositories.Characters, characterDocument );
			unitOfWork.Delete( _repositories.CharacterSlots, slotDocument );

			foreach ( var inventory in inventories )
				unitOfWork.Delete( _repositories.Inventories, inventory );
			foreach ( var ownerIndex in ownerIndexes )
				unitOfWork.Delete( _repositories.OwnerInventories, ownerIndex );

			foreach ( var itemDocument in itemDocuments )
				unitOfWork.Delete( _repositories.Items, itemDocument );

			foreach ( var reservation in reservations )
				unitOfWork.Delete( _repositories.UniqueReservations, reservation );

			foreach ( var reference in references )
				unitOfWork.Delete( _repositories.CharacterReferences, reference );

			var committed = await unitOfWork.CommitAsync( cancellationToken );
			await unitOfWork.DisposeAsync();
			if ( !committed.Succeeded ) return PersistenceResultMapping.Failure( committed.Error! );

			_deletedEvents.Publish( new CharacterDeletedEvent( character.Id, character.AccountId, committed.Value!.Sequence ) );
			return OperationResult.Success();
	}

	private async ValueTask<OperationResult<CharacterCreationReceipt>> CreateCoreAsync(
		AccountId authenticatedAccount,
		CharacterCreationRequest request,
		CancellationToken cancellationToken )
	{
		var allowedFields = _schema.CharacterFields.All
			.Where( definition => definition.ShowInCreation )
			.Select( definition => definition.Id )
			.ToHashSet( StringComparer.Ordinal );
		var basicValidation = CharacterRules.ValidateCreationRequest( request, allowedFields );
		if ( basicValidation.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure(
				basicValidation.Error!.Code, basicValidation.Error.Message );

		var fieldValidation = ValidateCreationFields( request );
		if ( fieldValidation.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure(
				fieldValidation.Error!.Code, fieldValidation.Error.Message );

		if ( !_schema.Factions.TryGet( request.Faction.Value, out var faction ) )
			return OperationResult<CharacterCreationReceipt>.Failure( ErrorCode.UnknownDefinition, "Unknown faction." );

		ClassId? selectedClass = request.Class;
		Kernel.Definitions.ClassDefinition? selectedClassDefinition = null;
		if ( selectedClass is null && faction!.DefaultClassId is not null )
			selectedClass = new ClassId( faction.DefaultClassId );

		if ( selectedClass is not null )
		{
			if ( !_schema.Classes.TryGet( selectedClass.Value.Value, out selectedClassDefinition ) ||
				!string.Equals( selectedClassDefinition!.FactionId, request.Faction.Value, StringComparison.Ordinal ) )
				return OperationResult<CharacterCreationReceipt>.Failure( ErrorCode.PolicyDenied, "Class is not valid for the selected faction." );
		}

		if ( !_models.IsAllowed( request.Model, request.Faction, selectedClass ) )
			return OperationResult<CharacterCreationReceipt>.Failure( ErrorCode.PolicyDenied, "Model is not allowed for the selected faction and class." );

		var normalizedRequest = request with
		{
			Name = request.Name.Trim(),
			Description = request.Description.Trim(),
			Class = selectedClass,
			Fields = new Dictionary<string, CreationValue>( request.Fields, StringComparer.Ordinal )
		};
		var ownedCharacters = ListForAccount( authenticatedAccount );
		var slot = CharacterRules.FindLowestFreeSlot( ownedCharacters );
		var now = _clock.UtcNow;
		var context = new CharacterCreationContext( authenticatedAccount, normalizedRequest, slot, now );
		var policy = _creationPolicy.Evaluate( context );
		if ( policy.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure( policy.Error!.Code, policy.Error.Message );

		var state = _stateFactory.Create( context );
		if ( state.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure( state.Error!.Code, state.Error.Message );
		if ( state.Value.StartingBalance < 0 )
			return OperationResult<CharacterCreationReceipt>.Failure( ErrorCode.InvalidArgument, "Starting balance cannot be negative." );
		var stateType = ValidateTypedPayload( state.Value.State );
		if ( stateType.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure( stateType.Error!.Code, stateType.Error.Message );

		var contributions = new List<CharacterInitializerContribution>();
		foreach ( var initializer in _initializers )
		{
			var contribution = initializer.Build( context, state.Value );
			if ( contribution.Failed )
				return OperationResult<CharacterCreationReceipt>.Failure(
					contribution.Error!.Code,
					$"Initializer '{initializer.Id}' failed: {contribution.Error.Message}" );
			contributions.Add( contribution.Value );
		}

		var characterId = _ids.NewCharacterId();
		var inventoryId = _ids.NewInventoryId();
		var character = new CharacterRecord
		{
			Id = characterId,
			AccountId = authenticatedAccount,
			Slot = slot,
			Name = normalizedRequest.Name,
			Description = normalizedRequest.Description,
			Model = normalizedRequest.Model,
			Faction = normalizedRequest.Faction,
			Class = selectedClass,
			Balance = state.Value.StartingBalance,
			CreatedAt = now,
			LastPlayedAt = now,
			SchemaState = state.Value.State.DeepCopy(),
			Revision = 0
		};
		var mainInventory = new InventoryRecord
		{
			Id = inventoryId,
			Owner = InventoryOwner.Character( characterId ),
			Width = _inventoryWidth,
			Height = _inventoryHeight,
			Placements = Array.Empty<InventoryPlacement>(),
			Revision = 0
		};

		var items = new List<ItemRecord>();
		var inventories = new List<InventoryRecord>();
		var ownerIndexes = new List<OwnerInventoryRecord>();
		var stagedDefinitions = new Dictionary<ItemId, DefinitionId>();
		var fill = FillInventory(
			mainInventory,
			contributions.SelectMany( contribution => contribution.Items ),
			items,
			inventories,
			ownerIndexes,
			stagedDefinitions );
		if ( fill.Failed )
			return OperationResult<CharacterCreationReceipt>.Failure( fill.Error!.Code, fill.Error.Message );
		mainInventory = fill.Value;
		inventories.Insert( 0, mainInventory );
		ownerIndexes.Insert( 0, new OwnerInventoryRecord
		{
			Role = MainInventoryRole,
			Owner = mainInventory.Owner,
			InventoryId = mainInventory.Id
		} );

		var reservationPlans = contributions.SelectMany( contribution => contribution.Reservations ).ToList();
		if ( selectedClassDefinition?.Capacity is int classCapacity )
		{
			var capacitySlot = Enumerable.Range( 0, classCapacity )
				.FirstOrDefault( candidate => _repositories.UniqueReservations.Find(
					DomainKeys.UniqueReservation(
						ClassCapacityReservationNamespace,
						$"{selectedClassDefinition.Id}.{candidate:D6}" ) ) is null, -1 );
			if ( capacitySlot < 0 )
				return OperationResult<CharacterCreationReceipt>.Failure(
					ErrorCode.Conflict, $"Class '{selectedClassDefinition.Id}' is at capacity." );
			reservationPlans.Add( new UniqueReservationPlan(
				ClassCapacityReservationNamespace,
				$"{selectedClassDefinition.Id}.{capacitySlot:D6}" ) );
		}
		var reservationKeys = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var reservation in reservationPlans )
		{
			var key = DomainKeys.UniqueReservation( reservation.Namespace, reservation.Value );
			if ( !reservationKeys.Add( key ) || _repositories.UniqueReservations.Find( key ) is not null )
				return OperationResult<CharacterCreationReceipt>.Failure(
					ErrorCode.Conflict, $"Unique value '{reservation.Namespace}' is already reserved." );
		}

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Create( _repositories.CharacterSlots, DomainKeys.CharacterSlot( authenticatedAccount, slot ), new CharacterSlotRecord
		{
			AccountId = authenticatedAccount,
			Slot = slot,
			CharacterId = character.Id
		} );
		unitOfWork.Create( _repositories.Characters, DomainKeys.Character( character.Id ), character );

		foreach ( var inventory in inventories )
			unitOfWork.Create( _repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
		foreach ( var ownerIndex in ownerIndexes )
			unitOfWork.Create(
				_repositories.OwnerInventories,
				DomainKeys.OwnerInventory( ownerIndex.Owner, ownerIndex.Role ),
				ownerIndex );
		foreach ( var item in items )
			unitOfWork.Create( _repositories.Items, DomainKeys.Item( item.Id ), item );
		foreach ( var reservation in reservationPlans )
			unitOfWork.Create(
				_repositories.UniqueReservations,
				DomainKeys.UniqueReservation( reservation.Namespace, reservation.Value ),
				new UniqueReservationRecord
				{
					Namespace = reservation.Namespace,
					Value = reservation.Value,
					CharacterId = character.Id
				} );

		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded ) return PersistenceResultMapping.Failure<CharacterCreationReceipt>( committed.Error! );

		var receipt = new CharacterCreationReceipt( character, mainInventory, items.ToArray() );
		_createdEvents.Publish( new CharacterCreatedEvent(
			character, mainInventory, items.ToArray(), committed.Value!.Sequence ) );
		return OperationResult<CharacterCreationReceipt>.Success( receipt );
	}

	private OperationResult<InventoryRecord> FillInventory(
		InventoryRecord destination,
		IEnumerable<ItemGrantPlan> grants,
		ICollection<ItemRecord> items,
		ICollection<InventoryRecord> childInventories,
		ICollection<OwnerInventoryRecord> ownerIndexes,
		IDictionary<ItemId, DefinitionId> stagedDefinitions )
	{
		var current = destination;
		foreach ( var grant in grants )
		{
			if ( !_schema.Items.TryGet( grant.Definition.Value, out _ ) )
				return OperationResult<InventoryRecord>.Failure(
					ErrorCode.UnknownDefinition, $"Initializer requested unknown item '{grant.Definition}'." );
			foreach ( var trait in grant.Traits )
			{
				var traitType = ValidateTypedPayload( trait.Value );
				if ( traitType.Failed )
					return OperationResult<InventoryRecord>.Failure(
						traitType.Error!.Code, $"Trait '{trait.Key}' is invalid: {traitType.Error.Message}" );
			}

			var item = new ItemRecord
			{
				Id = _ids.NewItemId(),
				Definition = grant.Definition,
				Traits = grant.Traits.ToDictionary(
					pair => pair.Key,
					pair => pair.Value.DeepCopy(),
					StringComparer.Ordinal ),
				Revision = 0
			};
			if ( !stagedDefinitions.TryAdd( item.Id, item.Definition ) )
				return OperationResult<InventoryRecord>.Failure( ErrorCode.Conflict, "ID generator produced a duplicate item ID." );

			var firstFit = _layout.FindFirstFit( current, item, (IReadOnlyDictionary<ItemId, DefinitionId>)stagedDefinitions );
			if ( firstFit.Failed )
				return OperationResult<InventoryRecord>.Failure( firstFit.Error!.Code, firstFit.Error.Message );
			var added = _layout.AddAt(
				current, item, firstFit.Value.X, firstFit.Value.Y,
				(IReadOnlyDictionary<ItemId, DefinitionId>)stagedDefinitions );
			if ( added.Failed )
				return OperationResult<InventoryRecord>.Failure( added.Error!.Code, added.Error.Message );
			current = added.Value;
			items.Add( item );

			if ( grant.Bag is null ) continue;
			if ( grant.Bag.Width <= 0 || grant.Bag.Height <= 0 )
				return OperationResult<InventoryRecord>.Failure( ErrorCode.InvalidArgument, "Bag dimensions must be positive." );
			var bagInventory = new InventoryRecord
			{
				Id = _ids.NewInventoryId(),
				Owner = InventoryOwner.ParentItem( item.Id ),
				Width = grant.Bag.Width,
				Height = grant.Bag.Height,
				Placements = Array.Empty<InventoryPlacement>(),
				Revision = 0
			};
			var filledBag = FillInventory(
				bagInventory, grant.Bag.Items, items, childInventories, ownerIndexes, stagedDefinitions );
			if ( filledBag.Failed ) return filledBag;
			childInventories.Add( filledBag.Value );
			ownerIndexes.Add( new OwnerInventoryRecord
			{
				Role = BagInventoryRole,
				Owner = filledBag.Value.Owner,
				InventoryId = filledBag.Value.Id
			} );
		}

		return OperationResult<InventoryRecord>.Success( current );
	}

	private OperationResult ValidateCreationFields( CharacterCreationRequest request )
	{
		foreach ( var definition in _schema.CharacterFields.All.Where( field => field.ShowInCreation ) )
		{
			if ( !request.Fields.TryGetValue( definition.Id, out var value ) )
			{
				if ( definition.Required && definition.DefaultValue is null )
					return OperationResult.Failure( ErrorCode.InvalidArgument, $"Creation field '{definition.Id}' is required." );
				continue;
			}

			var validType = value.Kind switch
			{
				CreationValueKind.String => definition.ValueKind == Kernel.Definitions.CharacterFieldValueKind.String,
				CreationValueKind.Choice => definition.ValueKind == Kernel.Definitions.CharacterFieldValueKind.Choice,
				CreationValueKind.Integer => definition.ValueKind == Kernel.Definitions.CharacterFieldValueKind.Integer,
				CreationValueKind.Boolean => definition.ValueKind == Kernel.Definitions.CharacterFieldValueKind.Boolean,
				_ => false
			};
			if ( !validType )
				return OperationResult.Failure( ErrorCode.InvalidArgument, $"Creation field '{definition.Id}' has the wrong type." );
		}

		return OperationResult.Success();
	}

	private OperationResult ValidateTypedPayload( TypedPayload payload )
	{
		if ( !_schema.PersistedTypes.TryGet( payload.TypeId.Value, out var registration ) )
			return OperationResult.Failure(
				ErrorCode.PersistedTypeInvalid, $"Persisted type '{payload.TypeId}' is not registered." );
		var registeredVersion = registration!.Version;
		if ( payload.TypeVersion <= 0 || payload.TypeVersion > registeredVersion )
			return OperationResult.Failure(
				ErrorCode.PersistedTypeInvalid,
				$"Payload '{payload.TypeId}' version {payload.TypeVersion} is incompatible with registered version {registeredVersion}." );
		return OperationResult.Success();
	}

	private IReadOnlyList<DocumentSnapshot<InventoryRecord>> FindOwnedInventoryGraph( CharacterId characterId )
	{
		var all = _repositories.Inventories.All().ToArray();
		var owned = new List<DocumentSnapshot<InventoryRecord>>();
		var itemFrontier = new Queue<ItemId>();
		foreach ( var inventory in all.Where( candidate =>
			candidate.Value.Owner.Kind == InventoryOwnerKind.Character &&
			candidate.Value.Owner.OwnerId == characterId.Value ) )
		{
			owned.Add( inventory );
			foreach ( var placement in inventory.Value.Placements ) itemFrontier.Enqueue( placement.ItemId );
		}

		var seenInventories = owned.Select( inventory => inventory.Value.Id ).ToHashSet();
		while ( itemFrontier.Count > 0 )
		{
			var parentItem = itemFrontier.Dequeue();
			foreach ( var child in all.Where( candidate =>
				candidate.Value.Owner.Kind == InventoryOwnerKind.ParentItem &&
				candidate.Value.Owner.OwnerId == parentItem.Value ) )
			{
				if ( !seenInventories.Add( child.Value.Id ) ) continue;
				owned.Add( child );
				foreach ( var placement in child.Value.Placements ) itemFrontier.Enqueue( placement.ItemId );
			}
		}

		return owned;
	}
}
