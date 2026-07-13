using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class InventoryLayoutServiceTests
{
	[TestMethod]
	public void TransferRequiresSourceMembership()
	{
		var catalog = new FakeCatalog();
		var item = CreateItem();
		catalog.Add( item, new ItemShape( 1, 1 ) );
		var service = new InventoryLayoutService( catalog );

		var result = service.Transfer( EmptyInventory(), EmptyInventory(), item, 0, 0 );

		Assert.IsTrue( result.Failed );
	}

	[TestMethod]
	public void FailedTargetPlacementLeavesSourceUnchanged()
	{
		var catalog = new FakeCatalog();
		var item = CreateItem();
		var blocker = CreateItem();
		catalog.Add( item, new ItemShape( 1, 1 ) );
		catalog.Add( blocker, new ItemShape( 1, 1 ) );
		var service = new InventoryLayoutService( catalog );
		var source = EmptyInventory() with { Placements = [new InventoryPlacement( item.Id, 0, 0 )] };
		var target = EmptyInventory() with { Placements = [new InventoryPlacement( blocker.Id, 0, 0 )] };

		var result = service.Transfer( source, target, item, 0, 0 );

		Assert.IsTrue( result.Failed );
		Assert.IsNotNull( source.Find( item.Id ) );
	}

	[TestMethod]
	public void SuccessfulTransferChangesBothSnapshots()
	{
		var catalog = new FakeCatalog();
		var item = CreateItem();
		catalog.Add( item, new ItemShape( 1, 1 ) );
		var service = new InventoryLayoutService( catalog );
		var source = EmptyInventory() with { Placements = [new InventoryPlacement( item.Id, 0, 0 )] };
		var target = EmptyInventory();

		var result = service.Transfer( source, target, item, 1, 2 );

		Assert.IsTrue( result.Succeeded );
		Assert.IsNull( result.Value.Source.Find( item.Id ) );
		Assert.AreEqual( new InventoryPlacement( item.Id, 1, 2 ), result.Value.Target.Find( item.Id ) );
	}

	private static ItemRecord CreateItem() => new()
	{
		Id = ItemId.New(),
		Definition = new DefinitionId( "test.item" )
	};

	private static InventoryRecord EmptyInventory() => new()
	{
		Id = InventoryId.New(),
		Owner = InventoryOwner.Character( CharacterId.New() ),
		Width = 4,
		Height = 4
	};

	private sealed class FakeCatalog : IItemShapeCatalog
	{
		private readonly Dictionary<DefinitionId, ItemShape> _definitions = new();
		private readonly Dictionary<ItemId, ItemShape> _items = new();

		public void Add( ItemRecord item, ItemShape shape )
		{
			_definitions[item.Definition] = shape;
			_items[item.Id] = shape;
		}

		public bool TryGetShape( DefinitionId definitionId, out ItemShape shape ) =>
			_definitions.TryGetValue( definitionId, out shape );

		public bool TryGetShape( ItemId itemId, out ItemShape shape ) =>
			_items.TryGetValue( itemId, out shape );
	}
}
