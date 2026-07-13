#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

public sealed record DomainInvariantIssue( ErrorCode Code, string Path, string Message );

public sealed class DomainInvariantReport
{
	public DomainInvariantReport( IReadOnlyList<DomainInvariantIssue> issues ) => Issues = issues;
	public IReadOnlyList<DomainInvariantIssue> Issues { get; }
	public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Neutral startup/test validator for cross-document invariants that cannot be
/// represented by a single aggregate revision.
/// </summary>
public sealed class DomainInvariantValidator
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly IItemShapeCatalog _shapes;
	private readonly SchemaPersistenceInvariantProfile _persistenceProfile;

	public DomainInvariantValidator(
		DomainRepositories repositories,
		CompiledSchema schema,
		IItemShapeCatalog shapes,
		SchemaPersistenceInvariantProfile persistenceProfile )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_shapes = shapes ?? throw new ArgumentNullException( nameof(shapes) );
		_persistenceProfile = persistenceProfile ?? throw new ArgumentNullException( nameof(persistenceProfile) );
	}

	public DomainInvariantReport Validate()
	{
		var issues = new List<DomainInvariantIssue>();
		ValidateProfile( issues );
		var characterDocuments = _repositories.Characters.All();
		var slotDocuments = _repositories.CharacterSlots.All();
		var inventoryDocuments = _repositories.Inventories.All();
		var ownerInventoryDocuments = _repositories.OwnerInventories.All();
		var itemDocuments = _repositories.Items.All();
		var worldItemDocuments = _repositories.WorldItems.All();
		var reservationDocuments = _repositories.UniqueReservations.All();
		var referenceDocuments = _repositories.CharacterReferences.All();
		var sceneEntityDocuments = _repositories.SceneEntities.All();
		var characters = characterDocuments.Select( value => value.Value ).ToArray();
		var inventories = inventoryDocuments.Select( value => value.Value ).ToArray();
		var items = itemDocuments
			.GroupBy( value => value.Value.Id )
			.ToDictionary( group => group.Key, group => group.First().Value );
		var sceneEntities = sceneEntityDocuments.Select( value => value.Value ).ToArray();

		ValidateLogicalAggregateIds(
			characterDocuments, inventoryDocuments, itemDocuments, worldItemDocuments, sceneEntityDocuments, issues );
		ValidateCanonicalKeys(
			characterDocuments, slotDocuments, inventoryDocuments, itemDocuments,
			worldItemDocuments, sceneEntityDocuments, issues );
		ValidateCharacters( characters, slotDocuments, issues );
		ValidateReservations( characters, reservationDocuments, issues );
		ValidateOwnerInventories(
			characters, inventories, items, sceneEntities, ownerInventoryDocuments, issues );
		ValidateNestedPayloads(
			characterDocuments, itemDocuments, referenceDocuments, sceneEntityDocuments, issues );
		ValidateReferenceTargets( characters, sceneEntities, referenceDocuments, issues );
		ValidateInventories( inventories, items, issues );
		ValidateLocations( inventories, items, worldItemDocuments.Select( value => value.Value ), issues );
		ValidateBagGraph( inventories, issues );
		return new DomainInvariantReport( issues );
	}

	private static void ValidateLogicalAggregateIds(
		IEnumerable<DocumentSnapshot<CharacterRecord>> characters,
		IEnumerable<DocumentSnapshot<InventoryRecord>> inventories,
		IEnumerable<DocumentSnapshot<ItemRecord>> items,
		IEnumerable<DocumentSnapshot<WorldItemRecord>> worldItems,
		IEnumerable<DocumentSnapshot<PersistentSceneEntityRecord>> sceneEntities,
		ICollection<DomainInvariantIssue> issues )
	{
		ReportDuplicateIds( characters, value => value.Id.Value, "character", issues );
		ReportDuplicateIds( inventories, value => value.Id.Value, "inventory", issues );
		ReportDuplicateIds( items, value => value.Id.Value, "item", issues );
		ReportDuplicateIds( worldItems, value => value.ItemId.Value, "world-item", issues );
		ReportDuplicateIds( sceneEntities, value => value.Id.Value, "scene-entity", issues );
	}

	private static void ReportDuplicateIds<T>(
		IEnumerable<DocumentSnapshot<T>> documents,
		Func<T, Guid> id,
		string subject,
		ICollection<DomainInvariantIssue> issues ) where T : class
	{
		foreach ( var duplicate in documents.GroupBy( document => id( document.Value ) ).Where( group => group.Count() > 1 ) )
			issues.Add( new DomainInvariantIssue(
				ErrorCode.Conflict,
				$"{subject}/{duplicate.Key:N}",
				$"Logical {subject} ID is duplicated across outer documents." ) );
	}

	private void ValidateProfile( ICollection<DomainInvariantIssue> issues )
	{
		ValidateExpectedType( _persistenceProfile.CharacterState, "persistence-profile/character-state", issues );
		foreach ( var item in _schema.Items.All )
			if ( !_persistenceProfile.Items.ContainsKey( new DefinitionId( item.Id ) ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					$"persistence-profile/items/{item.Id}",
					"Registered item definition has no persistence contract." ) );
		foreach ( var contract in _persistenceProfile.Items.Values )
		{
			if ( !_schema.Items.Contains( contract.Definition.Value ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.UnknownDefinition,
					$"persistence-profile/items/{contract.Definition}",
					"Persistence contract refers to an unknown item definition." ) );
			foreach ( var trait in contract.Traits )
				ValidateExpectedType(
					trait.Value,
					$"persistence-profile/items/{contract.Definition}/traits/{trait.Key}",
					issues );
		}
		foreach ( var contract in _persistenceProfile.CharacterReferences.Values )
			ValidateExpectedType(
				contract.StateType,
				$"persistence-profile/character-references/{contract.Category}",
				issues );
		foreach ( var contract in _persistenceProfile.SceneEntities )
			ValidateExpectedType(
				contract.Value,
				$"persistence-profile/scene-entities/{contract.Key}",
				issues );
	}

	private void ValidateExpectedType(
		PersistedTypeId expectedType,
		string path,
		ICollection<DomainInvariantIssue> issues )
	{
		if ( !_schema.PersistedTypes.TryGet( expectedType.Value, out var registration ) )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid, path, "Expected nested payload type is not registered by the schema." ) );
			return;
		}
		try
		{
			var codec = _repositories.Provider.Types.Resolve( new PersistedTypeKey( expectedType.Value ) );
			if ( codec.CurrentVersion != registration!.Version || codec.ClrType != registration.ClrType )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					path,
					"Expected nested payload type has an incompatible registered codec." ) );
		}
		catch ( Exception exception ) when ( exception is KeyNotFoundException or ArgumentException )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid, path, "Expected nested payload type has no registered codec." ) );
		}
	}

	private static void ValidateCharacters(
		IReadOnlyCollection<CharacterRecord> characters,
		IEnumerable<DocumentSnapshot<CharacterSlotRecord>> slotDocuments,
		ICollection<DomainInvariantIssue> issues )
	{
		var slots = slotDocuments.ToArray();
		var logicalSlots = new HashSet<(AccountId AccountId, int Slot)>();
		foreach ( var document in slots )
		{
			var slot = document.Value;
			if ( !logicalSlots.Add( (slot.AccountId, slot.Slot) ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character-slot/{document.Key}",
					"Logical owner-slot guard is duplicated." ) );
			var referenced = characters.Where( value => value.Id == slot.CharacterId ).ToArray();
			if ( referenced.Length != 1 )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-slot/{document.Key}",
					$"Owner-slot guard references {referenced.Length} characters; expected exactly one." ) );
				continue;
			}
			if ( referenced[0].AccountId != slot.AccountId || referenced[0].Slot != slot.Slot )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character-slot/{document.Key}",
					"Owner-slot guard does not match its character's account and slot." ) );
		}

		foreach ( var character in characters )
		{
			var matching = slots.Count( document =>
				document.Value.AccountId == character.AccountId &&
				document.Value.Slot == character.Slot &&
				document.Value.CharacterId == character.Id );
			if ( matching != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character/{character.Id}",
					$"Character has {matching} matching owner-slot guards; expected exactly one." ) );
		}
	}

	private static void ValidateReservations(
		IReadOnlyCollection<CharacterRecord> characters,
		IEnumerable<DocumentSnapshot<UniqueReservationRecord>> reservationDocuments,
		ICollection<DomainInvariantIssue> issues )
	{
		var logicalReservations = new HashSet<(string Namespace, string Value)>();
		foreach ( var document in reservationDocuments )
		{
			var reservation = document.Value;
			string canonicalKey;
			try
			{
				canonicalKey = DomainKeys.UniqueReservation( reservation.Namespace, reservation.Value );
			}
			catch ( ArgumentException exception )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument, $"unique-reservation/{document.Key}", exception.Message ) );
				continue;
			}
			ValidateKey( document.Key, canonicalKey, "unique-reservation", issues );
			var logical = (reservation.Namespace.Trim().ToLowerInvariant(), reservation.Value.Trim().ToLowerInvariant());
			if ( !logicalReservations.Add( logical ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"unique-reservation/{document.Key}",
					"Logical unique reservation is duplicated." ) );
			if ( characters.Count( character => character.Id == reservation.CharacterId ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"unique-reservation/{document.Key}",
					"Unique reservation references a missing character." ) );
		}
	}

	private static void ValidateCanonicalKeys(
		IEnumerable<DocumentSnapshot<CharacterRecord>> characters,
		IEnumerable<DocumentSnapshot<CharacterSlotRecord>> slots,
		IEnumerable<DocumentSnapshot<InventoryRecord>> inventories,
		IEnumerable<DocumentSnapshot<ItemRecord>> items,
		IEnumerable<DocumentSnapshot<WorldItemRecord>> worldItems,
		IEnumerable<DocumentSnapshot<PersistentSceneEntityRecord>> sceneEntities,
		ICollection<DomainInvariantIssue> issues )
	{
		foreach ( var document in characters )
			ValidateKey( document.Key, DomainKeys.Character( document.Value.Id ), "character", issues );
		foreach ( var document in slots )
			ValidateKey(
				document.Key,
				DomainKeys.CharacterSlot( document.Value.AccountId, document.Value.Slot ),
				"character-slot",
				issues );
		foreach ( var document in inventories )
			ValidateKey( document.Key, DomainKeys.Inventory( document.Value.Id ), "inventory", issues );
		foreach ( var document in items )
			ValidateKey( document.Key, DomainKeys.Item( document.Value.Id ), "item", issues );
		foreach ( var document in worldItems )
			ValidateKey( document.Key, DomainKeys.WorldItem( document.Value.ItemId ), "world-item", issues );
		foreach ( var document in sceneEntities )
			ValidateKey( document.Key, DomainKeys.SceneEntity( document.Value.Id ), "scene-entity", issues );
	}

	private static void ValidateKey(
		string actual,
		string expected,
		string collection,
		ICollection<DomainInvariantIssue> issues )
	{
		if ( !string.Equals( actual, expected, StringComparison.Ordinal ) )
			issues.Add( new DomainInvariantIssue(
				ErrorCode.Conflict,
				$"{collection}/{actual}",
				$"Document key is not canonical; expected '{expected}'." ) );
	}

	private static void ValidateOwnerInventories(
		IReadOnlyCollection<CharacterRecord> characters,
		IReadOnlyCollection<InventoryRecord> inventories,
		IReadOnlyDictionary<ItemId, ItemRecord> items,
		IReadOnlyCollection<PersistentSceneEntityRecord> sceneEntities,
		IEnumerable<DocumentSnapshot<OwnerInventoryRecord>> ownerIndexes,
		ICollection<DomainInvariantIssue> issues )
	{
		var inventoriesById = inventories
			.GroupBy( inventory => inventory.Id )
			.ToDictionary( group => group.Key, group => group.First() );
		var indexesByInventory = new Dictionary<InventoryId, int>();
		var logicalIndexes = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var document in ownerIndexes )
		{
			var index = document.Value;
			string canonicalKey;
			try
			{
				canonicalKey = DomainKeys.OwnerInventory( index.Owner, index.Role );
			}
			catch ( ArgumentException exception )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument, $"owner-inventory/{document.Key}", exception.Message ) );
				continue;
			}
			ValidateKey( document.Key, canonicalKey, "owner-inventory", issues );
			if ( !logicalIndexes.Add( canonicalKey ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict, $"owner-inventory/{document.Key}", "Logical owner inventory index is duplicated." ) );
			if ( !inventoriesById.TryGetValue( index.InventoryId, out var inventory ) )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound, $"owner-inventory/{document.Key}", "Owner index references a missing inventory." ) );
				continue;
			}
			indexesByInventory[index.InventoryId] = indexesByInventory.GetValueOrDefault( index.InventoryId ) + 1;
			if ( inventory.Owner != index.Owner )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict, $"owner-inventory/{document.Key}", "Owner index does not match the inventory owner." ) );
			var expectedRole = ExpectedInventoryRole( inventory.Owner.Kind );
			if ( !string.Equals( index.Role, expectedRole, StringComparison.Ordinal ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"owner-inventory/{document.Key}",
					$"Inventory owner kind requires role '{expectedRole}', not '{index.Role}'." ) );
		}

		foreach ( var inventory in inventories )
		{
			var count = indexesByInventory.GetValueOrDefault( inventory.Id );
			if ( count != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"inventory/{inventory.Id}/owner-index",
					$"Expected exactly one canonical owner index, found {count}." ) );
			var ownerExists = inventory.Owner.Kind switch
			{
				InventoryOwnerKind.Character => characters.Any( value => value.Id.Value == inventory.Owner.OwnerId ),
				InventoryOwnerKind.ParentItem => items.ContainsKey( new ItemId( inventory.Owner.OwnerId ) ),
				InventoryOwnerKind.SceneEntity => sceneEntities.Any( value => value.Id.Value == inventory.Owner.OwnerId ),
				_ => false
			};
			if ( !ownerExists )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound, $"inventory/{inventory.Id}/owner", "Inventory owner does not exist." ) );
		}

		foreach ( var character in characters )
		{
			var mainInventories = inventories.Count( inventory =>
				inventory.Owner == InventoryOwner.Character( character.Id ) );
			var mainIndexes = logicalIndexes.Contains(
				DomainKeys.OwnerInventory( InventoryOwner.Character( character.Id ), "main" ) ) ? 1 : 0;
			if ( mainInventories != 1 || mainIndexes != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character/{character.Id}/inventory",
					$"Expected one main inventory and canonical index, found {mainInventories} inventories and {mainIndexes} indexes." ) );
		}
	}

	private void ValidateNestedPayloads(
		IEnumerable<DocumentSnapshot<CharacterRecord>> characters,
		IEnumerable<DocumentSnapshot<ItemRecord>> items,
		IEnumerable<DocumentSnapshot<CharacterReferenceRecord>> references,
		IEnumerable<DocumentSnapshot<PersistentSceneEntityRecord>> sceneEntities,
		ICollection<DomainInvariantIssue> issues )
	{
		foreach ( var character in characters )
			ValidatePayload(
				character.Value.SchemaState,
				_persistenceProfile.CharacterState,
				$"character/{character.Key}/schema-state",
				issues );

		foreach ( var item in items )
		{
			if ( !_persistenceProfile.Items.TryGetValue( item.Value.Definition, out var contract ) )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.UnknownDefinition,
					$"item/{item.Key}/traits",
					$"Item definition '{item.Value.Definition}' has no persistence contract." ) );
				continue;
			}
			foreach ( var expected in contract.Traits )
			{
				if ( !item.Value.Traits.TryGetValue( expected.Key, out var payload ) )
				{
					issues.Add( new DomainInvariantIssue(
						ErrorCode.PersistedTypeInvalid,
						$"item/{item.Key}/traits/{expected.Key}",
						"Required typed item trait is missing." ) );
					continue;
				}
				ValidatePayload( payload, expected.Value, $"item/{item.Key}/traits/{expected.Key}", issues );
			}
			foreach ( var actual in item.Value.Traits.Keys.Where( key => !contract.Traits.ContainsKey( key ) ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					$"item/{item.Key}/traits/{actual}",
					"Item trait is not declared for this definition." ) );
		}

		foreach ( var reference in references )
		{
			if ( !_persistenceProfile.CharacterReferences.TryGetValue( reference.Value.Category, out var contract ) )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					$"character-reference/{reference.Key}/state",
					$"Character-reference category '{reference.Value.Category}' is not declared." ) );
				continue;
			}
			ValidatePayload( reference.Value.State, contract.StateType, $"character-reference/{reference.Key}/state", issues );
		}

		foreach ( var entity in sceneEntities )
		{
			if ( !_persistenceProfile.SceneEntities.TryGetValue( entity.Value.Kind, out var expected ) )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					$"scene-entity/{entity.Key}/state",
					$"Scene-entity kind '{entity.Value.Kind}' is not declared." ) );
				continue;
			}
			ValidatePayload( entity.Value.State, expected, $"scene-entity/{entity.Key}/state", issues );
		}
	}

	private void ValidateReferenceTargets(
		IReadOnlyCollection<CharacterRecord> characters,
		IReadOnlyCollection<PersistentSceneEntityRecord> sceneEntities,
		IEnumerable<DocumentSnapshot<CharacterReferenceRecord>> referenceDocuments,
		ICollection<DomainInvariantIssue> issues )
	{
		foreach ( var document in referenceDocuments )
		{
			var reference = document.Value;
			if ( !_persistenceProfile.CharacterReferences.TryGetValue( reference.Category, out var contract ) )
				continue;
			if ( !contract.AllowMissingCharacter &&
				characters.Count( character => character.Id == reference.CharacterId ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-reference/{document.Key}/character",
					"Character reference points to a missing character." ) );
			if ( reference.RelatedCharacterId is CharacterId related &&
				!contract.AllowMissingRelatedCharacter &&
				characters.Count( character => character.Id == related ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-reference/{document.Key}/related-character",
					"Character reference points to a missing related character." ) );
			if ( reference.SceneEntityId is SceneEntityId sceneEntityId &&
				!contract.AllowMissingSceneEntity &&
				sceneEntities.Count( entity => entity.Id == sceneEntityId ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-reference/{document.Key}/scene-entity",
					"Character reference points to a missing scene entity." ) );
		}
	}

	private void ValidatePayload(
		TypedPayload payload,
		PersistedTypeId expectedType,
		string path,
		ICollection<DomainInvariantIssue> issues )
	{
		if ( payload.TypeId != expectedType )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid,
				path,
				$"Expected persisted type '{expectedType}', found '{payload.TypeId}'." ) );
			return;
		}

		if ( !_schema.PersistedTypes.TryGet( payload.TypeId.Value, out var registration ) )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid, path, "Nested payload type is not registered by the schema." ) );
			return;
		}

		IPersistedTypeCodec codec;
		try
		{
			codec = _repositories.Provider.Types.Resolve( new PersistedTypeKey( payload.TypeId.Value ) );
		}
		catch ( Exception exception ) when ( exception is KeyNotFoundException or ArgumentException )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid, path, "Nested payload type has no registered codec." ) );
			return;
		}

		if ( payload.TypeVersion != registration!.Version ||
			codec.CurrentVersion != registration.Version ||
			codec.ClrType != registration.ClrType )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid,
				path,
				$"Persisted type '{payload.TypeId}' version or concrete codec is incompatible." ) );
			return;
		}

		try
		{
			var decoded = codec.Deserialize( payload.Data, payload.TypeVersion );
			_ = codec.Serialize( codec.PrepareForPublication( decoded ) );
		}
		catch ( Exception exception ) when (
			exception is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException or ArgumentException )
		{
			issues.Add( new DomainInvariantIssue(
				ErrorCode.PersistedTypeInvalid,
				path,
				$"Persisted payload could not be decoded by '{payload.TypeId}'." ) );
		}
	}

	private static string ExpectedInventoryRole( InventoryOwnerKind kind ) => kind switch
	{
		InventoryOwnerKind.Character => "main",
		InventoryOwnerKind.ParentItem => "bag",
		InventoryOwnerKind.SceneEntity => "storage",
		_ => throw new ArgumentOutOfRangeException( nameof(kind), kind, null )
	};

	private void ValidateInventories(
		IEnumerable<InventoryRecord> inventories,
		IReadOnlyDictionary<ItemId, ItemRecord> items,
		ICollection<DomainInvariantIssue> issues )
	{
		foreach ( var inventory in inventories )
		{
			if ( inventory.Width <= 0 || inventory.Height <= 0 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument, $"inventory/{inventory.Id}", "Inventory dimensions must be positive." ) );
			var occupied = new HashSet<(int X, int Y)>();
			foreach ( var placement in inventory.Placements )
			{
				if ( !items.TryGetValue( placement.ItemId, out var item ) )
				{
					// Fail closed on the one coordinate we can prove even when the
					// missing item's registered dimensions are unavailable.
					if ( !occupied.Add( (placement.X, placement.Y) ) )
						issues.Add( new DomainInvariantIssue(
							ErrorCode.Conflict, $"inventory/{inventory.Id}", "Item placements overlap." ) );
					issues.Add( new DomainInvariantIssue(
						ErrorCode.NotFound, $"inventory/{inventory.Id}/item/{placement.ItemId}", "Placement references a missing item." ) );
					continue;
				}
				if ( !_schema.Items.Contains( item.Definition.Value ) || !_shapes.TryGetShape( item.Definition, out var shape ) )
				{
					issues.Add( new DomainInvariantIssue(
						ErrorCode.UnknownDefinition, $"item/{item.Id}", "Item definition or shape is not registered." ) );
					continue;
				}
				if ( placement.X + shape.Width > inventory.Width || placement.Y + shape.Height > inventory.Height )
					issues.Add( new DomainInvariantIssue(
						ErrorCode.Conflict, $"inventory/{inventory.Id}/item/{item.Id}", "Item exceeds inventory bounds." ) );
				for ( var y = placement.Y; y < placement.Y + shape.Height; y++ )
				for ( var x = placement.X; x < placement.X + shape.Width; x++ )
					if ( !occupied.Add( (x, y) ) )
						issues.Add( new DomainInvariantIssue(
							ErrorCode.Conflict, $"inventory/{inventory.Id}", "Item placements overlap." ) );
			}
		}
	}

	private static void ValidateLocations(
		IEnumerable<InventoryRecord> inventories,
		IReadOnlyDictionary<ItemId, ItemRecord> items,
		IEnumerable<WorldItemRecord> worldItems,
		ICollection<DomainInvariantIssue> issues )
	{
		var counts = items.Keys.ToDictionary( id => id, _ => 0 );
		foreach ( var placement in inventories.SelectMany( inventory => inventory.Placements ) )
		{
			if ( counts.ContainsKey( placement.ItemId ) ) counts[placement.ItemId]++;
		}
		foreach ( var world in worldItems )
		{
			if ( counts.ContainsKey( world.ItemId ) ) counts[world.ItemId]++;
			else issues.Add( new DomainInvariantIssue(
				ErrorCode.NotFound, $"world-item/{world.ItemId}", "World record references a missing item." ) );
		}
		foreach ( var location in counts.Where( pair => pair.Value != 1 ) )
			issues.Add( new DomainInvariantIssue(
				ErrorCode.Conflict, $"item/{location.Key}/location", $"Expected exactly one location, found {location.Value}." ) );
	}

	private static void ValidateBagGraph(
		IReadOnlyList<InventoryRecord> inventories,
		ICollection<DomainInvariantIssue> issues )
	{
		var containingInventory = inventories
			.SelectMany( inventory => inventory.Placements.Select( placement => (placement.ItemId, inventory.Id) ) )
			.GroupBy( pair => pair.ItemId )
			.ToDictionary( group => group.Key, group => group.First().Id );
		var byId = inventories
			.GroupBy( inventory => inventory.Id )
			.ToDictionary( group => group.Key, group => group.First() );
		foreach ( var inventory in inventories.Where( value => value.Owner.Kind == InventoryOwnerKind.ParentItem ) )
		{
			var seen = new HashSet<InventoryId> { inventory.Id };
			var parentItem = new ItemId( inventory.Owner.OwnerId );
			while ( containingInventory.TryGetValue( parentItem, out var parentInventoryId ) &&
				byId.TryGetValue( parentInventoryId, out var parentInventory ) )
			{
				if ( !seen.Add( parentInventoryId ) )
				{
					issues.Add( new DomainInvariantIssue(
						ErrorCode.Conflict, $"inventory/{inventory.Id}/owner", "Nested bag ownership contains a cycle." ) );
					break;
				}
				if ( parentInventory.Owner.Kind != InventoryOwnerKind.ParentItem ) break;
				parentItem = new ItemId( parentInventory.Owner.OwnerId );
			}
		}
	}
}
