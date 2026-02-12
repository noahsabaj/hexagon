namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for ammo items. When used, loads ammo into a compatible weapon
/// in the player's inventory, or adds to a reserve ammo pool.
/// </summary>
[AssetType( Name = "Ammo Item", Extension = "ammo" )]
public class AmmoItemDef : ItemDefinition
{
	/// <summary>
	/// The ammo type identifier. Must match WeaponItemDef.AmmoType to be compatible.
	/// </summary>
	[Property] public string AmmoType { get; set; }

	/// <summary>
	/// How much ammo this item gives when used.
	/// </summary>
	[Property] public int AmmoAmount { get; set; } = 30;

	public override Dictionary<string, ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions["use"] = new ItemAction
		{
			Name = "Use",
			Icon = "add_circle",
			OnRun = ( player, item ) => OnUse( player, item ),
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		};

		return actions;
	}

	public override bool OnUse( HexPlayerComponent player, ItemInstance item )
	{
		if ( string.IsNullOrEmpty( AmmoType ) )
			return false;

		// Try to find a compatible weapon in the player's main inventory and load it
		var character = player.Character;
		if ( character == null ) return false;

		var inventories = Inventory.InventoryManager.LoadForCharacter( character.Data.Id );

		foreach ( var inv in inventories )
		{
			foreach ( var kvp in inv.Items )
			{
				var otherItem = kvp.Value;
				if ( otherItem.Definition is WeaponItemDef weaponDef && weaponDef.AmmoType == AmmoType )
				{
					var currentAmmo = weaponDef.GetClipAmmo( otherItem );
					if ( currentAmmo < weaponDef.ClipSize )
					{
						var toLoad = Math.Min( AmmoAmount, weaponDef.ClipSize - currentAmmo );
						weaponDef.SetClipAmmo( otherItem, currentAmmo + toLoad );
						return true; // Consume the ammo item
					}
				}
			}
		}

		return false; // No compatible weapon found or all full
	}
}
