namespace Hexagon.Storage;

/// <summary>
/// A world-placed storage container that players can interact with via USE key.
/// Items persist across server restarts via the inventory system.
///
/// Place on any GameObject in the scene. The InventoryId is generated on first use
/// and persists via scene save, linking to the database-backed inventory.
/// </summary>
public sealed class StorageComponent : Component, Component.IPressable
{
	/// <summary>
	/// Display name shown in the tooltip.
	/// </summary>
	[Property] public string StorageName { get; set; } = "Storage";

	/// <summary>
	/// Grid width of the storage inventory.
	/// </summary>
	[Property] public int Width { get; set; } = 4;

	/// <summary>
	/// Grid height of the storage inventory.
	/// </summary>
	[Property] public int Height { get; set; } = 4;

	/// <summary>
	/// Persisted inventory ID. Set automatically on first interaction.
	/// </summary>
	[Property] public string InventoryId { get; set; } = "";

	private HexInventory _inventory;

	/// <summary>
	/// The backing inventory for this container.
	/// </summary>
	public HexInventory Inventory => _inventory;

	private void EnsureInventory()
	{
		if ( _inventory != null ) return;

		if ( !string.IsNullOrEmpty( InventoryId ) )
		{
			_inventory = InventoryManager.Get( InventoryId );
		}

		if ( _inventory == null )
		{
			var w = Width > 0 ? Width : Config.HexConfig.Get<int>( "storage.defaultWidth", 4 );
			var h = Height > 0 ? Height : Config.HexConfig.Get<int>( "storage.defaultHeight", 4 );
			_inventory = InventoryManager.Create( w, h, type: "container" );
			InventoryId = _inventory.Id;
		}
	}

	private HexPlayerComponent GetPlayer( Component.IPressable.Event e )
	{
		return e.Source?.GetComponentInParent<HexPlayerComponent>();
	}

	// --- IPressable ---

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player?.Character == null ) return false;

		return HexEvents.CanAll<ICanOpenStorageListener>(
			x => x.CanOpenStorage( player, this ) );
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player?.Character == null ) return false;

		EnsureInventory();

		_inventory.AddReceiver( player.Connection );

		// Send initial inventory snapshot to the player
		Inventory.HexInventoryComponent.Instance?.SendSnapshotTo( _inventory, player.Connection );

		HexEvents.Fire<IStorageOpenedListener>(
			x => x.OnStorageOpened( player, this ) );

		HexLog.Add( LogType.Item, player, $"Opened storage \"{StorageName}\" ({InventoryId})" );
		return true;
	}

	public void Release( Component.IPressable.Event e )
	{
		CloseForPlayer( e );
	}

	public void Blur( Component.IPressable.Event e )
	{
		CloseForPlayer( e );
	}

	private void CloseForPlayer( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player == null ) return;
		if ( _inventory == null || !_inventory.GetReceivers().Contains( player.Connection ) ) return;

		_inventory.RemoveReceiver( player.Connection );

		// Notify client that this inventory is no longer available
		if ( Inventory.HexInventoryComponent.Instance != null )
		{
			using ( Rpc.FilterInclude( player.Connection ) )
			{
				Inventory.HexInventoryComponent.Instance.ReceiveInventoryRemoved( _inventory.Id );
			}
		}

		HexEvents.Fire<IStorageClosedListener>(
			x => x.OnStorageClosed( player, this ) );
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		return new Component.IPressable.Tooltip( StorageName, "inventory_2", "Open container" );
	}
}

/// <summary>
/// Permission hook: can a player open this storage container? Return false to block.
/// </summary>
public interface ICanOpenStorageListener
{
	bool CanOpenStorage( HexPlayerComponent player, StorageComponent storage );
}

/// <summary>
/// Fired when a player opens a storage container.
/// </summary>
public interface IStorageOpenedListener
{
	void OnStorageOpened( HexPlayerComponent player, StorageComponent storage );
}

/// <summary>
/// Fired when a player closes a storage container (release or look away).
/// </summary>
public interface IStorageClosedListener
{
	void OnStorageClosed( HexPlayerComponent player, StorageComponent storage );
}
