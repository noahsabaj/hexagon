namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for bag/container items. When used, creates a nested inventory
/// that the player can store additional items in.
/// </summary>
[AssetType( Name = "Bag Item", Extension = "bag" )]
public class BagItemDef : ItemDefinition
{
	/// <summary>
	/// Width of the bag's internal inventory grid.
	/// </summary>
	[Property] public int BagWidth { get; set; } = 3;

	/// <summary>
	/// Height of the bag's internal inventory grid.
	/// </summary>
	[Property] public int BagHeight { get; set; } = 3;

	public override Dictionary<string, ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions["open"] = new ItemAction
		{
			Name = "Open",
			Icon = "inventory_2",
			OnRun = ( player, item ) =>
			{
				OpenBag( player, item );
				return false;
			},
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		};

		return actions;
	}

	/// <summary>
	/// Get or create the nested inventory for this bag instance.
	/// </summary>
	public Inventory.HexInventory GetBagInventory( ItemInstance item )
	{
		var invId = item.GetData<string>( "bagInventoryId" );

		if ( !string.IsNullOrEmpty( invId ) )
		{
			var existing = Inventory.InventoryManager.Get( invId );
			if ( existing != null )
				return existing;
		}

		// Create the bag's inventory
		var inv = Inventory.InventoryManager.Create( BagWidth, BagHeight, type: "bag" );
		item.SetData( "bagInventoryId", inv.Id );
		return inv;
	}

	/// <summary>
	/// Open this bag for a player (adds them as a receiver of the bag's inventory).
	/// </summary>
	public void OpenBag( HexPlayerComponent player, ItemInstance item )
	{
		var inv = GetBagInventory( item );
		inv.AddReceiver( player.Connection );
		Inventory.InventoryManager.MarkDirty( inv.Id );
	}

	/// <summary>
	/// Close this bag for a player.
	/// </summary>
	public void CloseBag( HexPlayerComponent player, ItemInstance item )
	{
		var invId = item.GetData<string>( "bagInventoryId" );
		if ( string.IsNullOrEmpty( invId ) ) return;

		var inv = Inventory.InventoryManager.Get( invId );
		if ( inv == null ) return;

		inv.RemoveReceiver( player.Connection );
	}

	public override void OnRemoved( ItemInstance item )
	{
		base.OnRemoved( item );

		// Clean up the bag's inventory when the bag is destroyed
		var invId = item.GetData<string>( "bagInventoryId" );
		if ( !string.IsNullOrEmpty( invId ) )
			Inventory.InventoryManager.Delete( invId );
	}
}
