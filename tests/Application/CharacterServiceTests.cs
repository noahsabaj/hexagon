#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class CharacterServiceTests
{
	[TestMethod]
	public async Task ClassCapacityUsesADurableReservationAndReleasesOnDeletion()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync( classCapacity: 1 );
		var service = environment.CreateCharacterService();
		var firstAccount = new AccountId( 9001 );
		var secondAccount = new AccountId( 9002 );

		var first = await service.CreateAsync( firstAccount, ApplicationServiceTestEnvironment.Request() );
		var full = await service.CreateAsync( secondAccount, ApplicationServiceTestEnvironment.Request() );
		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.AreEqual( ErrorCode.Conflict, full.Error!.Code );
		Assert.AreEqual( 1, environment.Repositories.UniqueReservations.All().Count( value =>
			value.Value.Namespace == "hexagon.class-capacity" ) );

		Assert.IsTrue( (await service.DeleteAsync( firstAccount, first.Value.Character.Id )).Succeeded );
		var afterRelease = await service.CreateAsync( secondAccount, ApplicationServiceTestEnvironment.Request() );
		Assert.IsTrue( afterRelease.Succeeded, afterRelease.Error?.Message );
	}

	[TestMethod]
	public async Task ConcurrentClassCreationCannotExceedCapacity()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync( classCapacity: 1 );
		var service = environment.CreateCharacterService();

		var results = await Task.WhenAll(
			service.CreateAsync( new AccountId( 9101 ), ApplicationServiceTestEnvironment.Request() ).AsTask(),
			service.CreateAsync( new AccountId( 9102 ), ApplicationServiceTestEnvironment.Request() ).AsTask() );

		Assert.AreEqual( 1, results.Count( result => result.Succeeded ) );
		Assert.AreEqual( 1, results.Count( result => result.Error?.Code == ErrorCode.Conflict ) );
		Assert.HasCount( 1, environment.Repositories.Characters.All() );
	}

	[TestMethod]
	public async Task CreateAndDeleteCommitTheCompleteNestedAggregate()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var initializer = new TestCharacterInitializer("loadout", (_, _) =>
			OperationResult<CharacterInitializerContribution>.Success(new CharacterInitializerContribution
			{
				Items = new[]
				{
					new ItemGrantPlan
					{
						Definition = new DefinitionId(ApplicationServiceTestEnvironment.BagDefinitionId),
						Bag = new BagInventoryPlan
						{
							Width = 2,
							Height = 2,
							Items = new[]
							{
								new ItemGrantPlan
								{
									Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId)
								}
							}
						}
					}
				},
				Reservations = new[] { new UniqueReservationPlan("cid", "12345") }
			}));
		var service = environment.CreateCharacterService(new[] { initializer });
		var account = new AccountId(7656119);

		var created = await service.CreateAsync(account, ApplicationServiceTestEnvironment.Request());

		Assert.IsTrue(created.Succeeded, created.Error?.Message);
		Assert.AreEqual(account, created.Value.Character.AccountId);
		Assert.AreEqual(0, created.Value.Character.Slot);
		Assert.AreEqual("Alyx Vance", created.Value.Character.Name);
		Assert.AreEqual(new ClassId("worker"), created.Value.Character.Class);
		Assert.AreEqual(25L, created.Value.Character.Balance);
		Assert.HasCount(2, created.Value.Items);
		Assert.HasCount(1, environment.Repositories.Characters.All());
		Assert.HasCount(1, environment.Repositories.CharacterSlots.All());
		Assert.HasCount(2, environment.Repositories.Inventories.All());
		Assert.HasCount(2, environment.Repositories.OwnerInventories.All());
		Assert.HasCount(2, environment.Repositories.Items.All());
		Assert.HasCount(1, environment.Repositories.UniqueReservations.All());

		var deleted = await service.DeleteAsync(account, created.Value.Character.Id);

		Assert.IsTrue(deleted.Succeeded, deleted.Error?.Message);
		Assert.IsEmpty(environment.Repositories.Characters.All());
		Assert.IsEmpty(environment.Repositories.CharacterSlots.All());
		Assert.IsEmpty(environment.Repositories.Inventories.All());
		Assert.IsEmpty(environment.Repositories.OwnerInventories.All());
		Assert.IsEmpty(environment.Repositories.Items.All());
		Assert.IsEmpty(environment.Repositories.UniqueReservations.All());
	}

	[TestMethod]
	public async Task CreateSelectsTheLowestFreeOwnerSlot()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(7656120);
		var first = ApplicationServiceTestEnvironment.Character(account, 0);
		var third = ApplicationServiceTestEnvironment.Character(account, 2);
		await environment.SeedAsync(unitOfWork =>
		{
			SeedCharacter(unitOfWork, environment, first);
			SeedCharacter(unitOfWork, environment, third);
		});
		var service = environment.CreateCharacterService();

		var created = await service.CreateAsync(account, ApplicationServiceTestEnvironment.Request());

		Assert.IsTrue(created.Succeeded, created.Error?.Message);
		Assert.AreEqual(1, created.Value.Character.Slot);
		CollectionAssert.AreEqual(
			new[] { 0, 1, 2 },
			service.ListForAccount(account).Select(character => character.Slot).ToArray());
	}

	[TestMethod]
	public async Task CreationFieldsAreStrictlyAllowlistedRequiredAndTyped()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var service = environment.CreateCharacterService();
		var account = new AccountId(7656121);
		var unknown = ApplicationServiceTestEnvironment.Request(
			new Dictionary<string, CreationValue>(StringComparer.Ordinal)
			{
				["nickname"] = CreationValue.String("alyx"),
				["money"] = CreationValue.Integer(long.MaxValue)
			});
		var wrongType = ApplicationServiceTestEnvironment.Request(
			new Dictionary<string, CreationValue>(StringComparer.Ordinal)
			{
				["nickname"] = CreationValue.Integer(42)
			});
		var missing = ApplicationServiceTestEnvironment.Request(
			new Dictionary<string, CreationValue>(StringComparer.Ordinal));

		var unknownResult = await service.CreateAsync(account, unknown);
		var wrongTypeResult = await service.CreateAsync(account, wrongType);
		var missingResult = await service.CreateAsync(account, missing);

		Assert.AreEqual(ErrorCode.InvalidArgument, unknownResult.Error!.Code);
		Assert.AreEqual(ErrorCode.InvalidArgument, wrongTypeResult.Error!.Code);
		Assert.AreEqual(ErrorCode.InvalidArgument, missingResult.Error!.Code);
		Assert.IsEmpty(environment.Repositories.Characters.All());
		Assert.IsEmpty(environment.Repositories.Inventories.All());
	}

	[TestMethod]
	public async Task FullInitialInventoryRejectsTheWholeCreationBeforeCommit()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var initializer = new TestCharacterInitializer("oversized_loadout", (_, _) =>
			OperationResult<CharacterInitializerContribution>.Success(new CharacterInitializerContribution
			{
				Items = new[]
				{
					new ItemGrantPlan { Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId) },
					new ItemGrantPlan { Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId) }
				}
			}));
		var service = environment.CreateCharacterService(new[] { initializer }, 1, 1);

		var result = await service.CreateAsync(
			new AccountId(7656122),
			ApplicationServiceTestEnvironment.Request());

		Assert.IsTrue(result.Failed);
		Assert.AreEqual(ErrorCode.Conflict, result.Error!.Code);
		AssertAllCreationCollectionsEmpty(environment);
	}

	[TestMethod]
	public async Task InjectedCommitFailurePublishesNoPartialCreation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var initializer = new TestCharacterInitializer("loadout", (_, _) =>
			OperationResult<CharacterInitializerContribution>.Success(new CharacterInitializerContribution
			{
				Items = new[]
				{
					new ItemGrantPlan { Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId) }
				},
				Reservations = new[] { new UniqueReservationPlan("cid", "67890") }
			}));
		var service = environment.CreateCharacterService(new[] { initializer });
		environment.Provider.FailNextCommit();

		var result = await service.CreateAsync(
			new AccountId(7656123),
			ApplicationServiceTestEnvironment.Request());

		Assert.IsTrue(result.Failed);
		Assert.AreEqual(ErrorCode.InternalError, result.Error!.Code);
		AssertAllCreationCollectionsEmpty(environment);
	}

	[TestMethod]
	public async Task DeleteRemovesCharacterReferencesButRetainsSeparateWorldBagGraph()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(7656124);
		var character = ApplicationServiceTestEnvironment.Character(account, 0);
		var mainInventory = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.Character(character.Id));
		var worldBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var nestedItem = ApplicationServiceTestEnvironment.Item();
		var worldBagInventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.ParentItem(worldBag.Id),
			new[] { new InventoryPlacement(nestedItem.Id, 0, 0) });
		var otherCharacter = CharacterId.New();
		await environment.SeedAsync(unitOfWork =>
		{
			SeedCharacter(unitOfWork, environment, character);
			unitOfWork.Create(
				environment.Repositories.Inventories,
				DomainKeys.Inventory(mainInventory.Id),
				mainInventory);
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory(mainInventory.Owner, "main"),
				new OwnerInventoryRecord
				{
					Role = "main",
					Owner = mainInventory.Owner,
					InventoryId = mainInventory.Id
				});
			foreach (var item in new[] { worldBag, nestedItem })
				unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(
				environment.Repositories.Inventories,
				DomainKeys.Inventory(worldBagInventory.Id),
				worldBagInventory);
			unitOfWork.Create(
				environment.Repositories.OwnerInventories,
				DomainKeys.OwnerInventory(worldBagInventory.Owner, "bag"),
				new OwnerInventoryRecord
				{
					Role = "bag",
					Owner = worldBagInventory.Owner,
					InventoryId = worldBagInventory.Id
				});
			unitOfWork.Create(
				environment.Repositories.WorldItems,
				DomainKeys.WorldItem(worldBag.Id),
				new WorldItemRecord
				{
					ItemId = worldBag.Id,
					Transform = ApplicationServiceTestEnvironment.Transform()
				});
			unitOfWork.Create(
				environment.Repositories.CharacterReferences,
				"primary",
				Reference("door", character.Id, null));
			unitOfWork.Create(
				environment.Repositories.CharacterReferences,
				"related",
				Reference("recognition", otherCharacter, character.Id));
			unitOfWork.Create(
				environment.Repositories.CharacterReferences,
				"unrelated",
				Reference("recognition", otherCharacter, CharacterId.New()));
		});

		var result = await environment.CreateCharacterService().DeleteAsync(account, character.Id);

		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		Assert.IsEmpty(environment.Repositories.Characters.All());
		Assert.IsEmpty(environment.Repositories.CharacterSlots.All());
		Assert.HasCount(1, environment.Repositories.CharacterReferences.All());
		Assert.AreEqual("unrelated", environment.Repositories.CharacterReferences.All()[0].Key);
		Assert.HasCount(1, environment.Repositories.WorldItems.All());
		Assert.HasCount(2, environment.Repositories.Items.All());
		Assert.HasCount(1, environment.Repositories.Inventories.All());
		Assert.AreEqual(worldBagInventory.Id, environment.Repositories.Inventories.All()[0].Value.Id);
		Assert.HasCount(1, environment.Repositories.OwnerInventories.All());
	}

	[TestMethod]
	public async Task ExistingUniqueReservationRejectsCreationAtomically()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		await environment.SeedAsync(unitOfWork =>
			unitOfWork.Create(
				environment.Repositories.UniqueReservations,
				DomainKeys.UniqueReservation("cid", "12345"),
				new UniqueReservationRecord
				{
					Namespace = "cid",
					Value = "12345",
					CharacterId = CharacterId.New()
				}));
		var initializer = new TestCharacterInitializer("duplicate_cid", (_, _) =>
			OperationResult<CharacterInitializerContribution>.Success(new CharacterInitializerContribution
			{
				Items = new[]
				{
					new ItemGrantPlan { Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId) }
				},
				Reservations = new[] { new UniqueReservationPlan("cid", "12345") }
			}));

		var result = await environment.CreateCharacterService(new[] { initializer }).CreateAsync(
			new AccountId(7656125),
			ApplicationServiceTestEnvironment.Request());

		Assert.AreEqual(ErrorCode.Conflict, result.Error!.Code);
		Assert.IsEmpty(environment.Repositories.Characters.All());
		Assert.IsEmpty(environment.Repositories.Inventories.All());
		Assert.IsEmpty(environment.Repositories.Items.All());
		Assert.HasCount(1, environment.Repositories.UniqueReservations.All());
	}

	[TestMethod]
	public async Task UnknownOrIncompatibleStateAndTraitTypesFailBeforeCommit()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(7656126);
		var unknownState = environment.CreateCharacterService(
			stateFactory: new StaticStateFactory(
				ApplicationServiceTestEnvironment.StatePayload(typeId: "unknown.state")));
		var incompatibleState = environment.CreateCharacterService(
			stateFactory: new StaticStateFactory(
				ApplicationServiceTestEnvironment.StatePayload(version: 2)));
		var unknownTrait = TraitInitializer(
			ApplicationServiceTestEnvironment.StatePayload(typeId: "unknown.trait"));
		var incompatibleTrait = TraitInitializer(
			ApplicationServiceTestEnvironment.StatePayload(version: 2));

		var unknownStateResult = await unknownState.CreateAsync(
			account, ApplicationServiceTestEnvironment.Request());
		var incompatibleStateResult = await incompatibleState.CreateAsync(
			account, ApplicationServiceTestEnvironment.Request());
		var unknownTraitResult = await environment.CreateCharacterService(new[] { unknownTrait }).CreateAsync(
			account, ApplicationServiceTestEnvironment.Request());
		var incompatibleTraitResult = await environment.CreateCharacterService(new[] { incompatibleTrait }).CreateAsync(
			account, ApplicationServiceTestEnvironment.Request());

		foreach (var result in new[]
			{ unknownStateResult, incompatibleStateResult, unknownTraitResult, incompatibleTraitResult })
		{
			Assert.AreEqual(ErrorCode.PersistedTypeInvalid, result.Error!.Code);
		}
		AssertAllCreationCollectionsEmpty(environment);
	}

	private static void SeedCharacter(
		Hexagon.V2.Persistence.IUnitOfWork unitOfWork,
		ApplicationServiceTestEnvironment environment,
		CharacterRecord character)
	{
		unitOfWork.Create(
			environment.Repositories.Characters,
			DomainKeys.Character(character.Id),
			character);
		unitOfWork.Create(
			environment.Repositories.CharacterSlots,
			DomainKeys.CharacterSlot(character.AccountId, character.Slot),
			new CharacterSlotRecord
			{
				AccountId = character.AccountId,
				Slot = character.Slot,
				CharacterId = character.Id
			});
		unitOfWork.Create(
			environment.Repositories.CharacterLifecycleGuards,
			DomainKeys.CharacterLifecycleGuard(character.Id),
			new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 });
	}

	private static void AssertAllCreationCollectionsEmpty(ApplicationServiceTestEnvironment environment)
	{
		Assert.IsEmpty(environment.Repositories.Characters.All());
		Assert.IsEmpty(environment.Repositories.CharacterSlots.All());
		Assert.IsEmpty(environment.Repositories.CharacterLifecycleGuards.All());
		Assert.IsEmpty(environment.Repositories.Inventories.All());
		Assert.IsEmpty(environment.Repositories.OwnerInventories.All());
		Assert.IsEmpty(environment.Repositories.Items.All());
		Assert.IsEmpty(environment.Repositories.UniqueReservations.All());
	}

	private static CharacterReferenceRecord Reference(
		string category,
		CharacterId characterId,
		CharacterId? relatedCharacterId) => new()
	{
		Category = category,
		CharacterId = characterId,
		RelatedCharacterId = relatedCharacterId,
		State = ApplicationServiceTestEnvironment.StatePayload()
	};

	private static TestCharacterInitializer TraitInitializer(TypedPayload payload) => new(
		"trait",
		(_, _) => OperationResult<CharacterInitializerContribution>.Success(
			new CharacterInitializerContribution
			{
				Items = new[]
				{
					new ItemGrantPlan
					{
						Definition = new DefinitionId(ApplicationServiceTestEnvironment.ItemDefinitionId),
						Traits = new Dictionary<string, TypedPayload>(StringComparer.Ordinal)
						{
							["state"] = payload
						}
					}
				}
			}));

	private sealed class StaticStateFactory : ICharacterStateFactory
	{
		private readonly TypedPayload _state;
		public StaticStateFactory(TypedPayload state) => _state = state;

		public OperationResult<CharacterStatePlan> Create(CharacterCreationContext context) =>
			OperationResult<CharacterStatePlan>.Success(new CharacterStatePlan(_state, 0));
	}
}
