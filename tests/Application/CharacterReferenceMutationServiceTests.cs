#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class CharacterReferenceMutationServiceTests
{
	[TestMethod]
	public async Task DirectReferenceWriteWithoutGuardDeltaFailsCommitInvariant()
	{
		await using var fixture = await Fixture.CreateAsync();
		await using var unit = fixture.Provider.BeginUnitOfWork();
		unit.Create( fixture.Repositories.CharacterReferences, "test", fixture.Reference() );

		var result = await unit.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.InvariantViolation, result.Error!.Code );
		Assert.IsNull( fixture.Repositories.CharacterReferences.Find( "test" ) );
		Assert.AreEqual( 0L, fixture.Guard().Value.ReferenceRevision );
	}

	[TestMethod]
	public async Task CentralMutationStagesReferenceAndGuardInOneCommit()
	{
		await using var fixture = await Fixture.CreateAsync();
		var service = new CharacterReferenceMutationService( fixture.Repositories );

		var result = await service.UpsertAsync( "test", fixture.Reference() );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.IsNotNull( fixture.Repositories.CharacterReferences.Find( "test" ) );
		Assert.AreEqual( 1L, fixture.Guard().Value.ReferenceRevision );
	}

	[TestMethod]
	public async Task ConcurrentReferenceCreationMakesPreparedCharacterDeleteConflict()
	{
		await using var fixture = await Fixture.CreateAsync();
		var staleDelete = fixture.Provider.BeginUnitOfWork();
		staleDelete.Delete( fixture.Repositories.Characters, fixture.CharacterDocument() );
		staleDelete.Delete( fixture.Repositories.CharacterSlots, fixture.Slot() );
		staleDelete.Delete( fixture.Repositories.CharacterLifecycleGuards, fixture.Guard() );
		staleDelete.Delete( fixture.Repositories.Inventories, fixture.Inventory() );
		staleDelete.Delete( fixture.Repositories.OwnerInventories, fixture.OwnerIndex() );

		var referenceService = new CharacterReferenceMutationService( fixture.Repositories );
		Assert.IsTrue( (await referenceService.UpsertAsync( "test", fixture.Reference() )).Succeeded );
		var deletion = await staleDelete.CommitAsync();
		await staleDelete.DisposeAsync();

		Assert.IsFalse( deletion.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, deletion.Error!.Code );
		Assert.IsNotNull( fixture.CharacterDocument() );
		Assert.IsNotNull( fixture.Repositories.CharacterReferences.Find( "test" ) );
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private Fixture(
			InMemoryPersistenceProvider provider,
			DomainRepositories repositories,
			CharacterRecord character,
			InventoryRecord inventory )
		{
			Provider = provider;
			Repositories = repositories;
			Character = character;
			InventoryRecord = inventory;
		}

		public InMemoryPersistenceProvider Provider { get; }
		public DomainRepositories Repositories { get; }
		public CharacterRecord Character { get; }
		public InventoryRecord InventoryRecord { get; }

		public static async Task<Fixture> CreateAsync()
		{
			await using var schemaEnvironment = await ApplicationServiceTestEnvironment.CreateAsync();
			var schema = schemaEnvironment.Schema;
			var profile = new SchemaPersistenceInvariantProfile(
				new PersistedTypeId( ApplicationServiceTestEnvironment.StateTypeId ),
				new[]
				{
					ItemPersistenceContract.WithoutTraits( ApplicationServiceTestEnvironment.ItemDefinitionId ),
					ItemPersistenceContract.WithoutTraits( ApplicationServiceTestEnvironment.BagDefinitionId )
				},
				new[]
				{
					new CharacterReferencePersistenceContract(
						"test", new PersistedTypeId( ApplicationServiceTestEnvironment.StateTypeId ) )
				},
				Array.Empty<KeyValuePair<string, PersistedTypeId>>() );
			var types = new PersistedTypeRegistry()
				.RegisterHexagonDomainTypes()
				.Register<TestCharacterState>(
					new PersistedTypeKey( ApplicationServiceTestEnvironment.StateTypeId ), 1,
					PersistedValuePublication.Immutable );
			var invariants = new DomainInvariantValidator(
				types, schema, new SchemaItemShapeCatalog( schema ), profile );
			var provider = new InMemoryPersistenceProvider( types, invariants );
			await provider.InitializeAsync();
			var repositories = new DomainRepositories( provider );
			var account = new AccountId( 9911 );
			var character = ApplicationServiceTestEnvironment.Character( account, 0 );
			var inventory = ApplicationServiceTestEnvironment.Inventory( InventoryOwner.Character( character.Id ) );
			await using var unit = provider.BeginUnitOfWork();
			unit.Create( repositories.Characters, DomainKeys.Character( character.Id ), character );
			unit.Create( repositories.CharacterSlots, DomainKeys.CharacterSlot( account, 0 ),
				new CharacterSlotRecord { AccountId = account, Slot = 0, CharacterId = character.Id } );
			unit.Create( repositories.CharacterLifecycleGuards, DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			unit.Create( repositories.Inventories, DomainKeys.Inventory( inventory.Id ), inventory );
			unit.Create( repositories.OwnerInventories, DomainKeys.OwnerInventory( inventory.Owner, "main" ),
				new OwnerInventoryRecord { Owner = inventory.Owner, Role = "main", InventoryId = inventory.Id } );
			var seeded = await unit.CommitAsync();
			if ( !seeded.Succeeded )
			{
				await provider.DisposeAsync();
				throw new InvalidOperationException( seeded.Error!.Message );
			}
			return new Fixture( provider, repositories, character, inventory );
		}

		public CharacterReferenceRecord Reference() => new()
		{
			Category = "test",
			CharacterId = Character.Id,
			State = ApplicationServiceTestEnvironment.StatePayload()
		};

		public DocumentSnapshot<CharacterRecord> CharacterDocument() =>
			Repositories.Characters.Find( DomainKeys.Character( Character.Id ) )!;
		public DocumentSnapshot<CharacterSlotRecord> Slot() =>
			Repositories.CharacterSlots.Find( DomainKeys.CharacterSlot( Character.AccountId, Character.Slot ) )!;
		public DocumentSnapshot<CharacterLifecycleGuardRecord> Guard() =>
			Repositories.CharacterLifecycleGuards.Find( DomainKeys.CharacterLifecycleGuard( Character.Id ) )!;
		public DocumentSnapshot<InventoryRecord> Inventory() =>
			Repositories.Inventories.Find( DomainKeys.Inventory( InventoryRecord.Id ) )!;
		public DocumentSnapshot<OwnerInventoryRecord> OwnerIndex() =>
			Repositories.OwnerInventories.Find( DomainKeys.OwnerInventory( InventoryRecord.Owner, "main" ) )!;

		public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
	}
}
