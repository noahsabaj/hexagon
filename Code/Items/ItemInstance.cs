using System.Text.Json.Serialization;

namespace Hexagon.Items;

/// <summary>
/// A runtime instance of an item, backed by the database. Each instance has a unique ID,
/// references an ItemDefinition, and stores per-instance data (condition, ammo, custom data).
///
/// Item instances live inside inventories at specific grid positions.
/// </summary>
public class ItemInstance
{
	/// <summary>
	/// Unique database ID for this item instance.
	/// </summary>
	public string Id { get; set; }

	/// <summary>
	/// The UniqueId of the ItemDefinition this instance is based on.
	/// </summary>
	public string DefinitionId { get; set; }

	/// <summary>
	/// The inventory this item is currently in. 0 or empty = world/not in inventory.
	/// </summary>
	public string InventoryId { get; set; }

	/// <summary>
	/// Grid X position within the inventory.
	/// </summary>
	public int X { get; set; }

	/// <summary>
	/// Grid Y position within the inventory.
	/// </summary>
	public int Y { get; set; }

	/// <summary>
	/// Per-instance custom data (condition, ammo count, custom properties, etc.).
	/// </summary>
	public Dictionary<string, object> Data { get; set; } = new();

	/// <summary>
	/// The character ID that owns this item (for ownership tracking).
	/// </summary>
	public string CharacterId { get; set; }

	/// <summary>
	/// Whether this item instance has unsaved changes.
	/// </summary>
	[JsonIgnore] public bool IsDirty { get; private set; }

	/// <summary>
	/// Get the ItemDefinition for this instance.
	/// </summary>
	[JsonIgnore]
	public ItemDefinition Definition => ItemManager.GetDefinition( DefinitionId );

	/// <summary>
	/// Get a per-instance data value.
	/// </summary>
	public T GetData<T>( string key, T defaultValue = default )
	{
		if ( Data == null || !Data.TryGetValue( key, out var value ) )
			return defaultValue;

		try
		{
			if ( value is T typed )
				return typed;

			return (T)Convert.ChangeType( value, typeof( T ) );
		}
		catch
		{
			return defaultValue;
		}
	}

	/// <summary>
	/// Set a per-instance data value. Marks the item as dirty.
	/// </summary>
	public void SetData( string key, object value )
	{
		Data ??= new();
		Data[key] = value;
		MarkDirty();
	}

	/// <summary>
	/// Remove a per-instance data value.
	/// </summary>
	public void RemoveData( string key )
	{
		Data?.Remove( key );
		MarkDirty();
	}

	/// <summary>
	/// Mark this item as having unsaved changes.
	/// </summary>
	public void MarkDirty()
	{
		IsDirty = true;
	}

	/// <summary>
	/// Clear the dirty flag (called after save).
	/// </summary>
	internal void ClearDirty()
	{
		IsDirty = false;
	}

	/// <summary>
	/// Save this item instance to the database.
	/// </summary>
	public void Save()
	{
		if ( !IsDirty ) return;
		Persistence.DatabaseManager.Save( "items", Id, this );
		ClearDirty();
	}
}
