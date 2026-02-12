using System.Text.Json.Serialization;

namespace Hexagon.Inventory;

/// <summary>
/// A grid-based inventory. Items occupy Width x Height cells at specific (X, Y) positions.
/// Supports receiver-based networking - only players who should see this inventory
/// receive its contents.
///
/// Inventories are persisted to the database and restored when characters load.
/// </summary>
public class HexInventory
{
	/// <summary>
	/// Unique database ID for this inventory.
	/// </summary>
	public string Id { get; set; }

	/// <summary>
	/// Grid width in cells.
	/// </summary>
	public int Width { get; set; }

	/// <summary>
	/// Grid height in cells.
	/// </summary>
	public int Height { get; set; }

	/// <summary>
	/// The character ID that owns this inventory. Empty for world containers.
	/// </summary>
	public string OwnerId { get; set; }

	/// <summary>
	/// Inventory type identifier (e.g. "main", "bag", "container").
	/// </summary>
	public string Type { get; set; } = "main";

	/// <summary>
	/// IDs of items in this inventory (for persistence).
	/// </summary>
	public List<string> ItemIds { get; set; } = new();

	// --- Runtime State (not persisted) ---

	[JsonIgnore] private readonly Dictionary<string, Items.ItemInstance> _items = new();
	[JsonIgnore] private readonly HashSet<Connection> _receivers = new();

	/// <summary>
	/// All items currently in this inventory.
	/// </summary>
	[JsonIgnore]
	public IReadOnlyDictionary<string, Items.ItemInstance> Items => _items;

	// --- Grid Operations ---

