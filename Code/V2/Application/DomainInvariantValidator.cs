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
public sealed class DomainInvariantValidator : IIncrementalPersistenceInvariantSet
{
	private readonly DomainRepositories? _repositories;
	private readonly PersistedTypeRegistry _types;
	private readonly CompiledSchema _schema;
	private readonly IItemShapeCatalog _shapes;
	private readonly SchemaPersistenceInvariantProfile _persistenceProfile;
	private readonly DomainInvariantDependencyIndex _dependencyIndex = new();

	public DomainInvariantValidator(
		DomainRepositories repositories,
		CompiledSchema schema,
		IItemShapeCatalog shapes,
		SchemaPersistenceInvariantProfile persistenceProfile )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_types = repositories.Provider.Types;
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_shapes = shapes ?? throw new ArgumentNullException( nameof(shapes) );
		_persistenceProfile = persistenceProfile ?? throw new ArgumentNullException( nameof(persistenceProfile) );
	}

	public DomainInvariantValidator(
		PersistedTypeRegistry types,
		CompiledSchema schema,
		IItemShapeCatalog shapes,
		SchemaPersistenceInvariantProfile persistenceProfile )
	{
		_types = types ?? throw new ArgumentNullException( nameof(types) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_shapes = shapes ?? throw new ArgumentNullException( nameof(shapes) );
		_persistenceProfile = persistenceProfile ?? throw new ArgumentNullException( nameof(persistenceProfile) );
	}

	public DomainInvariantReport Validate()
	{
		if ( _repositories is null )
			throw new InvalidOperationException( "Repository-backed validation requires the DomainRepositories constructor." );
		return ValidateDocuments(
			_repositories.Characters.All(),
			_repositories.CharacterSlots.All(),
			_repositories.CharacterLifecycleGuards.All(),
			_repositories.Inventories.All(),
			_repositories.OwnerInventories.All(),
			_repositories.Items.All(),
			_repositories.WorldItems.All(),
			_repositories.UniqueReservations.All(),
			_repositories.CharacterReferences.All(),
			_repositories.SceneEntities.All() );
	}

	public IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context )
	{
		ArgumentNullException.ThrowIfNull( context );
		var issues = new List<DomainInvariantIssue>();
		var characters = CandidateDocuments<CharacterRecord>( context, DomainCollections.Characters, issues );
		var slots = CandidateDocuments<CharacterSlotRecord>( context, DomainCollections.CharacterSlots, issues );
		var guards = CandidateDocuments<CharacterLifecycleGuardRecord>(
			context, DomainCollections.CharacterLifecycleGuards, issues );
		var inventories = CandidateDocuments<InventoryRecord>( context, DomainCollections.Inventories, issues );
		var ownerInventories = CandidateDocuments<OwnerInventoryRecord>( context, DomainCollections.OwnerInventories, issues );
		var items = CandidateDocuments<ItemRecord>( context, DomainCollections.Items, issues );
		var worldItems = CandidateDocuments<WorldItemRecord>( context, DomainCollections.WorldItems, issues );
		var reservations = CandidateDocuments<UniqueReservationRecord>( context, DomainCollections.UniqueReservations, issues );
		var references = CandidateDocuments<CharacterReferenceRecord>( context, DomainCollections.CharacterReferences, issues );
		var sceneEntities = CandidateDocuments<PersistentSceneEntityRecord>( context, DomainCollections.SceneEntities, issues );
		ValidateReferenceGuardMutation( context, issues );
		Func<string, string, bool>? shouldValidatePayload = context.IsRecovery
			? null
			: (collection, key) => context.ChangedAddresses.Contains( new DocumentAddress( collection, key ) );
		issues.AddRange( ValidateDocuments(
			characters, slots, guards, inventories, ownerInventories, items, worldItems,
			reservations, references, sceneEntities, shouldValidatePayload ).Issues );
		return issues.Select( issue => new PersistenceInvariantIssue(
			issue.Code.ToString(), issue.Path, issue.Message ) ).ToArray();
	}

	public PersistenceInvariantPreparation Prepare( PersistenceInvariantContext context )
	{
		ArgumentNullException.ThrowIfNull( context );
		if ( context.IsRecovery )
			throw new InvalidOperationException( "Recovery must use full invariant validation and Rebuild." );
		if ( !_dependencyIndex.IsReady )
			throw new InvalidOperationException( "Domain invariant dependency index has not been rebuilt." );

		var mutations = new List<DomainDependencyMutation>( context.ChangedAddresses.Count );
		foreach ( var address in context.ChangedAddresses )
		{
			_dependencyIndex.TryGet( address, out var previous );
			var current = context.TryGet( address, out var candidate ) && candidate is not null
				? Describe( candidate )
				: null;
			mutations.Add( new DomainDependencyMutation( address, previous, current ) );
		}

		var closure = _dependencyIndex.DependencyClosure( mutations );
		var candidateDocuments = new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		var previousDocuments = new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		foreach ( var address in closure )
		{
			if ( context.TryGet( address, out var candidate ) && candidate is not null )
				candidateDocuments.Add( address, candidate );
			if ( _dependencyIndex.TryGet( address, out var previous ) && previous is not null )
				previousDocuments.Add( address, previous.Document );
		}
		context.RecordVisitedDocuments( closure.Count );

		var subset = new PersistenceInvariantContext(
			context.Sequence,
			context.CompactionGeneration,
			candidateDocuments,
			previousDocuments,
			context.ChangedAddresses,
			isRecovery: false );
		var issues = Validate( subset );
		return new PersistenceInvariantPreparation(
			issues,
			new DomainInvariantPublication( mutations.ToArray(), closure.Count ) );
	}

	public void Publish( PersistenceInvariantPreparation preparation )
	{
		ArgumentNullException.ThrowIfNull( preparation );
		if ( preparation.PublicationState is not DomainInvariantPublication publication )
			throw new InvalidOperationException( "Domain invariant publication state is missing or invalid." );
		_dependencyIndex.Apply( publication.Mutations );
	}

	public void Rebuild( PersistenceInvariantContext context )
	{
		ArgumentNullException.ThrowIfNull( context );
		_dependencyIndex.Rebuild( context.Documents.Select( Describe ) );
	}

	internal int IndexedDocumentCount => _dependencyIndex.DocumentCount;

	private DomainInvariantReport ValidateDocuments(
		IReadOnlyList<DocumentSnapshot<CharacterRecord>> characterDocuments,
		IReadOnlyList<DocumentSnapshot<CharacterSlotRecord>> slotDocuments,
		IReadOnlyList<DocumentSnapshot<CharacterLifecycleGuardRecord>> guardDocuments,
		IReadOnlyList<DocumentSnapshot<InventoryRecord>> inventoryDocuments,
		IReadOnlyList<DocumentSnapshot<OwnerInventoryRecord>> ownerInventoryDocuments,
		IReadOnlyList<DocumentSnapshot<ItemRecord>> itemDocuments,
		IReadOnlyList<DocumentSnapshot<WorldItemRecord>> worldItemDocuments,
		IReadOnlyList<DocumentSnapshot<UniqueReservationRecord>> reservationDocuments,
		IReadOnlyList<DocumentSnapshot<CharacterReferenceRecord>> referenceDocuments,
		IReadOnlyList<DocumentSnapshot<PersistentSceneEntityRecord>> sceneEntityDocuments,
		Func<string, string, bool>? shouldValidatePayload = null )
	{
		var issues = new List<DomainInvariantIssue>();
		ValidateProfile( issues );
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
		ValidateLifecycleGuards( characters, guardDocuments, issues );
		ValidateReservations( characters, reservationDocuments, issues );
		ValidateOwnerInventories(
			characters, inventories, items, sceneEntities, ownerInventoryDocuments, issues );
		ValidateNestedPayloads(
			characterDocuments, itemDocuments, referenceDocuments, sceneEntityDocuments,
			shouldValidatePayload, issues );
		ValidateReferenceTargets( characters, sceneEntities, referenceDocuments, issues );
		ValidateInventories( inventories, items, issues );
		ValidateLocations( inventories, items, worldItemDocuments.Select( value => value.Value ), issues );
		ValidateBagGraph( inventories, issues );
		return new DomainInvariantReport( issues );
	}

	private static void ValidateLifecycleGuards(
		IReadOnlyCollection<CharacterRecord> characters,
		IEnumerable<DocumentSnapshot<CharacterLifecycleGuardRecord>> guardDocuments,
		ICollection<DomainInvariantIssue> issues )
	{
		var guards = guardDocuments.ToArray();
		var characterCounts = characters
			.GroupBy( character => character.Id )
			.ToDictionary( group => group.Key, group => group.Count() );
		var guardCounts = guards
			.GroupBy( document => document.Value.CharacterId )
			.ToDictionary( group => group.Key, group => group.Count() );
		foreach ( var document in guards )
		{
			var guard = document.Value;
			ValidateKey( document.Key, DomainKeys.CharacterLifecycleGuard( guard.CharacterId ),
				"character-lifecycle-guard", issues );
			if ( guard.ReferenceRevision < 0 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict, $"character-lifecycle-guard/{document.Key}", "Reference revision cannot be negative." ) );
			if ( characterCounts.GetValueOrDefault( guard.CharacterId ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound, $"character-lifecycle-guard/{document.Key}", "Lifecycle guard has no character." ) );
		}
		foreach ( var character in characters )
			if ( guardCounts.GetValueOrDefault( character.Id ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict, $"character/{character.Id}/lifecycle-guard", "Character requires exactly one lifecycle guard." ) );
	}

	private static void ValidateReferenceGuardMutation(
		PersistenceInvariantContext context,
		ICollection<DomainInvariantIssue> issues )
	{
		if ( context.IsRecovery ) return;
		var targets = new HashSet<CharacterId>();
		var candidateReferenceTargets = new HashSet<CharacterId>();
		foreach ( var candidate in context.Collection( DomainCollections.CharacterReferences ) )
			if ( candidate.Value is CharacterReferenceRecord reference )
				AddTargets( reference, candidateReferenceTargets );
		foreach ( var address in context.ChangedAddresses.Where(
			address => address.Collection == DomainCollections.CharacterReferences ) )
		{
			if ( context.PreviousDocuments.TryGetValue( address, out var previous ) &&
				previous.Value is CharacterReferenceRecord oldReference ) AddTargets( oldReference, targets );
			if ( context.TryGet( address, out var candidate ) &&
				candidate?.Value is CharacterReferenceRecord newReference ) AddTargets( newReference, targets );
		}
		foreach ( var target in targets )
		{
			var characterAddress = new DocumentAddress(
				DomainCollections.Characters, DomainKeys.Character( target ) );
			var existed = context.PreviousDocuments.ContainsKey( characterAddress );
			var exists = context.TryGet( characterAddress, out _ );
			if ( !existed && !exists ) continue;
			var guardAddress = new DocumentAddress(
				DomainCollections.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard( target ) );
			if ( existed && !exists && context.ChangedAddresses.Contains( guardAddress ) &&
				!context.TryGet( guardAddress, out _) )
			{
				var dangling = candidateReferenceTargets.Contains( target );
				if ( dangling )
					issues.Add( new DomainInvariantIssue(
						ErrorCode.Conflict, $"character/{target}/references",
						"Character deletion left a matching character reference." ) );
				continue;
			}
			if ( !context.ChangedAddresses.Contains( guardAddress ) ||
				!context.PreviousDocuments.TryGetValue( guardAddress, out var previous ) ||
				previous.Value is not CharacterLifecycleGuardRecord oldGuard ||
				!context.TryGet( guardAddress, out var current ) ||
				current?.Value is not CharacterLifecycleGuardRecord newGuard ||
				newGuard.ReferenceRevision != checked(oldGuard.ReferenceRevision + 1) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character/{target}/lifecycle-guard",
					"Character-reference mutation did not advance its target lifecycle guard exactly once." ) );
		}

		static void AddTargets( CharacterReferenceRecord reference, ISet<CharacterId> destination )
		{
			destination.Add( reference.CharacterId );
			if ( reference.RelatedCharacterId is { } related ) destination.Add( related );
		}
	}

	private IReadOnlyList<DocumentSnapshot<T>> CandidateDocuments<T>(
		PersistenceInvariantContext context,
		string collection,
		ICollection<DomainInvariantIssue> issues ) where T : class
	{
		var documents = new List<DocumentSnapshot<T>>();
		var expected = _types.Resolve<T>();
		foreach ( var candidate in context.Collection( collection ) )
		{
			if ( candidate.PersistedType != expected.Key || candidate.TypeVersion != expected.CurrentVersion ||
				candidate.Value is not T value )
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.PersistedTypeInvalid,
					candidate.Address.ToString(),
					$"Outer document type/version is not the exact current '{expected.Key}' v{expected.CurrentVersion}." ) );
				continue;
			}
			documents.Add( new DocumentSnapshot<T>( candidate.Address.Key, candidate.Revision, value ) );
		}
		return documents;
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
			var codec = _types.Resolve( new PersistedTypeKey( expectedType.Value ) );
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
		var charactersById = characters
			.GroupBy( character => character.Id )
			.ToDictionary( group => group.Key, group => group.ToArray() );
		var slotMatchCounts = slots
			.GroupBy( document => (
				document.Value.AccountId,
				document.Value.Slot,
				document.Value.CharacterId ) )
			.ToDictionary( group => group.Key, group => group.Count() );
		var logicalSlots = new HashSet<(AccountId AccountId, int Slot)>();
		foreach ( var document in slots )
		{
			var slot = document.Value;
			if ( slot.Slot is < 0 or >= CharacterRules.MaximumSlots )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument,
					$"character-slot/{document.Key}",
					$"Owner-slot guard slot {slot.Slot} is outside 0..{CharacterRules.MaximumSlots - 1}." ) );
			if ( !logicalSlots.Add( (slot.AccountId, slot.Slot) ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict,
					$"character-slot/{document.Key}",
					"Logical owner-slot guard is duplicated." ) );
			var referenced = charactersById.GetValueOrDefault( slot.CharacterId ) ?? Array.Empty<CharacterRecord>();
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
			if ( character.Slot is < 0 or >= CharacterRules.MaximumSlots )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument,
					$"character/{character.Id}",
					$"Character slot {character.Slot} is outside 0..{CharacterRules.MaximumSlots - 1}." ) );
			var matching = slotMatchCounts.GetValueOrDefault(
				(character.AccountId, character.Slot, character.Id) );
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
		var characterCounts = characters
			.GroupBy( character => character.Id )
			.ToDictionary( group => group.Key, group => group.Count() );
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
			if ( characterCounts.GetValueOrDefault( reservation.CharacterId ) != 1 )
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
		var characterIds = characters.Select( character => character.Id.Value ).ToHashSet();
		var sceneEntityIds = sceneEntities.Select( entity => entity.Id.Value ).ToHashSet();
		var characterInventoryCounts = inventories
			.Where( inventory => inventory.Owner.Kind == InventoryOwnerKind.Character )
			.GroupBy( inventory => inventory.Owner.OwnerId )
			.ToDictionary( group => group.Key, group => group.Count() );
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
				InventoryOwnerKind.Character => characterIds.Contains( inventory.Owner.OwnerId ),
				InventoryOwnerKind.ParentItem => items.ContainsKey( new ItemId( inventory.Owner.OwnerId ) ),
				InventoryOwnerKind.SceneEntity => sceneEntityIds.Contains( inventory.Owner.OwnerId ),
				_ => false
			};
			if ( !ownerExists )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound, $"inventory/{inventory.Id}/owner", "Inventory owner does not exist." ) );
		}

		foreach ( var character in characters )
		{
			var mainInventories = characterInventoryCounts.GetValueOrDefault( character.Id.Value );
			var mainIndexes = logicalIndexes.Contains(
				DomainKeys.OwnerInventory( InventoryOwner.Character( character.Id ), InventoryRoles.Main ) ) ? 1 : 0;
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
		Func<string, string, bool>? shouldValidatePayload,
		ICollection<DomainInvariantIssue> issues )
	{
		foreach ( var character in characters )
		{
			if ( shouldValidatePayload is not null &&
				!shouldValidatePayload( DomainCollections.Characters, character.Key ) ) continue;
			ValidateIdentityText(
				character.Value.Name,
				CharacterRules.NormalizeName,
				$"character/{character.Key}/name",
				issues );
			ValidateIdentityText(
				character.Value.Description,
				CharacterRules.NormalizeDescription,
				$"character/{character.Key}/description",
				issues );
			ValidatePayload(
				character.Value.SchemaState,
				_persistenceProfile.CharacterState,
				$"character/{character.Key}/schema-state",
				issues );
		}

		foreach ( var item in items )
		{
			if ( shouldValidatePayload is not null &&
				!shouldValidatePayload( DomainCollections.Items, item.Key ) ) continue;
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
			if ( shouldValidatePayload is not null &&
				!shouldValidatePayload( DomainCollections.CharacterReferences, reference.Key ) ) continue;
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
			if ( shouldValidatePayload is not null &&
				!shouldValidatePayload( DomainCollections.SceneEntities, entity.Key ) ) continue;
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

	/// <summary>
	/// A persisted identity string must already BE the canonical form its rules produce, not
	/// merely pass them. Asserting equality rather than validity is what closes the path where a
	/// writer validates one string and stores another, and it holds for every writer rather than
	/// just character creation. Messages never quote the offending value, so a name cannot forge
	/// a log line on its way out through the startup report.
	/// </summary>
	private static void ValidateIdentityText(
		string value,
		Func<string, OperationResult<string>> normalize,
		string path,
		ICollection<DomainInvariantIssue> issues )
	{
		var canonical = normalize( value );
		if ( canonical.Failed )
		{
			issues.Add( new DomainInvariantIssue( canonical.Error!.Code, path, canonical.Error.Message ) );
			return;
		}

		if ( !string.Equals( canonical.Value, value, StringComparison.Ordinal ) )
			issues.Add( new DomainInvariantIssue(
				ErrorCode.InvalidArgument,
				path,
				"Stored text is not the canonical form its own rules produce." ) );
	}

	private void ValidateReferenceTargets(
		IReadOnlyCollection<CharacterRecord> characters,
		IReadOnlyCollection<PersistentSceneEntityRecord> sceneEntities,
		IEnumerable<DocumentSnapshot<CharacterReferenceRecord>> referenceDocuments,
		ICollection<DomainInvariantIssue> issues )
	{
		var characterCounts = characters
			.GroupBy( character => character.Id )
			.ToDictionary( group => group.Key, group => group.Count() );
		var sceneEntityCounts = sceneEntities
			.GroupBy( entity => entity.Id )
			.ToDictionary( group => group.Key, group => group.Count() );
		foreach ( var document in referenceDocuments )
		{
			var reference = document.Value;
			if ( !_persistenceProfile.CharacterReferences.TryGetValue( reference.Category, out var contract ) )
				continue;
			if ( !contract.AllowMissingCharacter &&
				characterCounts.GetValueOrDefault( reference.CharacterId ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-reference/{document.Key}/character",
					"Character reference points to a missing character." ) );
			if ( reference.RelatedCharacterId is CharacterId related &&
				!contract.AllowMissingRelatedCharacter &&
				characterCounts.GetValueOrDefault( related ) != 1 )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.NotFound,
					$"character-reference/{document.Key}/related-character",
					"Character reference points to a missing related character." ) );
			if ( reference.SceneEntityId is SceneEntityId sceneEntityId &&
				!contract.AllowMissingSceneEntity &&
				sceneEntityCounts.GetValueOrDefault( sceneEntityId ) != 1 )
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
			codec = _types.Resolve( new PersistedTypeKey( payload.TypeId.Value ) );
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
			_ = PersistedCodecCanonicalization.FromPayload(
				codec, payload.Data, payload.TypeVersion, requireCanonicalPayload: true );
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
		InventoryOwnerKind.Character => InventoryRoles.Main,
		InventoryOwnerKind.ParentItem => InventoryRoles.Bag,
		InventoryOwnerKind.SceneEntity => InventoryRoles.Storage,
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
			{
				issues.Add( new DomainInvariantIssue(
					ErrorCode.InvalidArgument, $"inventory/{inventory.Id}", "Inventory dimensions must be positive." ) );
				continue;
			}
			var bounds = new InventoryGridSize( inventory.Width, inventory.Height );
			var rectangles = new List<InventoryRectangle>( inventory.Placements.Count );
			foreach ( var placement in inventory.Placements )
			{
				if ( !items.TryGetValue( placement.ItemId, out var item ) )
				{
					// Fail closed on the one coordinate we can prove even when the
					// missing item's registered dimensions are unavailable.
					var unknownRectangle = new InventoryRectangle(
						placement.Position, new InventoryGridSize( 1, 1 ) );
					rectangles.Add( unknownRectangle );
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
				var rectangle = new InventoryRectangle( placement.Position, shape.GridSize );
				if ( !rectangle.FitsWithin( bounds ) )
					issues.Add( new DomainInvariantIssue(
						ErrorCode.Conflict, $"inventory/{inventory.Id}/item/{item.Id}", "Item exceeds inventory bounds." ) );
				rectangles.Add( rectangle );
			}
			if ( HasRectangleOverlap( rectangles ) )
				issues.Add( new DomainInvariantIssue(
					ErrorCode.Conflict, $"inventory/{inventory.Id}", "Item placements overlap." ) );
		}
	}

	private static bool HasRectangleOverlap( IReadOnlyList<InventoryRectangle> rectangles )
	{
		if ( rectangles.Count < 2 ) return false;
		var coordinates = rectangles
			.SelectMany( rectangle => new[] { rectangle.Top, rectangle.Bottom } )
			.Distinct()
			.OrderBy( coordinate => coordinate )
			.ToArray();
		var coordinateIndexes = coordinates
			.Select( (coordinate, index) => (coordinate, index) )
			.ToDictionary( pair => pair.coordinate, pair => pair.index );
		var events = rectangles
			.SelectMany( rectangle => new[]
			{
				new RectangleSweepEvent( rectangle.Left, true, rectangle ),
				new RectangleSweepEvent( rectangle.Right, false, rectangle )
			} )
			.OrderBy( item => item.X )
			// Half-open rectangles that merely touch at an edge do not overlap.
			.ThenBy( item => item.IsStart ? 1 : 0 )
			.ToArray();
		var active = new RangeMaximumTree( coordinates.Length - 1 );
		foreach ( var item in events )
		{
			var start = coordinateIndexes[item.Rectangle.Top];
			var end = coordinateIndexes[item.Rectangle.Bottom] - 1;
			if ( item.IsStart )
			{
				if ( active.Query( start, end ) > 0 ) return true;
				active.Add( start, end, 1 );
			}
			else
			{
				active.Add( start, end, -1 );
			}
		}
		return false;
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
		var parentByInventory = new Dictionary<InventoryId, InventoryId>();
		foreach ( var inventory in inventories.Where( value => value.Owner.Kind == InventoryOwnerKind.ParentItem ) )
		{
			var parentItem = new ItemId( inventory.Owner.OwnerId );
			if ( containingInventory.TryGetValue( parentItem, out var parentInventoryId ) )
				parentByInventory[inventory.Id] = parentInventoryId;
		}

		var states = new Dictionary<InventoryId, byte>();
		foreach ( var start in parentByInventory.Keys )
		{
			if ( states.GetValueOrDefault( start ) == 2 ) continue;
			var path = new List<InventoryId>();
			var positions = new Dictionary<InventoryId, int>();
			var current = start;
			while ( true )
			{
				var state = states.GetValueOrDefault( current );
				if ( state == 2 || !parentByInventory.TryGetValue( current, out var parent ) ) break;
				if ( state == 1 )
				{
					if ( positions.ContainsKey( current ) )
						issues.Add( new DomainInvariantIssue(
							ErrorCode.Conflict,
							$"inventory/{current}/owner",
							"Nested bag ownership contains a cycle." ) );
					break;
				}
				states[current] = 1;
				positions[current] = path.Count;
				path.Add( current );
				current = parent;
			}
			foreach ( var inventoryId in path ) states[inventoryId] = 2;
		}
	}

	private readonly record struct RectangleSweepEvent(
		long X,
		bool IsStart,
		InventoryRectangle Rectangle );

	private sealed class RangeMaximumTree
	{
		private readonly int _count;
		private readonly int[] _maximum;
		private readonly int[] _lazy;

		public RangeMaximumTree( int count )
		{
			_count = count;
			_maximum = new int[Math.Max( 1, count * 4 )];
			_lazy = new int[_maximum.Length];
		}

		public void Add( int start, int end, int value )
		{
			if ( start > end || _count == 0 ) return;
			Add( 1, 0, _count - 1, start, end, value );
		}

		public int Query( int start, int end ) =>
			start > end || _count == 0 ? 0 : Query( 1, 0, _count - 1, start, end );

		private void Add( int node, int left, int right, int start, int end, int value )
		{
			if ( start <= left && right <= end )
			{
				_maximum[node] += value;
				_lazy[node] += value;
				return;
			}
			var middle = left + ((right - left) / 2);
			if ( start <= middle ) Add( node * 2, left, middle, start, end, value );
			if ( end > middle ) Add( node * 2 + 1, middle + 1, right, start, end, value );
			_maximum[node] = _lazy[node] + Math.Max( _maximum[node * 2], _maximum[node * 2 + 1] );
		}

		private int Query( int node, int left, int right, int start, int end )
		{
			if ( start <= left && right <= end ) return _maximum[node];
			var middle = left + ((right - left) / 2);
			var maximum = 0;
			if ( start <= middle ) maximum = Query( node * 2, left, middle, start, end );
			if ( end > middle ) maximum = Math.Max(
				maximum,
				Query( node * 2 + 1, middle + 1, right, start, end ) );
			return _lazy[node] + maximum;
		}
	}

	private static DomainDependencyDocument Describe( PersistenceCandidateDocument document )
	{
		var tokens = new HashSet<string>( StringComparer.Ordinal )
		{
			$"address:{document.Address.Collection}/{document.Address.Key}"
		};
		switch ( document.Value )
		{
			case CharacterRecord character:
				AddCharacter( tokens, character.Id );
				tokens.Add( SlotToken( character.AccountId, character.Slot ) );
				break;
			case CharacterSlotRecord slot:
				AddCharacter( tokens, slot.CharacterId );
				tokens.Add( SlotToken( slot.AccountId, slot.Slot ) );
				break;
			case CharacterLifecycleGuardRecord guard:
				AddCharacter( tokens, guard.CharacterId );
				break;
			case InventoryRecord inventory:
				tokens.Add( InventoryToken( inventory.Id ) );
				AddOwner( tokens, inventory.Owner );
				foreach ( var placement in inventory.Placements ) tokens.Add( ItemToken( placement.ItemId ) );
				break;
			case OwnerInventoryRecord ownerInventory:
				tokens.Add( InventoryToken( ownerInventory.InventoryId ) );
				AddOwner( tokens, ownerInventory.Owner );
				tokens.Add( OwnerRoleToken( ownerInventory.Owner, ownerInventory.Role ) );
				break;
			case ItemRecord item:
				tokens.Add( ItemToken( item.Id ) );
				break;
			case WorldItemRecord worldItem:
				tokens.Add( ItemToken( worldItem.ItemId ) );
				break;
			case UniqueReservationRecord reservation:
				AddCharacter( tokens, reservation.CharacterId );
				tokens.Add( $"reservation:{NormalizeTokenPart( reservation.Namespace )}:{NormalizeTokenPart( reservation.Value )}" );
				break;
			case CharacterReferenceRecord reference:
				AddCharacter( tokens, reference.CharacterId );
				if ( reference.RelatedCharacterId is CharacterId related ) AddCharacter( tokens, related );
				if ( reference.SceneEntityId is SceneEntityId scene ) tokens.Add( SceneToken( scene ) );
				break;
			case PersistentSceneEntityRecord sceneEntity:
				tokens.Add( SceneToken( sceneEntity.Id ) );
				break;
		}
		return new DomainDependencyDocument( document, tokens.ToArray() );
	}

	private static void AddCharacter( ISet<string> tokens, CharacterId characterId ) =>
		tokens.Add( CharacterToken( characterId ) );

	private static void AddOwner( ISet<string> tokens, InventoryOwner owner )
	{
		switch ( owner.Kind )
		{
			case InventoryOwnerKind.Character:
				tokens.Add( CharacterToken( new CharacterId( owner.OwnerId ) ) );
				break;
			case InventoryOwnerKind.ParentItem:
				tokens.Add( ItemToken( new ItemId( owner.OwnerId ) ) );
				break;
			case InventoryOwnerKind.SceneEntity:
				tokens.Add( SceneToken( new SceneEntityId( owner.OwnerId ) ) );
				break;
		}
	}

	private static string CharacterToken( CharacterId id ) => $"character:{id.Value:N}";
	private static string InventoryToken( InventoryId id ) => $"inventory:{id.Value:N}";
	private static string ItemToken( ItemId id ) => $"item:{id.Value:N}";
	private static string SceneToken( SceneEntityId id ) => $"scene:{id.Value:N}";
	private static string SlotToken( AccountId accountId, int slot ) => $"slot:{accountId.Value}:{slot}";
	private static string OwnerRoleToken( InventoryOwner owner, string? role ) =>
		$"owner-role:{(int)owner.Kind}:{owner.OwnerId:N}:{NormalizeTokenPart( role )}";
	private static string NormalizeTokenPart( string? value ) => value?.Trim().ToLowerInvariant() ?? "<null>";

	private sealed record DomainDependencyDocument(
		PersistenceCandidateDocument Document,
		IReadOnlyList<string> Tokens );

	private sealed record DomainDependencyMutation(
		DocumentAddress Address,
		DomainDependencyDocument? Previous,
		DomainDependencyDocument? Current );

	private sealed record DomainInvariantPublication(
		IReadOnlyList<DomainDependencyMutation> Mutations,
		int ClosureDocumentCount );

	private sealed class DomainInvariantDependencyIndex
	{
		private readonly Dictionary<DocumentAddress, DomainDependencyDocument> _documents = new();
		private readonly Dictionary<string, HashSet<DocumentAddress>> _byToken = new( StringComparer.Ordinal );

		public bool IsReady { get; private set; }
		public int DocumentCount => _documents.Count;

		public bool TryGet( DocumentAddress address, out DomainDependencyDocument? document ) =>
			_documents.TryGetValue( address, out document );

		public void Rebuild( IEnumerable<DomainDependencyDocument> documents )
		{
			_documents.Clear();
			_byToken.Clear();
			foreach ( var document in documents ) Add( document );
			IsReady = true;
		}

		public IReadOnlySet<DocumentAddress> DependencyClosure(
			IReadOnlyCollection<DomainDependencyMutation> mutations )
		{
			var changed = mutations.ToDictionary( mutation => mutation.Address );
			var transientByToken = new Dictionary<string, HashSet<DocumentAddress>>( StringComparer.Ordinal );
			foreach ( var mutation in mutations )
			{
				AddTransient( mutation.Previous );
				AddTransient( mutation.Current );
			}

			var addresses = mutations.Select( mutation => mutation.Address ).ToHashSet();
			var visitedTokens = new HashSet<string>( StringComparer.Ordinal );
			var pendingTokens = new Queue<string>();
			foreach ( var mutation in mutations )
			{
				Enqueue( mutation.Previous );
				Enqueue( mutation.Current );
			}

			while ( pendingTokens.Count > 0 )
			{
				var token = pendingTokens.Dequeue();
				VisitAddresses( _byToken.GetValueOrDefault( token ) );
				VisitAddresses( transientByToken.GetValueOrDefault( token ) );
			}
			return addresses;

			void AddTransient( DomainDependencyDocument? document )
			{
				if ( document is null ) return;
				foreach ( var token in document.Tokens )
				{
					if ( !transientByToken.TryGetValue( token, out var tokenAddresses ) )
					{
						tokenAddresses = new HashSet<DocumentAddress>();
						transientByToken.Add( token, tokenAddresses );
					}
					tokenAddresses.Add( document.Document.Address );
				}
			}

			void Enqueue( DomainDependencyDocument? document )
			{
				if ( document is null ) return;
				foreach ( var token in document.Tokens )
					if ( visitedTokens.Add( token ) ) pendingTokens.Enqueue( token );
			}

			void VisitAddresses( IEnumerable<DocumentAddress>? candidates )
			{
				if ( candidates is null ) return;
				foreach ( var address in candidates )
				{
					if ( !addresses.Add( address ) ) continue;
					if ( changed.TryGetValue( address, out var mutation ) )
					{
						Enqueue( mutation.Previous );
						Enqueue( mutation.Current );
					}
					else if ( _documents.TryGetValue( address, out var document ) )
					{
						Enqueue( document );
					}
				}
			}
		}

		public void Apply( IReadOnlyList<DomainDependencyMutation> mutations )
		{
			if ( !IsReady ) throw new InvalidOperationException( "Domain invariant dependency index is not ready." );
			foreach ( var mutation in mutations )
			{
				Remove( mutation.Address );
				if ( mutation.Current is not null ) Add( mutation.Current );
			}
		}

		private void Add( DomainDependencyDocument document )
		{
			_documents.Add( document.Document.Address, document );
			foreach ( var token in document.Tokens )
			{
				if ( !_byToken.TryGetValue( token, out var addresses ) )
				{
					addresses = new HashSet<DocumentAddress>();
					_byToken.Add( token, addresses );
				}
				addresses.Add( document.Document.Address );
			}
		}

		private void Remove( DocumentAddress address )
		{
			if ( !_documents.Remove( address, out var document ) ) return;
			foreach ( var token in document.Tokens )
			{
				if ( !_byToken.TryGetValue( token, out var addresses ) ) continue;
				addresses.Remove( address );
				if ( addresses.Count == 0 ) _byToken.Remove( token );
			}
		}
	}
}
