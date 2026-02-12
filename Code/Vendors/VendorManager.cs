namespace Hexagon.Vendors;

/// <summary>
/// Manages vendor registration, persistence, and buy/sell operations.
/// VendorComponents register/unregister themselves on enable/disable.
/// </summary>
public static class VendorManager
{
	private static readonly Dictionary<string, VendorComponent> _vendors = new();

	internal static void Initialize()
	{
		Log.Info( "Hexagon: VendorManager initialized." );
	}

	/// <summary>
	/// Register a vendor component. Called by VendorComponent.OnEnabled.
	/// </summary>
	internal static void Register( VendorComponent vendor )
	{
		if ( string.IsNullOrEmpty( vendor.VendorId ) ) return;
		_vendors[vendor.VendorId] = vendor;
	}

	/// <summary>
	/// Unregister a vendor component. Called by VendorComponent.OnDisabled.
	/// </summary>
	internal static void Unregister( VendorComponent vendor )
	{
		if ( string.IsNullOrEmpty( vendor.VendorId ) ) return;
		_vendors.Remove( vendor.VendorId );
	}

	/// <summary>
	/// Get a vendor component by its ID.
	/// </summary>
	public static VendorComponent GetVendor( string vendorId )
	{
		return _vendors.GetValueOrDefault( vendorId );
	}

	/// <summary>
	/// Get all registered vendors.
	/// </summary>
	public static IReadOnlyDictionary<string, VendorComponent> GetAllVendors() => _vendors;

	// --- Buy/Sell Operations ---

	/// <summary>
	/// Attempt to buy an item from a vendor for a player.
	/// Flow: validate > ICanBuyItemListener > CanAfford > FindEmptySlot > TakeMoney > CreateInstance > AddAt > IItemBoughtListener
	/// </summary>
	public static (bool Success, string Message) Buy( HexPlayerComponent player, VendorComponent vendor, string definitionId )
	{
		if ( player?.Character == null )
			return (false, "No active character.");

		var vendorItem = vendor.GetVendorItem( definitionId );
		if ( vendorItem == null )
			return (false, "Item not found in this vendor.");

		if ( vendorItem.BuyPrice <= 0 )
			return (false, "This item is not for sale.");

		var definition = ItemManager.GetDefinition( definitionId );
		if ( definition == null )
			return (false, "Invalid item definition.");

		// Hook: can buy?
		if ( !HexEvents.CanAll<ICanBuyItemListener>(
			x => x.CanBuyItem( player, vendor, vendorItem ) ) )
			return (false, "You are not allowed to buy this item.");

		// Check money
		if ( !CurrencyManager.CanAfford( player.Character, vendorItem.BuyPrice ) )
			return (false, $"You need {CurrencyManager.Format( vendorItem.BuyPrice )} to buy this.");

		// Find inventory slot
		var inventory = InventoryManager.LoadForCharacter( player.Character.Id )
			.FirstOrDefault( inv => inv.Type == "main" );

		if ( inventory == null )
			return (false, "No inventory found.");

		var slot = inventory.FindEmptySlot( definition.Width, definition.Height );
		if ( slot == null )
			return (false, "Your inventory is full.");

		// Take money
		if ( !CurrencyManager.TakeMoney( player.Character, vendorItem.BuyPrice, "vendor_buy" ) )
			return (false, "Failed to process payment.");

		// Create item instance and add to inventory
		var instance = ItemManager.CreateInstance( definitionId, player.Character.Id );
		if ( instance == null )
		{
			// Refund on failure
			CurrencyManager.GiveMoney( player.Character, vendorItem.BuyPrice, "vendor_buy_refund" );
			return (false, "Failed to create item.");
		}

		inventory.AddAt( instance, slot.Value.x, slot.Value.y );

		// Fire event
		HexEvents.Fire<IItemBoughtListener>(
			x => x.OnItemBought( player, vendor, vendorItem, instance ) );

		HexLog.Add( LogType.Vendor, player,
			$"Bought \"{definition.DisplayName}\" from \"{vendor.VendorName}\" for {CurrencyManager.Format( vendorItem.BuyPrice )}" );

		return (true, $"Purchased {definition.DisplayName} for {CurrencyManager.Format( vendorItem.BuyPrice )}.");
	}

