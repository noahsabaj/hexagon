namespace Hexagon.Inventory;

/// <summary>
/// Manages inventory lifecycle: creation, restoration, persistence, and dirty tracking.
/// </summary>
public static class InventoryManager
{
	private static readonly Dictionary<string, HexInventory> _inventories = new();
	private static readonly HashSet<string> _dirtyInventories = new();

	/// <summary>
	/// All active inventories.
	/// </summary>
	public static IReadOnlyDictionary<string, HexInventory> Inventories => _inventories;

	internal static void Initialize()
	{
		Log.Info( "Hexagon: InventoryManager initialized." );
	}

	/// <summary>
	/// Create a new inventory and persist it.
	/// </summary>
	public static HexInventory Create( int width, int height, string ownerId = null, string type = "main" )
	{
		var inv = new HexInventory
		{
			Id = Persistence.DatabaseManager.NewId(),
			Width = width,
			Height = height,
			OwnerId = ownerId,
			Type = type
		};

		Persistence.DatabaseManager.Save( "inventories", inv.Id, inv );
		_inventories[inv.Id] = inv;

		return inv;
	}

	/// <summary>
	/// Create an inventory using the default config dimensions.
	/// </summary>
	public static HexInventory CreateDefault( string ownerId = null, string type = "main" )
	{
		var w = Config.HexConfig.Get<int>( "inventory.defaultWidth", 4 );
		var h = Config.HexConfig.Get<int>( "inventory.defaultHeight", 4 );
		return Create( w, h, ownerId, type );
	}

	/// <summary>
	/// Get an inventory by ID. Loads from database if not in memory.
	/// </summary>
	public static HexInventory Get( string inventoryId )
	{
		if ( string.IsNullOrEmpty( inventoryId ) ) return null;

		if ( _inventories.TryGetValue( inventoryId, out var inv ) )
			return inv;

		// Try loading from DB
		var loaded = Persistence.DatabaseManager.Load<HexInventory>( "inventories", inventoryId );
		if ( loaded != null )
		{
			loaded.RestoreItems();
			_inventories[loaded.Id] = loaded;
		}

		return loaded;
	}

	/// <summary>
	/// Load all inventories for a character.
	/// </summary>
	public static List<HexInventory> LoadForCharacter( string characterId )
	{
		var inventories = Persistence.DatabaseManager.Select<HexInventory>(
			"inventories",
			inv => inv.OwnerId == characterId
		);

		foreach ( var inv in inventories )
		{
			inv.RestoreItems();
			_inventories[inv.Id] = inv;
		}

		return inventories;
	}

	/// <summary>
	/// Delete an inventory and all its items.
	/// </summary>
	public static void Delete( string inventoryId )
	{
		if ( _inventories.TryGetValue( inventoryId, out var inv ) )
		{
			// Remove all items
			foreach ( var itemId in inv.ItemIds.ToList() )
			{
				Items.ItemManager.DestroyInstance( itemId );
			}

			_inventories.Remove( inventoryId );
		}

		_dirtyInventories.Remove( inventoryId );
		Persistence.DatabaseManager.Delete( "inventories", inventoryId );
	}

	/// <summary>
	/// Mark an inventory as needing a sync to receivers.
	/// </summary>
	internal static void MarkDirty( string inventoryId )
	{
		_dirtyInventories.Add( inventoryId );
	}

	/// <summary>
	/// Get all dirty inventory IDs and clear the dirty set.
	/// Used by HexInventoryComponent for network sync.
	/// </summary>
	internal static HashSet<string> GetDirtyAndClear()
	{
		var dirty = new HashSet<string>( _dirtyInventories );
		_dirtyInventories.Clear();
		return dirty;
	}

	/// <summary>
	/// Save all dirty inventories and their items.
	/// </summary>
	public static void SaveAll()
	{
		foreach ( var inv in _inventories.Values )
		{
			inv.Save();
		}

		Items.ItemManager.SaveAll();
		_dirtyInventories.Clear();
	}

	/// <summary>
	/// Unload an inventory from memory (e.g. when a character disconnects).
	/// Saves first.
	/// </summary>
	public static void Unload( string inventoryId )
	{
		if ( _inventories.TryGetValue( inventoryId, out var inv ) )
		{
			inv.Save();
			_inventories.Remove( inventoryId );
			_dirtyInventories.Remove( inventoryId );
		}
	}
}
