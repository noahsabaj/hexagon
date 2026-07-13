#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

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
		Assert.AreEqual("open", stored.State.Data.GetProperty("Name").GetString());
		Assert.AreEqual(ApplicationServiceTestEnvironment.StateTypeId, stored.State.TypeId.Value);
		Assert.AreEqual(1, stored.State.TypeVersion);
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
		Assert.AreEqual("citizen", storedCharacter.SchemaState.Data.GetProperty("Name").GetString());
		Assert.AreEqual("old", storedItem.Traits["state"].Data.GetProperty("Name").GetString());
	}
}
