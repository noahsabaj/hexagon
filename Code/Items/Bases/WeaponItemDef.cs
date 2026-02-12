namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for weapon items. Handles equip/unequip lifecycle and ammo tracking.
/// Schema devs can subclass this for specific weapon types or use it directly via .item assets.
/// </summary>
[AssetType( Name = "Weapon Item", Extension = "weapon" )]
public class WeaponItemDef : ItemDefinition
{
	/// <summary>
	/// The ammo definition ID this weapon uses. Empty for melee weapons.
	/// </summary>
	[Property] public string AmmoType { get; set; }

	/// <summary>
	/// Maximum ammo this weapon can hold in its magazine.
	/// </summary>
	[Property] public int ClipSize { get; set; } = 30;

	/// <summary>
	/// Model used when the weapon is held (viewmodel or world attachment).
	/// </summary>
	[Property] public Model WeaponModel { get; set; }

	/// <summary>
	/// Whether this weapon is two-handed (prevents offhand use).
	/// </summary>
	[Property] public bool TwoHanded { get; set; }

	public override Dictionary<string, ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions["equip"] = new ItemAction
		{
			Name = "Equip",
			Icon = "sports_martial_arts",
			OnRun = ( player, item ) =>
			{
				OnEquip( player, item );
				return false; // Don't consume
			},
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		};

		actions["unequip"] = new ItemAction
		{
			Name = "Unequip",
			Icon = "back_hand",
			OnRun = ( player, item ) =>
			{
				OnUnequip( player, item );
				return false;
			}
		};

		return actions;
	}

	/// <summary>
	/// Get the current ammo in this weapon's clip.
	/// </summary>
	public int GetClipAmmo( ItemInstance item )
	{
		return item.GetData<int>( "clipAmmo", ClipSize );
	}

	/// <summary>
	/// Set the current ammo in this weapon's clip.
	/// </summary>
	public void SetClipAmmo( ItemInstance item, int amount )
	{
		item.SetData( "clipAmmo", Math.Clamp( amount, 0, ClipSize ) );
	}

	public override void OnInstanced( ItemInstance item )
	{
		base.OnInstanced( item );

		// Initialize with full clip
		if ( !item.Data.ContainsKey( "clipAmmo" ) )
			item.SetData( "clipAmmo", ClipSize );
	}
}
