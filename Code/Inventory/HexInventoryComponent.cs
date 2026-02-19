namespace Hexagon.Inventory;

/// <summary>
/// Snapshot of an inventory's state for client-side caching.
/// </summary>
public class InventorySnapshot
{
	public string Id { get; set; }
	public string OwnerId { get; set; }
	public string Type { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
	public List<ItemSnapshot> Items { get; set; } = new();
}

/// <summary>
/// Snapshot of a single item instance within an inventory.
/// </summary>
public class ItemSnapshot
{
	public string Id { get; set; }
	public string DefinitionId { get; set; }
	public int X { get; set; }
	public int Y { get; set; }
	public Dictionary<string, Items.ItemDataTrait> Traits { get; set; } = new();
}

/// <summary>
/// Catalog entry sent to clients when opening a vendor.
/// </summary>
public class VendorCatalogEntry
{
	public string DefinitionId { get; set; }
	public string DisplayName { get; set; }
	public string Description { get; set; }
	public string Category { get; set; }
	public int BuyPrice { get; set; }
	public int SellPrice { get; set; }
}

/// <summary>
/// Singleton network bridge for the inventory system. Lives on the Hexagon Services GameObject.
///
/// Server-side: flushes dirty inventories to receivers via RPCs.
/// Client-side: caches inventory snapshots for UI consumption.
/// Also handles item action RPCs (move, transfer, drop, use) and vendor buy/sell RPCs.
/// </summary>
public sealed class HexInventoryComponent : Component
{
	public static HexInventoryComponent Instance { get; private set; }

	// --- Client-side cache ---

	private readonly Dictionary<string, InventorySnapshot> _clientInventories = new();

	/// <summary>
	/// Client-side inventory cache, keyed by inventory ID.
	/// </summary>
	public IReadOnlyDictionary<string, InventorySnapshot> ClientInventories => _clientInventories;

	/// <summary>
	/// Get a cached inventory snapshot by ID (client-side).
	/// </summary>
	public InventorySnapshot GetClientInventory( string id )
	{
		if ( string.IsNullOrEmpty( id ) ) return null;
		return _clientInventories.GetValueOrDefault( id );
	}

	// --- Client-side vendor state ---

	/// <summary>
	/// The most recently received vendor catalog (client-side).
	/// </summary>
	public List<VendorCatalogEntry> CurrentVendorCatalog { get; private set; }

	/// <summary>
	/// The vendor ID for the currently open vendor (client-side).
	/// </summary>
	public string CurrentVendorId { get; private set; }

	/// <summary>
	/// The vendor name for the currently open vendor (client-side).
	/// </summary>
	public string CurrentVendorName { get; private set; }

	protected override void OnStart()
	{
		if ( Instance != null && Instance != this )
		{
			Destroy();
			return;
		}

		Instance = this;
	}

	protected override void OnDestroy()
	{
		if ( Instance == this )
			Instance = null;
	}

	// --- Server-side: flush dirty inventories ---

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;

		FlushDirtyInventories();
	}

	private void FlushDirtyInventories()
	{
		var dirtyIds = InventoryManager.GetDirtyAndClear();
		if ( dirtyIds.Count == 0 ) return;

		foreach ( var invId in dirtyIds )
		{
			var inv = InventoryManager.Get( invId );
			if ( inv == null ) continue;

			var receivers = inv.GetReceivers();
			if ( receivers.Count == 0 ) continue;

			var snapshot = BuildSnapshot( inv );

			using ( Rpc.FilterInclude( receivers.ToList() ) )
			{
				ReceiveInventorySnapshot( snapshot );
			}
		}
	}

	private InventorySnapshot BuildSnapshot( HexInventory inv )
	{
		var snapshot = new InventorySnapshot
		{
			Id = inv.Id,
			OwnerId = inv.OwnerId,
			Type = inv.Type,
			Width = inv.Width,
			Height = inv.Height
		};

		foreach ( var item in inv.Items.Values )
		{
			snapshot.Items.Add( new ItemSnapshot
			{
				Id = item.Id,
				DefinitionId = item.DefinitionId,
				X = item.X,
				Y = item.Y,
				Traits = item.Traits ?? new()
			} );
		}

		return snapshot;
	}

	/// <summary>
	/// Send a full inventory snapshot to a specific connection.
	/// Used when a receiver is first added (e.g. opening storage).
	/// </summary>
	internal void SendSnapshotTo( HexInventory inv, Connection conn )
	{
		var snapshot = BuildSnapshot( inv );

		using ( Rpc.FilterInclude( conn ) )
		{
			ReceiveInventorySnapshot( snapshot );
		}
	}

	// --- Server to Client RPCs ---

	/// <summary>
	/// Server sends an inventory snapshot to filtered receivers.
	/// InventorySnapshot is passed as a typed parameter — no JSON serialization in the RPC path.
	/// </summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void ReceiveInventorySnapshot( InventorySnapshot snapshot )
	{
		if ( snapshot == null ) return;

		_clientInventories[snapshot.Id] = snapshot;

		IHexInventoryEvent.Post(
			x => x.OnInventoryUpdated( snapshot.Id ) );
	}

