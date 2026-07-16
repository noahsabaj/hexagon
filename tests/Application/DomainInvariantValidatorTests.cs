#nullable enable

using System.Text.Json;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class DomainInvariantValidatorTests
{
	[TestMethod]
	public async Task ValidCharacterInventoryAndLocationGraphProducesCleanReport()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9001);
		var character = ApplicationServiceTestEnvironment.Character(account, 0);
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(character.Id),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			unitOfWork.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard(character.Id),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 });
			unitOfWork.Create(
				environment.Repositories.CharacterSlots,
				DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
		});
		var validator = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			environment.PersistenceProfile);

		var report = validator.Validate();

		Assert.IsTrue(report.IsValid);
		Assert.IsEmpty(report.Issues);
	}

	[TestMethod]
	public async Task EmptyStoreStillRejectsIncompleteOrUnregisteredPersistenceProfile()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var profile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId("unknown.character-state"),
			new[] { ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.ItemDefinitionId) },
			Array.Empty<KeyValuePair<string, PersistedTypeId>>(),
			Array.Empty<KeyValuePair<string, PersistedTypeId>>());
		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			profile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue =>
			issue.Path == "persistence-profile/character-state" &&
			issue.Code == ErrorCode.PersistedTypeInvalid));
		Assert.IsTrue(report.Issues.Any(issue =>
			issue.Path.EndsWith(ApplicationServiceTestEnvironment.BagDefinitionId, StringComparison.Ordinal) &&
			issue.Message.Contains("no persistence contract", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task DuplicateLogicalAggregateIdsUnderForgedOuterKeysAreReportedWithoutThrowing()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.SceneEntity(SceneEntityId.New()),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		await environment.SeedAsync(unit =>
		{
			unit.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unit.Create(environment.Repositories.Items, "forged-item-key", item);
			unit.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unit.Create(environment.Repositories.Inventories, "forged-inventory-key", inventory);
		});
		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			environment.PersistenceProfile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Logical item ID is duplicated", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Logical inventory ID is duplicated", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task CorruptCrossDocumentGraphReportsEachInvariantClass()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var character = ApplicationServiceTestEnvironment.Character(new AccountId(9002), 0);
		var outOfBounds = ApplicationServiceTestEnvironment.Item();
		var overlap = ApplicationServiceTestEnvironment.Item();
		var missingId = ItemId.New();
		var firstBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var secondBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var corruptInventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.SceneEntity(SceneEntityId.New()),
			new[]
			{
				new InventoryPlacement(outOfBounds.Id, 1, 0),
				new InventoryPlacement(overlap.Id, 0, 0),
				new InventoryPlacement(missingId, 0, 0)
			},
			width: 1,
			height: 1);
		var invalidDimensions = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.SceneEntity(SceneEntityId.New()),
			width: 0,
			height: 1);
		var firstBagInventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.ParentItem(firstBag.Id),
			new[] { new InventoryPlacement(secondBag.Id, 0, 0) });
		var secondBagInventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.ParentItem(secondBag.Id),
			new[] { new InventoryPlacement(firstBag.Id, 0, 0) });
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			foreach (var item in new[] { outOfBounds, overlap, firstBag, secondBag })
				unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			foreach (var inventory in new[]
				{ corruptInventory, invalidDimensions, firstBagInventory, secondBagInventory })
				unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unitOfWork.Create(
				environment.Repositories.WorldItems,
				DomainKeys.WorldItem(outOfBounds.Id),
				new WorldItemRecord
				{
					ItemId = outOfBounds.Id,
					Transform = ApplicationServiceTestEnvironment.Transform()
				});
		});
		var validator = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			environment.PersistenceProfile);

		var report = validator.Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("owner-slot", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("one main inventory", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("dimensions", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("missing item", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("exceeds inventory bounds", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("overlap", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("exactly one location", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("cycle", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Code == ErrorCode.NotFound));
		Assert.IsTrue(report.Issues.Any(issue => issue.Code == ErrorCode.InvalidArgument));
		Assert.IsTrue(report.Issues.Any(issue => issue.Code == ErrorCode.Conflict));
	}

	[TestMethod]
	public async Task NestedPayloadKindsVersionsCodecsAndOuterKeysAreValidated()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9003);
		var character = ApplicationServiceTestEnvironment.Character(account, 0) with
		{
			SchemaState = ApplicationServiceTestEnvironment.StatePayload(version: 2)
		};
		var item = ApplicationServiceTestEnvironment.Item() with
		{
			Traits = new Dictionary<string, TypedPayload>(StringComparer.Ordinal)
			{
				["state"] = new TypedPayload
				{
					TypeId = new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
					TypeVersion = 1,
					Data = System.Text.Json.JsonSerializer.SerializeToElement("malformed")
				}
			}
		};
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(character.Id),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var reference = new CharacterReferenceRecord
		{
			Category = "unknown_reference",
			CharacterId = character.Id,
			State = ApplicationServiceTestEnvironment.StatePayload()
		};
		var sceneEntity = new PersistentSceneEntityRecord
		{
			Id = SceneEntityId.New(),
			Kind = "storage",
			State = ApplicationServiceTestEnvironment.StatePayload(typeId: "unknown.state")
		};
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters, "wrong-character-key", character);
			unitOfWork.Create(
				environment.Repositories.CharacterSlots,
				DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
			unitOfWork.Create(environment.Repositories.CharacterReferences, "reference", reference);
			unitOfWork.Create(environment.Repositories.SceneEntities, DomainKeys.SceneEntity(sceneEntity.Id), sceneEntity);
		});
		var profile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
			new[]
			{
				ItemPersistenceContract.WithTrait(
					ApplicationServiceTestEnvironment.ItemDefinitionId,
					"state",
					ApplicationServiceTestEnvironment.StateTypeId),
				ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.BagDefinitionId)
			},
			new Dictionary<string, PersistedTypeId>(StringComparer.Ordinal)
			{
				["recognition"] = new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId)
			},
			new Dictionary<string, PersistedTypeId>(StringComparer.Ordinal)
			{
				["storage"] = new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId)
			});
		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			profile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("not canonical", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path.EndsWith("schema-state", StringComparison.Ordinal) &&
			issue.Message.Contains("incompatible", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path.Contains("traits/state", StringComparison.Ordinal) &&
			issue.Message.Contains("could not be decoded", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("not declared", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path.Contains("scene-entity", StringComparison.Ordinal) &&
			issue.Message.Contains("Expected persisted type", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task CanonicalOwnerIndexesRejectMissingExtraAndWrongMainOwnership()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9004);
		var character = ApplicationServiceTestEnvironment.Character(account, 0);
		var main = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(character.Id));
		var extra = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(character.Id));
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			unitOfWork.Create(
				environment.Repositories.CharacterSlots,
				DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(main.Id), main);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(extra.Id), extra);
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				"forged-index-key",
				new OwnerInventoryRecord { Owner = main.Owner, Role = "storage", InventoryId = main.Id });
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory(InventoryOwner.SceneEntity(SceneEntityId.New()), "storage"),
				new OwnerInventoryRecord
				{
					Owner = InventoryOwner.SceneEntity(SceneEntityId.New()),
					Role = "storage",
					InventoryId = InventoryId.New()
				});
		});
		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			environment.PersistenceProfile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("not canonical", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("requires role 'main'", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("missing inventory", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("canonical owner index, found 0", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Expected one main inventory and canonical index", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task RecoveredOuterEnvelopeWithMalformedNestedStateFailsNeutralValidation()
	{
		var storage = new InMemoryPersistenceStorage();
		var schemaResult = SchemaCompiler.Compile(new RecoverySchema());
		Assert.IsTrue(schemaResult.Succeeded, schemaResult.Error?.Message);
		var schema = schemaResult.Value;
		var characterId = CharacterId.New();
		var account = new AccountId(9005);
		var inventory = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(characterId));
		var registry = RecoveryRegistry();
		await using (var first = RecoveryProvider(storage, registry))
		{
			await first.InitializeAsync();
			var repositories = new DomainRepositories(first);
			var malformed = ApplicationServiceTestEnvironment.Character(account, 0, characterId) with
			{
				SchemaState = new TypedPayload
				{
					TypeId = new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
					TypeVersion = 1,
					Data = System.Text.Json.JsonSerializer.SerializeToElement("not-an-object")
				}
			};
			await using var unit = first.BeginUnitOfWork();
			unit.Create(repositories.Characters, DomainKeys.Character(characterId), malformed);
			unit.Create(repositories.CharacterSlots, DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = characterId });
			unit.Create(repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unit.Create(repositories.OwnerInventories, DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
			Assert.IsTrue((await unit.CommitAsync()).Succeeded);
			Assert.IsTrue( (await first.ShutdownAsync()).IsClean );
		}

		await using var recovered = RecoveryProvider(storage, RecoveryRegistry());
		await recovered.InitializeAsync();
		var recoveredRepositories = new DomainRepositories(recovered);
		var profile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
			new[] { ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.ItemDefinitionId) },
			Array.Empty<KeyValuePair<string, PersistedTypeId>>(),
			Array.Empty<KeyValuePair<string, PersistedTypeId>>());
		var report = new DomainInvariantValidator(
			recoveredRepositories,
			schema,
			new SchemaItemShapeCatalog(schema, recoveredRepositories),
			profile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Path.EndsWith("schema-state", StringComparison.Ordinal) &&
			issue.Message.Contains("could not be decoded", StringComparison.Ordinal)));
	}

	[TestMethod]
	public async Task NonCanonicalNestedPayloadFailsCodecRoundTripInvariant()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9009);
		var character = ApplicationServiceTestEnvironment.Character(account, 0) with
		{
			SchemaState = new TypedPayload
			{
				TypeId = new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
				TypeVersion = 1,
				Data = System.Text.Json.JsonDocument.Parse("{ \"Name\" : \"test\" }").RootElement.Clone()
			}
		};
		var inventory = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(character.Id));
		await environment.SeedAsync(unit =>
		{
			unit.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			unit.Create(environment.Repositories.CharacterSlots, DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unit.Create(environment.Repositories.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard(character.Id),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 });
			unit.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unit.Create(environment.Repositories.OwnerInventories, DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
		});

		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			environment.PersistenceProfile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue =>
			issue.Path.EndsWith("schema-state", StringComparison.Ordinal) &&
			issue.Code == ErrorCode.PersistedTypeInvalid));
	}

	[TestMethod]
	public async Task EquivalentJsonStringEscapesPassCodecRoundTripInvariant()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId( 9011 );
		var character = ApplicationServiceTestEnvironment.Character( account, 0 ) with
		{
			SchemaState = new TypedPayload
			{
				TypeId = new PersistedTypeId( ApplicationServiceTestEnvironment.StateTypeId ),
				TypeVersion = 1,
				Data = System.Text.Json.JsonDocument.Parse( "{\"name\":\"A\\u002BB\"}" ).RootElement.Clone()
			}
		};
		var inventory = ApplicationServiceTestEnvironment.Inventory( InventoryOwner.Character( character.Id ) );
		await environment.SeedAsync( unit =>
		{
			unit.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( environment.Repositories.CharacterSlots, DomainKeys.CharacterSlot( account, 0 ),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id } );
			unit.Create( environment.Repositories.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unit.Create( environment.Repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unit.Create( environment.Repositories.OwnerInventories, DomainKeys.OwnerInventory( inventory.Owner, "main" ),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id } );
		} );

		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog( environment.Schema, environment.Repositories ),
			environment.PersistenceProfile ).Validate();

		Assert.IsTrue( report.IsValid, string.Join( Environment.NewLine, report.Issues.Select( issue => issue.Message ) ) );
	}

	[TestMethod]
	public async Task RecoveryRejectsIntegerExtremeInventoryGeometryBeforeReady()
	{
		var storage = new InMemoryPersistenceStorage();
		var schemaResult = SchemaCompiler.Compile(new RecoverySchema());
		Assert.IsTrue(schemaResult.Succeeded, schemaResult.Error?.Message);
		var schema = schemaResult.Value;
		var characterId = CharacterId.New();
		var account = new AccountId(9010);
		var character = ApplicationServiceTestEnvironment.Character(account, 0, characterId);
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(characterId),
			new[] { new InventoryPlacement(item.Id, int.MaxValue, int.MaxValue) });
		await using (var writer = RecoveryProvider(storage, RecoveryRegistry()))
		{
			await writer.InitializeAsync();
			var repositories = new DomainRepositories(writer);
			await using var unit = writer.BeginUnitOfWork();
			unit.Create(repositories.Characters, DomainKeys.Character(characterId), character);
			unit.Create(repositories.CharacterSlots, DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = characterId });
			unit.Create(repositories.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard(characterId),
				new CharacterLifecycleGuardRecord { CharacterId = characterId, ReferenceRevision = 0 });
			unit.Create(repositories.Items, DomainKeys.Item(item.Id), item);
			unit.Create(repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unit.Create(repositories.OwnerInventories, DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
			Assert.IsTrue((await unit.CommitAsync()).Succeeded);
			Assert.IsTrue((await writer.ShutdownAsync()).IsClean);
		}

		var readerTypes = RecoveryRegistry();
		var profile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
			new[] { ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.ItemDefinitionId) },
			Array.Empty<KeyValuePair<string, PersistedTypeId>>(),
			Array.Empty<KeyValuePair<string, PersistedTypeId>>());
		var invariants = new DomainInvariantValidator(
			readerTypes, schema, new SchemaItemShapeCatalog(schema), profile);
		await using var recovered = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions("outer-envelope-test") { CheckpointEveryCommits = 0 },
			readerTypes,
			invariants);

		await Assert.ThrowsAsync<PersistenceCorruptionException>(async () => await recovered.InitializeAsync());
		Assert.AreEqual(PersistenceProviderState.Faulted, recovered.State);
	}

	[TestMethod]
	public async Task ReverseGuardsReservationsAndReferenceTargetsFailClosed()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9006);
		var character = ApplicationServiceTestEnvironment.Character(account, 0);
		var inventory = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(character.Id));
		var missingCharacter = CharacterId.New();
		var missingRelated = CharacterId.New();
		var missingScene = SceneEntityId.New();
		await environment.SeedAsync(unit =>
		{
			unit.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			unit.Create(environment.Repositories.CharacterSlots, DomainKeys.CharacterSlot(account, 0),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unit.Create(environment.Repositories.CharacterSlots, "duplicate-slot-key",
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id });
			unit.Create(environment.Repositories.CharacterSlots, DomainKeys.CharacterSlot(new AccountId(9007), 1),
				new CharacterSlotRecord { AccountId = new AccountId(9007), Slot = 1, CharacterId = missingCharacter });
			unit.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
			unit.Create(environment.Repositories.OwnerInventories, DomainKeys.OwnerInventory(inventory.Owner, "main"),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id });
			unit.Create(environment.Repositories.UniqueReservations, "forged-reservation-key",
				new UniqueReservationRecord { Namespace = "test.cid", Value = "12345", CharacterId = character.Id });
			unit.Create(environment.Repositories.UniqueReservations, DomainKeys.UniqueReservation("test.cid", "12345"),
				new UniqueReservationRecord { Namespace = "test.cid", Value = "12345", CharacterId = missingCharacter });
			unit.Create(environment.Repositories.CharacterReferences, "strict-reference", new CharacterReferenceRecord
			{
				Category = "strict",
				CharacterId = missingCharacter,
				RelatedCharacterId = missingRelated,
				SceneEntityId = missingScene,
				State = ApplicationServiceTestEnvironment.StatePayload()
			});
			unit.Create(environment.Repositories.CharacterReferences, "external-reference", new CharacterReferenceRecord
			{
				Category = "external",
				CharacterId = missingCharacter,
				RelatedCharacterId = missingRelated,
				SceneEntityId = missingScene,
				State = ApplicationServiceTestEnvironment.StatePayload()
			});
		});
		var profile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId),
			new[]
			{
				ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.ItemDefinitionId),
				ItemPersistenceContract.WithoutTraits(ApplicationServiceTestEnvironment.BagDefinitionId)
			},
			new[]
			{
				new CharacterReferencePersistenceContract(
					"strict", new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId)),
				new CharacterReferencePersistenceContract(
					"external", new PersistedTypeId(ApplicationServiceTestEnvironment.StateTypeId), true, true, true)
			},
			Array.Empty<KeyValuePair<string, PersistedTypeId>>());
		var report = new DomainInvariantValidator(
			environment.Repositories,
			environment.Schema,
			new SchemaItemShapeCatalog(environment.Schema, environment.Repositories),
			profile).Validate();

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Logical owner-slot guard is duplicated", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Owner-slot guard references 0 characters", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Logical unique reservation is duplicated", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Message.Contains("Unique reservation references a missing character", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path == "character-reference/strict-reference/character"));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path == "character-reference/strict-reference/related-character"));
		Assert.IsTrue(report.Issues.Any(issue => issue.Path == "character-reference/strict-reference/scene-entity"));
		Assert.IsFalse(report.Issues.Any(issue => issue.Path.StartsWith("character-reference/external-reference/", StringComparison.Ordinal) &&
			!issue.Path.EndsWith("/state", StringComparison.Ordinal)));
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task TenThousandPlacementsUseBoundedOverlapValidation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		const int itemCount = 10_000;
		var documents = new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		var itemCodec = environment.Provider.Types.Resolve<ItemRecord>();
		var inventoryCodec = environment.Provider.Types.Resolve<InventoryRecord>();
		var placements = new InventoryPlacement[itemCount];
		for ( var index = 0; index < itemCount; index++ )
		{
			var item = ApplicationServiceTestEnvironment.Item(
				id: new ItemId( IndexedGuid( index, 1 ) ) );
			var address = new DocumentAddress( DomainCollections.Items, DomainKeys.Item( item.Id ) );
			documents.Add( address, new PersistenceCandidateDocument(
				address, new DocumentRevision( 1 ), itemCodec.Key, itemCodec.CurrentVersion, item ) );
			placements[index] = new InventoryPlacement(
				item.Id,
				index == itemCount - 1 ? itemCount - 2 : index,
				0 );
		}
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.SceneEntity( new SceneEntityId( IndexedGuid( 1, 2 ) ) ),
			placements,
			width: itemCount,
			height: 1,
			id: new InventoryId( IndexedGuid( 1, 3 ) ) );
		var inventoryAddress = new DocumentAddress(
			DomainCollections.Inventories, DomainKeys.Inventory( inventory.Id ) );
		documents.Add( inventoryAddress, new PersistenceCandidateDocument(
			inventoryAddress,
			new DocumentRevision( 1 ),
			inventoryCodec.Key,
			inventoryCodec.CurrentVersion,
			inventory ) );

		var issues = new DomainInvariantValidator(
			environment.Provider.Types,
			environment.Schema,
			new SchemaItemShapeCatalog( environment.Schema ),
			environment.PersistenceProfile ).Validate(
				new PersistenceInvariantContext( 1, 0, documents, isRecovery: true ) );

		Assert.IsTrue( issues.Any( issue => issue.Message.Contains( "overlap", StringComparison.Ordinal ) ) );
	}

	[TestMethod]
	[Timeout(30_000, CooperativeCancellation = true)]
	public async Task TenThousandInventoryOwnershipChainUsesLinearCycleValidation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		const int inventoryCount = 10_000;
		var documents = new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		var inventoryCodec = environment.Provider.Types.Resolve<InventoryRecord>();
		var parentItems = Enumerable.Range( 0, inventoryCount )
			.Select( index => new ItemId( IndexedGuid( index, 4 ) ) )
			.ToArray();
		for ( var index = 0; index < inventoryCount; index++ )
		{
			var inventory = ApplicationServiceTestEnvironment.Inventory(
				InventoryOwner.ParentItem( parentItems[index] ),
				index == 0
					? Array.Empty<InventoryPlacement>()
					: new[] { new InventoryPlacement( parentItems[index - 1], 0, 0 ) },
				width: 1,
				height: 1,
				id: new InventoryId( IndexedGuid( index, 5 ) ) );
			var address = new DocumentAddress(
				DomainCollections.Inventories, DomainKeys.Inventory( inventory.Id ) );
			documents.Add( address, new PersistenceCandidateDocument(
				address,
				new DocumentRevision( 1 ),
				inventoryCodec.Key,
				inventoryCodec.CurrentVersion,
				inventory ) );
		}

		var issues = new DomainInvariantValidator(
			environment.Provider.Types,
			environment.Schema,
			new SchemaItemShapeCatalog( environment.Schema ),
			environment.PersistenceProfile ).Validate(
				new PersistenceInvariantContext( 1, 0, documents, isRecovery: true ) );

		Assert.IsFalse( issues.Any( issue => issue.Message.Contains( "cycle", StringComparison.Ordinal ) ) );
	}

	[TestMethod]
	public async Task CommitValidationRoundTripsOnlyChangedPayloadsWhileRecoveryChecksAllPayloads()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var unchanged = ApplicationServiceTestEnvironment.Character( new AccountId( 9101 ), 0 );
		unchanged = unchanged with
		{
			SchemaState = unchanged.SchemaState with
			{
				Data = JsonSerializer.SerializeToElement( new { name = 42 } )
			}
		};
		var changed = ApplicationServiceTestEnvironment.Character( new AccountId( 9102 ), 0 );
		var codec = environment.Provider.Types.Resolve<CharacterRecord>();
		var unchangedAddress = new DocumentAddress(
			DomainCollections.Characters, DomainKeys.Character( unchanged.Id ) );
		var changedAddress = new DocumentAddress(
			DomainCollections.Characters, DomainKeys.Character( changed.Id ) );
		var documents = new Dictionary<DocumentAddress, PersistenceCandidateDocument>
		{
			[unchangedAddress] = new(
				unchangedAddress, new DocumentRevision( 1 ), codec.Key, codec.CurrentVersion, unchanged ),
			[changedAddress] = new(
				changedAddress, new DocumentRevision( 1 ), codec.Key, codec.CurrentVersion, changed )
		};
		var validator = new DomainInvariantValidator(
			environment.Provider.Types,
			environment.Schema,
			new SchemaItemShapeCatalog( environment.Schema ),
			environment.PersistenceProfile );

		var commitIssues = validator.Validate( new PersistenceInvariantContext(
			2,
			0,
			documents,
			previousDocuments: documents,
			changedAddresses: new HashSet<DocumentAddress> { changedAddress } ) );
		var recoveryIssues = validator.Validate( new PersistenceInvariantContext(
			2, 0, documents, isRecovery: true ) );

		Assert.IsFalse( commitIssues.Any( issue => issue.Path == $"character/{unchangedAddress.Key}/schema-state" ) );
		Assert.IsTrue( recoveryIssues.Any( issue => issue.Path == $"character/{unchangedAddress.Key}/schema-state" ) );
	}

	[TestMethod]
	public async Task IncrementalValidationMatchesFullValidationForChangedDependencyClosure()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var documents = BuildIndependentCharacterGraphs( environment, 200 );
		var validator = IncrementalValidator( environment );
		var recovery = new PersistenceInvariantContext( 1, 0, documents, isRecovery: true );
		Assert.IsEmpty( validator.Validate( recovery ) );
		validator.Rebuild( recovery );

		var changedAddress = documents.Keys.First( address => address.Collection == DomainCollections.Characters );
		var previous = documents[changedAddress];
		var character = (CharacterRecord)previous.Value;
		var changedDocument = previous with
		{
			Revision = new DocumentRevision( 2 ),
			Value = character with { AccountId = new AccountId( character.AccountId.Value + 100_000 ) }
		};
		var changed = new HashSet<DocumentAddress> { changedAddress };
		var delta = new Dictionary<DocumentAddress, PersistenceCandidateDocument?>
		{
			[changedAddress] = changedDocument
		};
		var incrementalContext = new PersistenceInvariantContext(
			2, 0, new PersistenceInvariantDocumentIndex( documents.Values ), delta, changed );
		var incremental = validator.Prepare( incrementalContext );

		var fullDocuments = new Dictionary<DocumentAddress, PersistenceCandidateDocument>( documents )
		{
			[changedAddress] = changedDocument
		};
		var full = validator.Validate( new PersistenceInvariantContext(
			2, 0, fullDocuments, documents, changed ) );

		AssertIssueSetsEqual( full, incremental.Issues );
		Assert.IsNotEmpty( full );
	}

	[TestMethod]
	[DataRow( 1_000 )]
	[DataRow( 10_000 )]
	[DataRow( 100_000 )]
	[Timeout( 30_000, CooperativeCancellation = true )]
	public async Task IncrementalValidationWorkRemainsBoundedAcrossStoreSizes( int documentCount )
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		const int documentsPerGraph = 5;
		Assert.AreEqual( 0, documentCount % documentsPerGraph );
		var graphCount = documentCount / documentsPerGraph;
		var documents = BuildIndependentCharacterGraphs( environment, graphCount );
		var validator = IncrementalValidator( environment );
		var recovery = new PersistenceInvariantContext( 1, 0, documents, isRecovery: true );
		Assert.IsEmpty( validator.Validate( recovery ) );
		validator.Rebuild( recovery );
		Assert.AreEqual( documentCount, validator.IndexedDocumentCount );

		var changedAddress = documents.Keys.First( address => address.Collection == DomainCollections.Characters );
		var previous = documents[changedAddress];
		var character = (CharacterRecord)previous.Value;
		var changedDocument = previous with
		{
			Revision = new DocumentRevision( 2 ),
			Value = character with { Name = character.Name + " updated" }
		};
		var changed = new HashSet<DocumentAddress> { changedAddress };
		var delta = new Dictionary<DocumentAddress, PersistenceCandidateDocument?>
		{
			[changedAddress] = changedDocument
		};
		var context = new PersistenceInvariantContext(
			2, 0, new PersistenceInvariantDocumentIndex( documents.Values ), delta, changed );

		var prepared = validator.Prepare( context );

		Assert.IsEmpty( prepared.Issues );
		Assert.IsLessThanOrEqualTo(
			16L,
			context.VisitedDocumentCount,
			$"One-record validation visited {context.VisitedDocumentCount} candidate documents in a {documentCount}-document store." );
		validator.Publish( prepared );
		Assert.AreEqual( documentCount, validator.IndexedDocumentCount );
	}

	[TestMethod]
	public async Task RandomizedIncrementalMutationsMatchFullValidation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		const int graphCount = 128;
		const int mutationKindCount = 8;
		const int mutationCount = 48;
		var documents = BuildIndependentCharacterGraphs( environment, graphCount );
		var baseIndex = new PersistenceInvariantDocumentIndex( documents.Values );
		var validator = IncrementalValidator( environment );
		var recovery = new PersistenceInvariantContext( 1, 0, documents, isRecovery: true );
		Assert.IsEmpty( validator.Validate( recovery ) );
		validator.Rebuild( recovery );
		var random = new Random( 0x5EED_2026 );
		var coveredMutationKinds = new bool[mutationKindCount];

		for ( var iteration = 0; iteration < mutationCount; iteration++ )
		{
			var graphIndex = random.Next( graphCount );
			var mutationKind = iteration < mutationKindCount ? iteration : random.Next( mutationKindCount );
			coveredMutationKinds[mutationKind] = true;
			var addresses = IndependentGraphAddresses( graphIndex );
			var delta = new Dictionary<DocumentAddress, PersistenceCandidateDocument?>();
			switch ( mutationKind )
			{
				case 0:
				{
					var document = documents[addresses.Character];
					var character = (CharacterRecord)document.Value;
					delta.Add( addresses.Character, ReplaceValue(
						document,
						character with { Name = $"{character.Name}-{random.Next():x8}" } ) );
					break;
				}
				case 1:
				{
					var document = documents[addresses.Character];
					var character = (CharacterRecord)document.Value;
					delta.Add( addresses.Character, ReplaceValue(
						document,
						character with { AccountId = new AccountId( (ulong)(1_000_000 + random.Next( 1_000_000 )) ) } ) );
					break;
				}
				case 2:
					delta.Add( addresses.Slot, null );
					break;
				case 3:
				{
					var document = documents[addresses.Guard];
					var guard = (CharacterLifecycleGuardRecord)document.Value;
					delta.Add( addresses.Guard, ReplaceValue(
						document,
						guard with { CharacterId = new CharacterId( IndexedGuid( graphIndex, 12 ) ) } ) );
					break;
				}
				case 4:
				{
					var document = documents[addresses.OwnerInventory];
					var ownerInventory = (OwnerInventoryRecord)document.Value;
					delta.Add( addresses.OwnerInventory, ReplaceValue(
						document,
						ownerInventory with { InventoryId = new InventoryId( IndexedGuid( graphIndex, 13 ) ) } ) );
					break;
				}
				case 5:
				{
					var document = documents[addresses.Inventory];
					var inventory = (InventoryRecord)document.Value;
					var otherCharacter = new CharacterId( IndexedGuid( (graphIndex + 1) % graphCount, 10 ) );
					delta.Add( addresses.Inventory, ReplaceValue(
						document,
						inventory with { Owner = InventoryOwner.Character( otherCharacter ) } ) );
					break;
				}
				case 6:
				{
					var source = documents[addresses.Character];
					var forgedAddress = new DocumentAddress(
						DomainCollections.Characters, $"forged-{iteration}-{random.Next():x8}" );
					delta.Add( forgedAddress, source with
					{
						Address = forgedAddress,
						Revision = new DocumentRevision( 1 )
					} );
					break;
				}
				case 7:
					delta.Add( addresses.Character, null );
					delta.Add( addresses.Slot, null );
					delta.Add( addresses.Guard, null );
					delta.Add( addresses.Inventory, null );
					delta.Add( addresses.OwnerInventory, null );
					break;
				default:
					Assert.Fail( $"Unknown mutation kind {mutationKind}." );
					break;
			}

			AssertIncrementalMatchesFull( validator, baseIndex, documents, delta, iteration + 2 );
		}

		Assert.IsTrue( coveredMutationKinds.All( covered => covered ) );
	}

	private static DomainInvariantValidator IncrementalValidator( ApplicationServiceTestEnvironment environment ) => new(
		environment.Provider.Types,
		environment.Schema,
		new SchemaItemShapeCatalog( environment.Schema ),
		environment.PersistenceProfile );

	private static Dictionary<DocumentAddress, PersistenceCandidateDocument> BuildIndependentCharacterGraphs(
		ApplicationServiceTestEnvironment environment,
		int count )
	{
		var documents = new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		for ( var index = 0; index < count; index++ )
		{
			var account = new AccountId( (ulong)(20_000 + index) );
			var character = ApplicationServiceTestEnvironment.Character(
				account, 0, new CharacterId( IndexedGuid( index, 10 ) ) );
			var inventory = ApplicationServiceTestEnvironment.Inventory(
				InventoryOwner.Character( character.Id ),
				id: new InventoryId( IndexedGuid( index, 11 ) ) );
			AddCandidate(
				documents,
				environment.Provider.Types,
				DomainCollections.Characters,
				DomainKeys.Character( character.Id ),
				character );
			AddCandidate(
				documents,
				environment.Provider.Types,
				DomainCollections.CharacterSlots,
				DomainKeys.CharacterSlot( account, 0 ),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id } );
			AddCandidate(
				documents,
				environment.Provider.Types,
				DomainCollections.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			AddCandidate(
				documents,
				environment.Provider.Types,
				DomainCollections.Inventories,
				DomainKeys.Inventory( inventory.Id ),
				inventory );
			AddCandidate(
				documents,
				environment.Provider.Types,
				DomainCollections.OwnerInventories,
				DomainKeys.OwnerInventory( inventory.Owner, "main" ),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id } );
		}
		return documents;
	}

	private static void AddCandidate<T>(
		IDictionary<DocumentAddress, PersistenceCandidateDocument> documents,
		PersistedTypeRegistry types,
		string collection,
		string key,
		T value ) where T : class
	{
		var codec = types.Resolve<T>();
		var address = new DocumentAddress( collection, key );
		documents.Add( address, new PersistenceCandidateDocument(
			address, new DocumentRevision( 1 ), codec.Key, codec.CurrentVersion, value ) );
	}

	private static void AssertIssueSetsEqual(
		IReadOnlyList<PersistenceInvariantIssue> expected,
		IReadOnlyList<PersistenceInvariantIssue> actual )
	{
		static string Key( PersistenceInvariantIssue issue ) => $"{issue.Code}|{issue.Path}|{issue.Message}";
		var expectedKeys = expected.Select( Key ).OrderBy( key => key, StringComparer.Ordinal ).ToArray();
		var actualKeys = actual.Select( Key ).OrderBy( key => key, StringComparer.Ordinal ).ToArray();
		CollectionAssert.AreEqual( expectedKeys, actualKeys );
	}

	private static void AssertIncrementalMatchesFull(
		DomainInvariantValidator validator,
		PersistenceInvariantDocumentIndex baseIndex,
		IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument> documents,
		IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument?> delta,
		long sequence )
	{
		var changed = delta.Keys.ToHashSet();
		var incremental = validator.Prepare( new PersistenceInvariantContext(
			sequence, 0, baseIndex, delta, changed ) );
		var fullDocuments = new Dictionary<DocumentAddress, PersistenceCandidateDocument>( documents );
		foreach ( var change in delta )
		{
			if ( change.Value is null ) fullDocuments.Remove( change.Key );
			else fullDocuments[change.Key] = change.Value;
		}
		var full = validator.Validate( new PersistenceInvariantContext(
			sequence, 0, fullDocuments, documents, changed ) );
		AssertIssueSetsEqual( full, incremental.Issues );
	}

	private static PersistenceCandidateDocument ReplaceValue<T>(
		PersistenceCandidateDocument document,
		T value ) where T : class => document with
	{
		Revision = new DocumentRevision( document.Revision.Value + 1 ),
		Value = value
	};

	private static IndependentGraphDocumentAddresses IndependentGraphAddresses( int index )
	{
		var character = new CharacterId( IndexedGuid( index, 10 ) );
		var inventory = new InventoryId( IndexedGuid( index, 11 ) );
		var owner = InventoryOwner.Character( character );
		return new IndependentGraphDocumentAddresses(
			new DocumentAddress( DomainCollections.Characters, DomainKeys.Character( character ) ),
			new DocumentAddress( DomainCollections.CharacterSlots, DomainKeys.CharacterSlot( new AccountId( (ulong)(20_000 + index) ), 0 ) ),
			new DocumentAddress( DomainCollections.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard( character ) ),
			new DocumentAddress( DomainCollections.Inventories, DomainKeys.Inventory( inventory ) ),
			new DocumentAddress( DomainCollections.OwnerInventories, DomainKeys.OwnerInventory( owner, "main" ) ) );
	}

	private sealed record IndependentGraphDocumentAddresses(
		DocumentAddress Character,
		DocumentAddress Slot,
		DocumentAddress Guard,
		DocumentAddress Inventory,
		DocumentAddress OwnerInventory );

	private static Guid IndexedGuid( int index, byte discriminator )
	{
		var bytes = new byte[16];
		BitConverter.GetBytes( index + 1 ).CopyTo( bytes, 0 );
		bytes[15] = discriminator;
		return new Guid( bytes );
	}

	private static PersistedTypeRegistry RecoveryRegistry() => new PersistedTypeRegistry()
		.RegisterHexagonDomainTypes()
		.Register<TestCharacterState>(
			new PersistedTypeKey(ApplicationServiceTestEnvironment.StateTypeId),
			1,
			PersistedValuePublication.Immutable);

	private static FileSystemPersistenceProvider RecoveryProvider(
		IPersistenceStorage storage,
		PersistedTypeRegistry registry) => new(
		storage,
		new FileSystemPersistenceOptions("outer-envelope-test"),
		registry);

	private sealed class RecoverySchema : IHexSchema
	{
		public string Id => "outer_envelope_test";

		public void Configure(SchemaBuilder builder)
		{
			builder.RegisterItem(new ItemDefinition(
				ApplicationServiceTestEnvironment.ItemDefinitionId,
				Array.Empty<string>()));
			builder.RegisterPersistedType(new PersistedTypeRegistration(
				ApplicationServiceTestEnvironment.StateTypeId,
				typeof(TestCharacterState),
				1));
		}
	}
}
