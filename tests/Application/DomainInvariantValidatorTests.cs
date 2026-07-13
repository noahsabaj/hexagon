#nullable enable

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
			await first.DrainAsync();
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