	/// <summary>
	/// Server notifies clients that an inventory is no longer available.
	/// </summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void ReceiveInventoryRemoved( string inventoryId )
	{
		_clientInventories.Remove( inventoryId );

		IHexInventoryEvent.Post(
			x => x.OnInventoryRemoved( inventoryId ) );
	}

	/// <summary>
	/// Server sends a vendor catalog to the client.
	/// Catalog is passed as a typed List — no JSON serialization in the RPC path.
	/// </summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void ReceiveVendorCatalog( string vendorId, string vendorName, List<VendorCatalogEntry> catalog )
	{
		CurrentVendorId = vendorId;
		CurrentVendorName = vendorName;
		CurrentVendorCatalog = catalog ?? new();

		IHexInventoryEvent.Post(
			x => x.OnVendorCatalogReceived( vendorId, vendorName, CurrentVendorCatalog ) );
	}

	/// <summary>
	/// Server sends a vendor operation result to the client.
	/// </summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void ReceiveVendorResult( bool success, string message )
	{
		IHexInventoryEvent.Post(
			x => x.OnVendorResult( success, message ) );
	}

	// --- Client to Server RPCs ---

	/// <summary>
	/// Client requests to move an item within an inventory.
	/// </summary>
	[Rpc.Host]
	public void RequestMoveItem( string inventoryId, string itemId, int newX, int newY )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var inv = InventoryManager.Get( inventoryId );
		if ( inv == null || !inv.GetReceivers().Contains( player.Connection ) ) return;

		inv.Move( itemId, newX, newY );
	}

	/// <summary>
	/// Client requests to transfer an item between inventories.
	/// </summary>
	[Rpc.Host]
	public void RequestTransferItem( string sourceInvId, string itemId, string targetInvId, int x, int y )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var source = InventoryManager.Get( sourceInvId );
		var target = InventoryManager.Get( targetInvId );

		if ( source == null || target == null ) return;
		if ( !source.GetReceivers().Contains( player.Connection ) || !target.GetReceivers().Contains( player.Connection ) ) return;

		source.Transfer( itemId, target, x, y );
	}

	/// <summary>
	/// Client requests to drop an item from an inventory into the world.
	/// </summary>
	[Rpc.Host]
	public void RequestDropItem( string inventoryId, string itemId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var inv = InventoryManager.Get( inventoryId );
		if ( inv == null || !inv.GetReceivers().Contains( player.Connection ) ) return;

		var item = Items.ItemManager.GetInstance( itemId );
		if ( item == null ) return;

		var def = item.Definition;
		if ( def == null || !def.CanDrop ) return;

		// Remove from inventory first
		inv.Remove( itemId );

		// Notify definition — default OnDrop spawns a world item if WorldModel is set
		def.OnDrop( player, item );
	}

	/// <summary>
	/// Client requests to use an item from an inventory.
	/// </summary>
	[Rpc.Host]
	public void RequestUseItem( string inventoryId, string itemId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var inv = InventoryManager.Get( inventoryId );
		if ( inv == null || !inv.GetReceivers().Contains( player.Connection ) ) return;

		var item = Items.ItemManager.GetInstance( itemId );
		if ( item == null ) return;

		var def = item.Definition;
		if ( def == null ) return;

		if ( !def.OnCanUse( player, item ) ) return;

		var consumed = def.OnUse( player, item );
		if ( consumed )
		{
			inv.Remove( itemId );
			Items.ItemManager.DestroyInstance( itemId );
		}
	}

	/// <summary>
	/// Client requests to buy an item from a vendor.
	/// </summary>
	[Rpc.Host]
	public void RequestBuyItem( string vendorId, string definitionId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var vendor = Vendors.VendorManager.GetVendor( vendorId );
		if ( vendor == null ) return;

		var (success, message) = Vendors.VendorManager.Buy( player, vendor, definitionId );

		// Send result to the caller
		using ( Rpc.FilterInclude( player.Connection ) )
		{
			ReceiveVendorResult( success, message );
		}

		// If successful, re-sync private data (money changed)
		if ( success )
		{
			player.SyncPrivateData();
		}
	}

	/// <summary>
	/// Client requests to sell an item to a vendor.
	/// </summary>
	[Rpc.Host]
	public void RequestSellItem( string vendorId, string itemInstanceId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		var vendor = Vendors.VendorManager.GetVendor( vendorId );
		if ( vendor == null ) return;

		var (success, message) = Vendors.VendorManager.Sell( player, vendor, itemInstanceId );

		// Send result to the caller
		using ( Rpc.FilterInclude( player.Connection ) )
		{
			ReceiveVendorResult( success, message );
		}

		// If successful, re-sync private data (money changed)
		if ( success )
		{
			player.SyncPrivateData();
		}
	}
}