	/// <summary>
	/// Check if an item of given size can fit at position (x, y).
	/// </summary>
	public bool CanItemFit( int x, int y, int w, int h, string excludeItemId = null )
	{
		// Bounds check
		if ( x < 0 || y < 0 || x + w > Width || y + h > Height )
			return false;

		// Collision check against existing items
		foreach ( var item in _items.Values )
		{
			if ( excludeItemId != null && item.Id == excludeItemId )
				continue;

			var def = item.Definition;
			if ( def == null ) continue;

			// AABB overlap test
			if ( x < item.X + def.Width && x + w > item.X &&
				 y < item.Y + def.Height && y + h > item.Y )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Find an empty slot that can fit an item of the given size.
	/// Scans left-to-right, top-to-bottom.
	/// </summary>
	public (int x, int y)? FindEmptySlot( int w, int h )
	{
		for ( int iy = 0; iy <= Height - h; iy++ )
		{
			for ( int ix = 0; ix <= Width - w; ix++ )
			{
				if ( CanItemFit( ix, iy, w, h ) )
					return (ix, iy);
			}
		}

		return null;
	}

	/// <summary>
	/// Add an item instance to this inventory at a specific position.
	/// Returns true if successful.
	/// </summary>
	public bool AddAt( Items.ItemInstance item, int x, int y )
	{
		var def = item.Definition;
		if ( def == null ) return false;

		if ( !CanItemFit( x, y, def.Width, def.Height ) )
			return false;

		item.InventoryId = Id;
		item.X = x;
		item.Y = y;
		item.MarkDirty();

		_items[item.Id] = item;

		if ( !ItemIds.Contains( item.Id ) )
			ItemIds.Add( item.Id );

		NotifyReceivers();
		return true;
	}

	/// <summary>
	/// Add an item instance to the first available slot.
	/// Returns true if successful.
	/// </summary>
	public bool Add( Items.ItemInstance item )
	{
		var def = item.Definition;
		if ( def == null ) return false;

		var slot = FindEmptySlot( def.Width, def.Height );
		if ( slot == null ) return false;

		return AddAt( item, slot.Value.x, slot.Value.y );
	}

	/// <summary>
	/// Remove an item from this inventory.
	/// </summary>
	public bool Remove( string itemId )
	{
		if ( !_items.TryGetValue( itemId, out var item ) )
			return false;

		_items.Remove( itemId );
		ItemIds.Remove( itemId );

		item.InventoryId = null;
		item.X = 0;
		item.Y = 0;
		item.MarkDirty();

		NotifyReceivers();
		return true;
	}

	/// <summary>
	/// Move an item within this inventory to a new position.
	/// </summary>
	public bool Move( string itemId, int newX, int newY )
	{
		if ( !_items.TryGetValue( itemId, out var item ) )
			return false;

		var def = item.Definition;
		if ( def == null ) return false;

		if ( !CanItemFit( newX, newY, def.Width, def.Height, excludeItemId: itemId ) )
			return false;

		item.X = newX;
		item.Y = newY;
		item.MarkDirty();

		NotifyReceivers();
		return true;
	}

	/// <summary>
	/// Transfer an item from this inventory to another.
	/// </summary>
	public bool Transfer( string itemId, HexInventory target, int? targetX = null, int? targetY = null )
	{
		if ( !_items.TryGetValue( itemId, out var item ) )
			return false;

		var def = item.Definition;
		if ( def == null ) return false;

		// Find target slot
		int x, y;
		if ( targetX.HasValue && targetY.HasValue )
		{
			if ( !target.CanItemFit( targetX.Value, targetY.Value, def.Width, def.Height ) )
				return false;
			x = targetX.Value;
			y = targetY.Value;
		}
		else
		{
			var slot = target.FindEmptySlot( def.Width, def.Height );
			if ( slot == null ) return false;
			x = slot.Value.x;
			y = slot.Value.y;
		}

		// Remove from source
		Remove( itemId );

		// Add to target
		target.AddAt( item, x, y );

		// Notify definition
		def.OnTransferred( item, this, target );

		return true;
	}

	// --- Queries ---

	/// <summary>
	/// Check if this inventory has an item with the given definition ID.
	/// </summary>
	public bool HasItem( string definitionId )
	{
		return _items.Values.Any( i => i.DefinitionId == definitionId );
	}

	/// <summary>
	/// Count items with the given definition ID.
	/// </summary>
	public int CountItem( string definitionId )
	{
		return _items.Values.Count( i => i.DefinitionId == definitionId );
	}

	/// <summary>
	/// Get the first item with the given definition ID.
	/// </summary>
	public Items.ItemInstance FindItem( string definitionId )
	{
		return _items.Values.FirstOrDefault( i => i.DefinitionId == definitionId );
	}

	/// <summary>
	/// Check if the inventory is full (no 1x1 slot available).
	/// </summary>
	public bool IsFull => FindEmptySlot( 1, 1 ) == null;

	/// <summary>
	/// Number of items in the inventory.
	/// </summary>
	public int ItemCount => _items.Count;

	// --- Receiver System ---

	/// <summary>
	/// Add a player as a receiver of this inventory's contents.
	/// They will receive the full inventory state.
	/// </summary>
	public void AddReceiver( Connection conn )
	{
		_receivers.Add( conn );
	}

	/// <summary>
	/// Remove a player from this inventory's receivers.
	/// </summary>
	public void RemoveReceiver( Connection conn )
	{
		_receivers.Remove( conn );
	}

	/// <summary>
	/// Get all current receivers.
	/// </summary>
	public IReadOnlySet<Connection> GetReceivers() => _receivers;

	/// <summary>
	/// Notify all receivers that the inventory has changed.
	/// The actual networking is handled by InventoryManager.
	/// </summary>
	private void NotifyReceivers()
	{
		InventoryManager.MarkDirty( Id );
	}

	// --- Persistence Helpers ---

	/// <summary>
	/// Load item instances from the database into the runtime cache.
	/// Called by InventoryManager when restoring an inventory.
	/// </summary>
	internal void RestoreItems()
	{
		_items.Clear();

		foreach ( var itemId in ItemIds.ToList() )
		{
			var item = Hexagon.Items.ItemManager.GetInstance( itemId );
			if ( item != null )
			{
				_items[item.Id] = item;
			}
			else
			{
				ItemIds.Remove( itemId );
			}
		}
	}

	/// <summary>
	/// Save this inventory's metadata to the database.
	/// </summary>
	public void Save()
	{
		Persistence.DatabaseManager.Save( "inventories", Id, this );
	}
}
