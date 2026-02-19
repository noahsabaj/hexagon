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
	/// Strongly-typed data traits attached to this item instance.
	/// </summary>
	public Dictionary<string, ItemDataTrait> Traits { get; set; } = new();

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
	/// Retrieves a specific data trait, automatically creating and attaching it if missing.
	/// </summary>
	public T GetTrait<T>() where T : ItemDataTrait, new()
	{
		var typeName = typeof( T ).Name;
		if ( Traits.TryGetValue( typeName, out var existing ) )
			return (T)existing;

		var newTrait = new T();
		Traits[typeName] = newTrait;
		MarkDirty();
		return newTrait;
	}

	/// <summary>
	/// Checks if a trait exists without creating it.
	/// </summary>
	public bool TryGetTrait<T>( out T trait ) where T : ItemDataTrait
	{
		if ( Traits.TryGetValue( typeof( T ).Name, out var existing ) )
		{
			trait = (T)existing;
			return true;
		}

		trait = null;
		return false;
	}

	/// <summary>
	/// Remove a data trait.
	/// </summary>
	public void RemoveTrait<T>() where T : ItemDataTrait
	{
		if ( Traits.Remove( typeof( T ).Name ) )
		{
			MarkDirty();
		}
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
