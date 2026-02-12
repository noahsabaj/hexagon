namespace Hexagon.Items;

/// <summary>
/// Base class for item type definitions. Create .item asset files in the s&box editor
/// for simple items, or subclass in C# for items with custom behavior.
///
/// Schema devs can create items visually (set name, model, size in editor)
/// or extend this class for weapons, bags, outfits, etc.
/// </summary>
[AssetType( Name = "Item", Extension = "item" )]
public class ItemDefinition : GameResource
{
	/// <summary>
	/// Unique identifier for this item type.
	/// </summary>
	[Property] public string UniqueId { get; set; }

	/// <summary>
	/// Display name of the item.
	/// </summary>
	[Property] public string DisplayName { get; set; }

	/// <summary>
	/// Description shown in tooltips.
	/// </summary>
	[Property, TextArea] public string Description { get; set; }

	/// <summary>
	/// World model for when the item is dropped on the ground.
	/// </summary>
	[Property] public Model WorldModel { get; set; }

	/// <summary>
	/// Inventory grid width (in cells).
	/// </summary>
	[Property] public int Width { get; set; } = 1;

	/// <summary>
	/// Inventory grid height (in cells).
	/// </summary>
	[Property] public int Height { get; set; } = 1;

	/// <summary>
	/// Category for organization (e.g. "Weapons", "Medical", "Misc").
	/// </summary>
	[Property] public string Category { get; set; } = "Misc";

	/// <summary>
	/// Maximum stack size. 1 = no stacking.
	/// </summary>
	[Property] public int MaxStack { get; set; } = 1;

	/// <summary>
	/// Whether this item can be dropped into the world.
	/// </summary>
	[Property] public bool CanDrop { get; set; } = true;

	/// <summary>
	/// Sort order within category.
	/// </summary>
	[Property] public int Order { get; set; } = 100;

	// --- Virtual Behavior Methods ---

	/// <summary>
	/// Get the context menu actions for this item type.
	/// Override in subclasses to add Use, Equip, etc.
	/// </summary>
	public virtual Dictionary<string, ItemAction> GetActions()
	{
		return new Dictionary<string, ItemAction>();
	}

	/// <summary>
	/// Called when a player uses this item. Return true to consume (remove) the item.
	/// </summary>
	public virtual bool OnUse( HexPlayerComponent player, ItemInstance item ) => false;

	/// <summary>
	/// Permission check: can this player use this item?
	/// </summary>
	public virtual bool OnCanUse( HexPlayerComponent player, ItemInstance item ) => true;

	/// <summary>
	/// Called when this item is equipped by a player.
	/// </summary>
	public virtual void OnEquip( HexPlayerComponent player, ItemInstance item ) { }

	/// <summary>
	/// Called when this item is unequipped.
	/// </summary>
	public virtual void OnUnequip( HexPlayerComponent player, ItemInstance item ) { }

	/// <summary>
	/// Called when this item is dropped into the world.
	/// </summary>
	public virtual void OnDrop( HexPlayerComponent player, ItemInstance item ) { }

	/// <summary>
	/// Called when this item is picked up from the world.
	/// </summary>
	public virtual void OnPickup( HexPlayerComponent player, ItemInstance item ) { }

	/// <summary>
	/// Called when this item is transferred between inventories.
	/// </summary>
	public virtual void OnTransferred( ItemInstance item, Inventory.HexInventory from, Inventory.HexInventory to ) { }

	/// <summary>
	/// Called when a new instance of this item is created.
	/// </summary>
	public virtual void OnInstanced( ItemInstance item ) { }

	/// <summary>
	/// Called when an instance of this item is permanently removed.
	/// </summary>
	public virtual void OnRemoved( ItemInstance item ) { }

	/// <summary>
	/// Called when the asset is loaded. Registers with ItemManager.
	/// </summary>
	protected override void PostLoad()
	{
		base.PostLoad();
		if ( !string.IsNullOrEmpty( UniqueId ) )
			ItemManager.Register( this );
	}

	protected override void PostReload()
	{
		base.PostReload();
		if ( !string.IsNullOrEmpty( UniqueId ) )
			ItemManager.Register( this );
	}
}
