#nullable enable

using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class WorldItemServiceTests
{
	[TestMethod]
	public async Task DropAndPickupMaintainExactlyOneItemLocation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		await SeedAsync(environment, item, inventory);
		environment.Grant(
			actor,
			inventory.Id,
			InventoryCapability.Move | InventoryCapability.Drop | InventoryCapability.TransferIn);
		var service = environment.CreateWorldItemService();

		var dropped = await service.DropAsync(
			actor, inventory.Id, item.Id, ApplicationServiceTestEnvironment.Transform());

		Assert.IsTrue(dropped.Succeeded, dropped.Error?.Message);
		Assert.IsEmpty(Find(environment, inventory.Id).Placements);
		Assert.HasCount(1, service.LoadWorldItems());
		Assert.AreEqual(item.Id, service.LoadWorldItems()[0].ItemId);

		var pickedUp = await service.PickUpAsync(actor, item.Id, inventory.Id);
		var duplicatePickup = await service.PickUpAsync(actor, item.Id, inventory.Id);

		Assert.IsTrue(pickedUp.Succeeded, pickedUp.Error?.Message);
		Assert.AreEqual(ErrorCode.NotFound, duplicatePickup.Error!.Code);
		Assert.IsEmpty(service.LoadWorldItems());
		Assert.HasCount(1, Find(environment, inventory.Id).Placements);
		Assert.AreEqual(item.Id, Find(environment, inventory.Id).Placements[0].ItemId);
	}

	[TestMethod]
	public async Task InjectedDropCommitFailureLeavesInventoryAndWorldViewUnchanged()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		await SeedAsync(environment, item, inventory);
		environment.Grant(
			actor,
			inventory.Id,
			InventoryCapability.Move | InventoryCapability.Drop);
		environment.Provider.FailNextCommit();

		var result = await environment.CreateWorldItemService().DropAsync(
			actor, inventory.Id, item.Id, ApplicationServiceTestEnvironment.Transform());

		Assert.AreEqual(ErrorCode.InternalError, result.Error!.Code);
		Assert.IsNotNull(Find(environment, inventory.Id).Find(item.Id));
		Assert.IsEmpty(environment.Repositories.WorldItems.All());
	}

	[TestMethod]
	public async Task InvalidModelAndMissingCapabilityRejectDropWithoutRemoval()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character(actor.CharacterId),
			new[] { new InventoryPlacement(item.Id, 0, 0) });
		await SeedAsync(environment, item, inventory);
		var service = environment.CreateWorldItemService(modelIsValid: false);

		var unauthorized = await service.DropAsync(
			actor, inventory.Id, item.Id, ApplicationServiceTestEnvironment.Transform());
		environment.Grant(
			actor,
			inventory.Id,
			InventoryCapability.Move | InventoryCapability.Drop);
		var invalidModel = await service.DropAsync(
			actor, inventory.Id, item.Id, ApplicationServiceTestEnvironment.Transform());

		Assert.AreEqual(ErrorCode.Unauthorized, unauthorized.Error!.Code);
		Assert.AreEqual(ErrorCode.InvalidArgument, invalidModel.Error!.Code);
		Assert.IsNotNull(Find(environment, inventory.Id).Find(item.Id));
		Assert.IsEmpty(environment.Repositories.WorldItems.All());
	}

	private static async Task SeedAsync(
		ApplicationServiceTestEnvironment environment,
		ItemRecord item,
		InventoryRecord inventory)
	{
		await environment.SeedAsync(unitOfWork =>
		{
			unitOfWork.Create(environment.Repositories.Items, DomainKeys.Item(item.Id), item);
			unitOfWork.Create(environment.Repositories.Inventories, DomainKeys.Inventory(inventory.Id), inventory);
		});
	}

	private static InventoryRecord Find(
		ApplicationServiceTestEnvironment environment,
		InventoryId id) => environment.Repositories.Inventories.Find(DomainKeys.Inventory(id))!.Value;
}