	/// <summary>
	/// Attempt to sell an item to a vendor for a player.
	/// Flow: validate ownership > ICanSellItemListener > Remove > DestroyInstance > GiveMoney > IItemSoldListener
	/// </summary>
	public static (bool Success, string Message) Sell( HexPlayerComponent player, VendorComponent vendor, string itemInstanceId )
	{
		if ( player?.Character == null )
			return (false, "No active character.");

		var instance = ItemManager.GetInstance( itemInstanceId );
		if ( instance == null )
			return (false, "Item not found.");

		// Verify ownership
		if ( instance.CharacterId != player.Character.Id )
			return (false, "You don't own this item.");

		var definition = instance.Definition;
		if ( definition == null )
			return (false, "Invalid item definition.");

		var vendorItem = vendor.GetVendorItem( instance.DefinitionId );
		if ( vendorItem == null || vendorItem.SellPrice <= 0 )
			return (false, "This vendor doesn't buy that item.");

		// Hook: can sell?
		if ( !HexEvents.CanAll<ICanSellItemListener>(
			x => x.CanSellItem( player, vendor, vendorItem, instance ) ) )
			return (false, "You are not allowed to sell this item.");

		// Remove from inventory
		var inventory = InventoryManager.Get( instance.InventoryId );
		inventory?.Remove( instance.Id );

		// Destroy instance
		ItemManager.DestroyInstance( instance.Id );

		// Give money
		CurrencyManager.GiveMoney( player.Character, vendorItem.SellPrice, "vendor_sell" );

		// Fire event
		HexEvents.Fire<IItemSoldListener>(
			x => x.OnItemSold( player, vendor, vendorItem ) );

		HexLog.Add( LogType.Vendor, player,
			$"Sold \"{definition.DisplayName}\" to \"{vendor.VendorName}\" for {CurrencyManager.Format( vendorItem.SellPrice )}" );

		return (true, $"Sold {definition.DisplayName} for {CurrencyManager.Format( vendorItem.SellPrice )}.");
	}

	// --- Persistence ---

	/// <summary>
	/// Save vendor data to the database.
	/// </summary>
	public static void SaveVendor( VendorData data )
	{
		Persistence.DatabaseManager.Save( "vendors", data.VendorId, data );
	}

	/// <summary>
	/// Load vendor data from the database.
	/// </summary>
	public static VendorData LoadVendor( string vendorId )
	{
		return Persistence.DatabaseManager.Load<VendorData>( "vendors", vendorId );
	}
}

/// <summary>
/// Permission hook: can a player buy this item? Return false to block.
/// </summary>
public interface ICanBuyItemListener
{
	bool CanBuyItem( HexPlayerComponent player, VendorComponent vendor, VendorItem item );
}

/// <summary>
/// Permission hook: can a player sell this item? Return false to block.
/// </summary>
public interface ICanSellItemListener
{
	bool CanSellItem( HexPlayerComponent player, VendorComponent vendor, VendorItem item, ItemInstance instance );
}

/// <summary>
/// Fired after a player buys an item from a vendor.
/// </summary>
public interface IItemBoughtListener
{
	void OnItemBought( HexPlayerComponent player, VendorComponent vendor, VendorItem item, ItemInstance instance );
}

/// <summary>
/// Fired after a player sells an item to a vendor.
/// </summary>
public interface IItemSoldListener
{
	void OnItemSold( HexPlayerComponent player, VendorComponent vendor, VendorItem item );
}

/// <summary>
/// Fired when a player opens/interacts with a vendor.
/// </summary>
public interface IVendorOpenedListener
{
	void OnVendorOpened( HexPlayerComponent player, VendorComponent vendor );
}
