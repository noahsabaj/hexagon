#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class SceneAndAggregateMutationTests
{
	[TestMethod]
	public async Task SceneStateUsesRegisteredTypesAndPublishesOnlyCommittedReplacement()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var service = new SceneEntityStateService(
			environment.Repositories,
			environment.Schema,
			ApplicationServiceTestEnvironment.AllowPolicy<SceneEntityStateMutationContext>());
		var sceneEntityId = SceneEntityId.New();
		var actor = new AccountId(9101);
		var initial = ApplicationServiceTestEnvironment.StatePayload("closed");

		var ensured = await service.EnsureAsync(sceneEntityId, "door", initial);
		var replaced = await service.ReplaceStateAsync(
			actor, null, sceneEntityId, ApplicationServiceTestEnvironment.StatePayload("open"));
		var incompatible = await service.ReplaceStateAsync(
			actor, null, sceneEntityId, ApplicationServiceTestEnvironment.StatePayload("future", version: 2));
		environment.Provider.FailNextCommit();
		var failedCommit = await service.ReplaceStateAsync(
			actor, null, sceneEntityId, ApplicationServiceTestEnvironment.StatePayload("unpublished"));

		Assert.IsTrue(ensured.Succeeded, ensured.Error?.Message);
		Assert.IsTrue(replaced.Succeeded, replaced.Error?.Message);
		Assert.AreEqual(ErrorCode.PersistedTypeInvalid, incompatible.Error!.Code);
		Assert.AreEqual(ErrorCode.InternalError, failedCommit.Error!.Code);
		var stored = service.Find(sceneEntityId)!;
		Assert.AreEqual("open", stored.State.Data.GetProperty("name").GetString());
		Assert.AreEqual(ApplicationServiceTestEnvironment.StateTypeId, stored.State.TypeId.Value);
		Assert.AreEqual(1, stored.State.TypeVersion);
	}

	[TestMethod]
	public async Task TouchLastPlayedReturnsOnlyProviderIssuedDurableReceipt()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId( 9103 );
		var character = ApplicationServiceTestEnvironment.Character( account, 0 ) with
		{
			LastPlayedAt = DateTimeOffset.UnixEpoch
		};
		await environment.SeedAsync( unitOfWork =>
			unitOfWork.Create( environment.Repositories.Characters, DomainKeys.Character( character.Id ), character ) );
		var service = new AggregateMutationService(
			environment.Repositories,
			environment.Schema,
			ApplicationServiceTestEnvironment.AllowPolicy<CharacterMutationContext>(),
			ApplicationServiceTestEnvironment.AllowPolicy<ItemTraitMutationContext>() );

		var timestamp = DateTimeOffset.UnixEpoch.AddMinutes( 1 );
		var touched = await service.TouchLastPlayedAsync( account, character.Id, timestamp );

		Assert.IsTrue( touched.Succeeded, touched.Error?.Message );
		Assert.AreEqual( CharacterMutationKind.TouchLastPlayed, touched.Value.Kind );
		Assert.AreEqual( character.LastPlayedAt, touched.Value.Before.LastPlayedAt );
		Assert.AreEqual( timestamp, touched.Value.After.LastPlayedAt );
		Assert.IsGreaterThan( 0L, touched.Value.Commit.Sequence );
		Assert.IsTrue( touched.Value.Commit.Documents.Any( document =>
			document.Address.Collection == DomainCollections.Characters &&
			document.Address.Key == DomainKeys.Character( character.Id ) ) );

		environment.Provider.FailNextCommit();
		var failed = await service.TouchLastPlayedAsync(
			account, character.Id, timestamp.AddMinutes( 1 ) );

		Assert.IsTrue( failed.Failed );
		Assert.AreEqual( ErrorCode.InternalError, failed.Error!.Code );
		Assert.AreEqual( timestamp, environment.Repositories.Characters.Find(
			DomainKeys.Character( character.Id ) )!.Value.LastPlayedAt );
	}

	[TestMethod]
	public async Task PreparedTouchStagesIntoCombinedCommitAndPublishesOnlyOnMatchingCompletion()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId( 9104 );
		var character = ApplicationServiceTestEnvironment.Character( account, 0 ) with
		{
			LastPlayedAt = DateTimeOffset.UnixEpoch
		};
		var item = ApplicationServiceTestEnvironment.Item();
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create(
				environment.Repositories.Characters, DomainKeys.Character( character.Id ), character );
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
		} );
		var events = new RecordingCharacterChangedHandler();
		var service = new AggregateMutationService(
			environment.Repositories,
			environment.Schema,
			ApplicationServiceTestEnvironment.AllowPolicy<CharacterMutationContext>(),
			ApplicationServiceTestEnvironment.AllowPolicy<ItemTraitMutationContext>(),
			new PostCommitEventBus<CharacterChangedEvent>( new[]
			{
				new EventHandlerRegistration<CharacterChangedEvent>( "record", events )
			} ) );

		var timestamp = DateTimeOffset.UnixEpoch.AddMinutes( 1 );
		var prepared = service.PrepareTouchLastPlayed( account, character.Id, timestamp );
		Assert.IsTrue( prepared.Succeeded, prepared.Error?.Message );
		var unitOfWork = environment.Repositories.Provider.BeginUnitOfWork();
		Assert.IsTrue( service.StageTouchLastPlayed( unitOfWork, prepared.Value ).Succeeded );
		var itemDocument = environment.Repositories.Items.Find( DomainKeys.Item( item.Id ) )!;
		var itemEditor = unitOfWork.Edit( environment.Repositories.Items, itemDocument )!;
		itemEditor.Replace( itemEditor.Value with { Revision = 1 } );
		unitOfWork.Save( itemEditor );
		var committed = await unitOfWork.CommitAsync();
		await unitOfWork.DisposeAsync();
		Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
		Assert.HasCount( 2, committed.Value!.Documents );
		Assert.IsEmpty( events.Events );

		var premature = service.CompleteTouchLastPlayed(
			prepared.Value, new CommitReceipt( committed.Value.Sequence, Array.Empty<CommittedDocumentVersion>() ) );
		Assert.AreEqual( ErrorCode.InvariantViolation, premature.Error!.Code );
		Assert.IsEmpty( events.Events );

		var completed = service.CompleteTouchLastPlayed( prepared.Value, committed.Value );
		Assert.IsTrue( completed.Succeeded, completed.Error?.Message );
		Assert.AreSame( committed.Value, completed.Value.Commit );
		Assert.AreEqual( timestamp, completed.Value.After.LastPlayedAt );
		Assert.HasCount( 1, events.Events );
		Assert.AreEqual( committed.Value.Sequence, events.Events[0].CommitSequence );
	}

	[TestMethod]
	public async Task PreparedTouchCommitFailureAndRevisionConflictEmitNoEvent()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId( 9105 );
		var character = ApplicationServiceTestEnvironment.Character( account, 0 ) with
		{
			LastPlayedAt = DateTimeOffset.UnixEpoch
		};
		await environment.SeedAsync( unitOfWork =>
			unitOfWork.Create(
				environment.Repositories.Characters, DomainKeys.Character( character.Id ), character ) );
		var events = new RecordingCharacterChangedHandler();
		var service = new AggregateMutationService(
			environment.Repositories,
			environment.Schema,
			ApplicationServiceTestEnvironment.AllowPolicy<CharacterMutationContext>(),
			ApplicationServiceTestEnvironment.AllowPolicy<ItemTraitMutationContext>(),
			new PostCommitEventBus<CharacterChangedEvent>( new[]
			{
				new EventHandlerRegistration<CharacterChangedEvent>( "record", events )
			} ) );

		var failedPlan = service.PrepareTouchLastPlayed(
			account, character.Id, DateTimeOffset.UnixEpoch.AddMinutes( 1 ) );
		var failedUnit = environment.Repositories.Provider.BeginUnitOfWork();
		Assert.IsTrue( service.StageTouchLastPlayed( failedUnit, failedPlan.Value ).Succeeded );
		environment.Provider.FailNextCommit();
		var failedCommit = await failedUnit.CommitAsync();
		await failedUnit.DisposeAsync();
		Assert.IsFalse( failedCommit.Succeeded );
		Assert.IsEmpty( events.Events );
		Assert.AreEqual( DateTimeOffset.UnixEpoch, environment.Repositories.Characters.Find(
			DomainKeys.Character( character.Id ) )!.Value.LastPlayedAt );

		var conflictedPlan = service.PrepareTouchLastPlayed(
			account, character.Id, DateTimeOffset.UnixEpoch.AddMinutes( 2 ) );
		var staleUnit = environment.Repositories.Provider.BeginUnitOfWork();
		Assert.IsTrue( service.StageTouchLastPlayed( staleUnit, conflictedPlan.Value ).Succeeded );
		var current = environment.Repositories.Characters.Find( DomainKeys.Character( character.Id ) )!;
		var competingUnit = environment.Repositories.Provider.BeginUnitOfWork();
		var competingEditor = competingUnit.Edit( environment.Repositories.Characters, current )!;
		competingEditor.Replace( competingEditor.Value with
		{
			LastPlayedAt = DateTimeOffset.UnixEpoch.AddMinutes( 3 )
		} );
		competingUnit.Save( competingEditor );
		var competingCommit = await competingUnit.CommitAsync();
		await competingUnit.DisposeAsync();
		Assert.IsTrue( competingCommit.Succeeded, competingCommit.Error?.Message );
		var conflict = await staleUnit.CommitAsync();
		await staleUnit.DisposeAsync();
		Assert.IsFalse( conflict.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, conflict.Error!.Code );
		Assert.IsEmpty( events.Events );

		var wrongCompletion = service.CompleteTouchLastPlayed(
			conflictedPlan.Value, competingCommit.Value! );
		Assert.AreEqual( ErrorCode.InvariantViolation, wrongCompletion.Error!.Code );
		Assert.IsEmpty( events.Events );
	}

	[TestMethod]
	public async Task AggregateMutationFailuresLeaveTimestampBanBalanceStateAndTraitsUntouched()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var account = new AccountId(9102);
		var character = ApplicationServiceTestEnvironment.Character(account, 0) with
		{
			Balance = 10,
			LastPlayedAt = DateTimeOffset.UnixEpoch.AddHours(2)
		};
		var item = ApplicationServiceTestEnvironment.Item() with
		{
			Traits = new Dictionary<string, TypedPayload>(StringComparer.Ordinal)
			{
				["state"] = ApplicationServiceTestEnvironment.StatePayload("old")
			}
		};
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Characters, DomainKeys.Character(character.Id), character);
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
		});
		var service = new AggregateMutationService(
			environment.Repositories,
			environment.Schema,
			ApplicationServiceTestEnvironment.AllowPolicy<CharacterMutationContext>(),
			ApplicationServiceTestEnvironment.AllowPolicy<ItemTraitMutationContext>());

		var oldTimestamp = await service.TouchLastPlayedAsync(
			account, character.Id, DateTimeOffset.UnixEpoch);
		var nonUtcBan = await service.SetBanAsync(
			account,
			character.Id,
			true,
			new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)));
		var insufficientFunds = await service.DebitAsync(account, character.Id, 11);
		var unknownState = await service.ReplaceSchemaStateAsync(
			account,
			character.Id,
			ApplicationServiceTestEnvironment.StatePayload(typeId: "unknown.state"));
		var unknownTrait = await service.ReplaceTraitAsync(
			account,
			character.Id,
			item.Id,
			"state",
			ApplicationServiceTestEnvironment.StatePayload(typeId: "unknown.trait"));

		environment.Provider.FailNextCommit();
		var failedCredit = await service.CreditAsync(account, character.Id, 1);
		environment.Provider.FailNextCommit();
		var failedTraitCommit = await service.ReplaceTraitAsync(
			account,
			character.Id,
			item.Id,
			"state",
			ApplicationServiceTestEnvironment.StatePayload("new"));

		Assert.AreEqual(ErrorCode.InvalidArgument, oldTimestamp.Error!.Code);
		Assert.AreEqual(ErrorCode.InvalidArgument, nonUtcBan.Error!.Code);
		Assert.AreEqual(ErrorCode.Conflict, insufficientFunds.Error!.Code);
		Assert.AreEqual(ErrorCode.PersistedTypeInvalid, unknownState.Error!.Code);
		Assert.AreEqual(ErrorCode.PersistedTypeInvalid, unknownTrait.Error!.Code);
		Assert.AreEqual(ErrorCode.InternalError, failedCredit.Error!.Code);
		Assert.AreEqual(ErrorCode.InternalError, failedTraitCommit.Error!.Code);

		var storedCharacter = environment.Repositories.Characters.Find(DomainKeys.Character(character.Id))!.Value;
		var storedItem = environment.Repositories.Items.Find(DomainKeys.Item(item.Id))!.Value;
		Assert.AreEqual(character.LastPlayedAt, storedCharacter.LastPlayedAt);
		Assert.IsFalse(storedCharacter.IsBanned);
		Assert.IsNull(storedCharacter.BanExpiresAt);
		Assert.AreEqual(10L, storedCharacter.Balance);
		Assert.AreEqual("citizen", storedCharacter.SchemaState.Data.GetProperty("name").GetString());
		Assert.AreEqual("old", storedItem.Traits["state"].Data.GetProperty("name").GetString());
	}

	private sealed class RecordingCharacterChangedHandler : IEventHandler<CharacterChangedEvent>
	{
		public List<CharacterChangedEvent> Events { get; } = new();
		public void Handle( CharacterChangedEvent @event ) => Events.Add( @event );
	}
}
