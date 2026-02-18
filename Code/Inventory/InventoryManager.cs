namespace Hexagon.Inventory;

/// <summary>
/// Manages inventory lifecycle: creation, restoration, persistence, and dirty tracking.
///
/// Converted from a static class to a GameObjectSystem so that per-scene mutable state
/// (_inventories, _dirtyInventories) is scoped to the scene and cleared on scene reload.
/// All public methods remain static via facades — callers need no changes.
/// </summary>
public sealed class InventoryManager : GameObjectSystem<InventoryManager>
{
	private static InventoryManager _instance;

	private readonly Dictionary<string, HexInventory> _inventories = new();
	private readonly HashSet<string> _dirtyInventories = new();

	public InventoryManager( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	public override void Dispose()
	{
		SaveAll();
		_inventories.Clear();
		_dirtyInventories.Clear();

		if ( _instance == this )
			_instance = null;

		base.Dispose();
	}

	private static InventoryManager Instance => _instance;

	// --- Public Static Facade (unchanged signatures) ---

	/// <summary>
	/// All active inventories.
	/// </summary>
	public static IReadOnlyDictionary<string, HexInventory> Inventories =>
		(IReadOnlyDictionary<string, HexInventory>)Instance?._inventories ?? new Dictionary<string, HexInventory>();

	/// <summary>
	/// Create a new inventory and persist it.
	/// </summary>
	public static HexInventory Create( int width, int height, string ownerId = null, string type = "main" )
	{
		if ( Instance == null ) return null;

		var inv = new HexInventory
		{
			Id = Persistence.DatabaseManager.NewId(),
			Width = width,
			Height = height,
			OwnerId = ownerId,
			Type = type
		};

		Persistence.DatabaseManager.Save( "inventories", inv.Id, inv );
		Instance._inventories[inv.Id] = inv;

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
		if ( string.IsNullOrEmpty( inventoryId ) || Instance == null ) return null;

		if ( Instance._inventories.TryGetValue( inventoryId, out var inv ) )
			return inv;

		// Try loading from DB
		var loaded = Persistence.DatabaseManager.Load<HexInventory>( "inventories", inventoryId );
		if ( loaded != null )
		{
			loaded.RestoreItems();
			Instance._inventories[loaded.Id] = loaded;
		}

		return loaded;
	}

	/// <summary>
	/// Get already-loaded inventories for a character from the in-memory cache.
	/// Use this for active players whose inventories are guaranteed to be loaded.
	/// Use LoadForCharacter() only on first load or for offline characters.
	/// </summary>
	public static List<HexInventory> GetForCharacter( string characterId )
	{
		if ( Instance == null || string.IsNullOrEmpty( characterId ) )
			return new List<HexInventory>();

		return Instance._inventories.Values
			.Where( inv => inv.OwnerId == characterId )
			.ToList();
	}

	/// <summary>
	/// Load all inventories for a character.
	/// </summary>
	public static List<HexInventory> LoadForCharacter( string characterId )
	{
		if ( Instance == null ) return new List<HexInventory>();

		var inventories = Persistence.DatabaseManager.Select<HexInventory>(
			"inventories",
			inv => inv.OwnerId == characterId
		);

		foreach ( var inv in inventories )
		{
			inv.RestoreItems();
			Instance._inventories[inv.Id] = inv;
		}

		return inventories;
	}

	/// <summary>
	/// Delete an inventory and all its items.
	/// </summary>
	public static void Delete( string inventoryId )
	{
		if ( Instance != null && Instance._inventories.TryGetValue( inventoryId, out var inv ) )
		{
			foreach ( var itemId in inv.ItemIds.ToList() )
			{
				Items.ItemManager.DestroyInstance( itemId );
			}

			Instance._inventories.Remove( inventoryId );
		}

		Instance?._dirtyInventories.Remove( inventoryId );
		Persistence.DatabaseManager.Delete( "inventories", inventoryId );
	}

	/// <summary>
	/// Mark an inventory as needing a sync to receivers.
	/// </summary>
	internal static void MarkDirty( string inventoryId )
	{
		Instance?._dirtyInventories.Add( inventoryId );
	}

	/// <summary>
	/// Get all dirty inventory IDs and clear the dirty set.
	/// Used by HexInventoryComponent for network sync.
	/// </summary>
	internal static HashSet<string> GetDirtyAndClear()
	{
		if ( Instance == null ) return new HashSet<string>();

		var dirty = new HashSet<string>( Instance._dirtyInventories );
		Instance._dirtyInventories.Clear();
		return dirty;
	}

	/// <summary>
	/// Save all active inventories and their items.
	/// </summary>
	public static void SaveAll()
	{
		if ( Instance == null ) return;

		foreach ( var inv in Instance._inventories.Values )
		{
			inv.Save();
		}

		Items.ItemManager.SaveAll();
		Instance._dirtyInventories.Clear();
	}

	/// <summary>
	/// Unload an inventory from memory (e.g. when a character disconnects).
	/// Saves first.
	/// </summary>
	public static void Unload( string inventoryId )
	{
		if ( Instance == null ) return;

		if ( Instance._inventories.TryGetValue( inventoryId, out var inv ) )
		{
			inv.Save();
			Instance._inventories.Remove( inventoryId );
			Instance._dirtyInventories.Remove( inventoryId );
		}
	}
}
