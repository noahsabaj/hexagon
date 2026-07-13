#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class InventoryMutationServiceTests
{
	[TestMethod]
	public async Task MoveCommitsBothInventorySnapshots()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var source = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var target = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(CharacterId.New()));
		await SeedAsync(environment, item, source, target);
		GrantTransfer(environment, actor, source.Id, target.Id);

		var result = await environment.CreateInventoryMutationService().MoveAsync(
			actor, source.Id, target.Id, item.Id, 2, 1);

		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		Assert.IsNull(Find(environment, source.Id).Find(item.Id));
		Assert.AreEqual(
			new InventoryPlacement(item.Id, 2, 1),
			Find(environment, target.Id).Find(item.Id));
	}

	[TestMethod]
	public async Task ForgedSourceIsRejectedWithoutMovingTheGloballyKnownItem()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var actualSource = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var forgedSource = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId));
		var target = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(CharacterId.New()));
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			foreach (var inventory in new[] { actualSource, forgedSource, target })
				unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
		});
		GrantTransfer(environment, actor, forgedSource.Id, target.Id);

		var result = await environment.CreateInventoryMutationService().MoveAsync(
			actor, forgedSource.Id, target.Id, item.Id, 1, 1);

		Assert.AreEqual(ErrorCode.NotFound, result.Error!.Code);
		Assert.IsNotNull(Find(environment, actualSource.Id).Find(item.Id));
		Assert.IsEmpty(Find(environment, target.Id).Placements);
	}

	[TestMethod]
	public async Task MissingTransferCapabilityIsRejectedAndLeavesBothSidesUnchanged()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var source = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var target = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(CharacterId.New()));
		await SeedAsync(environment, item, source, target);
		environment.Grant(actor, source.Id, InventoryCapability.Move);
		environment.Grant(actor, target.Id, InventoryCapability.TransferIn);

		var result = await environment.CreateInventoryMutationService().MoveAsync(
			actor, source.Id, target.Id, item.Id, 1, 1);

		Assert.AreEqual(ErrorCode.Unauthorized, result.Error!.Code);
		Assert.IsNotNull(Find(environment, source.Id).Find(item.Id));
		Assert.IsEmpty(Find(environment, target.Id).Placements);
	}

	[TestMethod]
	public async Task LegitimateConnectionGrantCannotBeReusedByAForgedCharacter()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var legitimateActor = ApplicationServiceTestEnvironment.Actor();
		var forgedActor = legitimateActor with { CharacterId = CharacterId.New() };
		var item = ApplicationServiceTestEnvironment.Item();
		var source = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(legitimateActor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var target = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(CharacterId.New()));
		await SeedAsync(environment, item, source, target);
		GrantTransfer(environment, legitimateActor, source.Id, target.Id);

		var result = await environment.CreateInventoryMutationService().MoveAsync(
			forgedActor, source.Id, target.Id, item.Id, 1, 1);

		Assert.AreEqual(ErrorCode.Unauthorized, result.Error!.Code);
		Assert.IsNotNull(Find(environment, source.Id).Find(item.Id));
		Assert.IsEmpty(Find(environment, target.Id).Placements);
	}

	[TestMethod]
	public async Task BagsCannotMoveIntoThemselvesOrDescendants()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var selfBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var selfSource = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(selfBag.Id, 0, 0) });
		var selfTarget = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.ParentItem(selfBag.Id));

		var outerBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var innerBag = ApplicationServiceTestEnvironment.Item(ApplicationServiceTestEnvironment.BagDefinitionId);
		var descendantSource = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(outerBag.Id, 0, 0) });
		var outerInventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.ParentItem(outerBag.Id),
			new[] { new InventoryPlacement(innerBag.Id, 0, 0) });
		var descendantTarget = ApplicationServiceTestEnvironment.Inventory(InventoryOwner.ParentItem(innerBag.Id));

		await environment.SeedAsync(unitOfWork =>
		{
			foreach (var item in new[] { selfBag, outerBag, innerBag })
				unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			foreach (var inventory in new[]
				{ selfSource, selfTarget, descendantSource, outerInventory, descendantTarget })
				unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
		});
		GrantTransfer(environment, actor, selfSource.Id, selfTarget.Id);
		GrantTransfer(environment, actor, descendantSource.Id, descendantTarget.Id);
		var service = environment.CreateInventoryMutationService();

		var selfResult = await service.MoveAsync(
			actor, selfSource.Id, selfTarget.Id, selfBag.Id, 0, 0);
		var descendantResult = await service.MoveAsync(
			actor, descendantSource.Id, descendantTarget.Id, outerBag.Id, 0, 0);

		Assert.AreEqual(ErrorCode.InvalidArgument, selfResult.Error!.Code);
		Assert.AreEqual(ErrorCode.InvalidArgument, descendantResult.Error!.Code);
		Assert.IsNotNull(Find(environment, selfSource.Id).Find(selfBag.Id));
		Assert.IsNotNull(Find(environment, descendantSource.Id).Find(outerBag.Id));
		Assert.IsEmpty(Find(environment, selfTarget.Id).Placements);
		Assert.IsEmpty(Find(environment, descendantTarget.Id).Placements);
	}

	[TestMethod]
	public async Task InjectedCommitFailureLeavesBothInventoriesUnchanged()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var source = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		var target = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(CharacterId.New()));
		await SeedAsync(environment, item, source, target);
		GrantTransfer(environment, actor, source.Id, target.Id);
		environment.Provider.FailNextCommit();

		var result = await environment.CreateInventoryMutationService().MoveAsync(
			actor, source.Id, target.Id, item.Id, 1, 1);

		Assert.AreEqual(ErrorCode.InternalError, result.Error!.Code);
		Assert.IsNotNull(Find(environment, source.Id).Find(item.Id));
		Assert.IsEmpty(Find(environment, target.Id).Placements);
	}

	private static async Task SeedAsync(
		ApplicationServiceTestEnvironment environment,
		ItemRecord item,
		InventoryRecord source,
		InventoryRecord target)
	{
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(source.Id), source);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(target.Id), target);
		});
	}

	private static void GrantTransfer(
		ApplicationServiceTestEnvironment environment,
		InventoryActor actor,
		InventoryId source,
		InventoryId target)
	{
		environment.Grant(actor, source, InventoryCapability.Move | InventoryCapability.TransferOut);
		environment.Grant(actor, target, InventoryCapability.TransferIn);
	}

	private static InventoryRecord Find(
		ApplicationServiceTestEnvironment environment,
		InventoryId id) => environment.Repositories.Inventories.Find(DomainKeys.Inventory(id))!.Value;
}
