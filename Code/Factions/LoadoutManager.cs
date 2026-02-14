namespace Hexagon.Factions;

/// <summary>
/// Manages class-based loadout distribution. When a character is created or loaded,
/// the loadout system checks their class for configured items and grants them.
/// </summary>
public static class LoadoutManager
{
	internal static void Initialize()
	{
		Log.Info( "Hexagon: LoadoutManager initialized." );
	}

	/// <summary>
	/// Apply the loadout for a character's class. Creates items and adds them to the character's main inventory.
	/// </summary>
	public static void ApplyLoadout( HexPlayerComponent player, Characters.HexCharacter character )
	{
		if ( player == null || character == null ) return;

		var classId = character.Data.Class;
		if ( string.IsNullOrEmpty( classId ) ) return;

		var classDef = FactionManager.GetClass( classId );
		if ( classDef == null || classDef.Loadout == null || classDef.Loadout.Count == 0 )
			return;

		// Permission check
		if ( !HexEvents.CanAll<ICanApplyLoadoutListener>(
			x => x.CanApplyLoadout( player, character, classDef ) ) )
			return;

		// Find or create the character's main inventory
		var inventories = Inventory.InventoryManager.LoadForCharacter( character.Data.Id );
		var mainInv = inventories.FirstOrDefault( inv => inv.Type == "main" );
		if ( mainInv == null )
		{
			mainInv = Inventory.InventoryManager.CreateDefault( character.Data.Id, "main" );
		}

		var grantedItems = new List<Items.ItemInstance>();

		foreach ( var entry in classDef.Loadout )
		{
			if ( string.IsNullOrEmpty( entry.ItemDefinitionId ) || entry.Count <= 0 )
				continue;

			for ( int i = 0; i < entry.Count; i++ )
			{
				var item = Items.ItemManager.CreateInstance( entry.ItemDefinitionId, character.Data.Id );
				if ( item == null )
				{
					Log.Warning( $"Hexagon: Loadout failed to create item '{entry.ItemDefinitionId}'" );
					continue;
				}

				if ( mainInv.Add( item ) )
				{
					grantedItems.Add( item );
				}
				else
				{
					Log.Warning( $"Hexagon: Loadout could not fit item '{entry.ItemDefinitionId}' in inventory" );
					Items.ItemManager.DestroyInstance( item.Id );
				}
			}
		}

		if ( grantedItems.Count > 0 )
		{
			Logging.HexLog.Add( Logging.LogType.Item, player,
				$"Loadout applied: {grantedItems.Count} item(s) from class \"{classDef.Name}\"" );

			HexEvents.Fire<ILoadoutAppliedListener>(
				x => x.OnLoadoutApplied( player, character, grantedItems ) );
		}
	}

	/// <summary>
	/// Called when a new character is created. Always applies loadout.
	/// </summary>
	internal static void OnCharacterCreated( HexPlayerComponent player, Characters.HexCharacter character )
	{
		ApplyLoadout( player, character );
	}

	/// <summary>
	/// Called when a character is loaded. Only applies loadout if the class uses OnLoad mode.
	/// </summary>
	internal static void OnCharacterLoaded( HexPlayerComponent player, Characters.HexCharacter character )
	{
		var classId = character.Data.Class;
		if ( string.IsNullOrEmpty( classId ) ) return;

		var classDef = FactionManager.GetClass( classId );
		if ( classDef == null ) return;

		if ( classDef.LoadoutMode == LoadoutMode.OnLoad )
		{
			ApplyLoadout( player, character );
		}
	}
}

/// <summary>
/// Permission hook: can this loadout be applied? Return false to block.
/// </summary>
public interface ICanApplyLoadoutListener
{
	/// <summary>
	/// Called before a class loadout is applied to a character.
	/// </summary>
	bool CanApplyLoadout( HexPlayerComponent player, Characters.HexCharacter character, ClassDefinition classDef );
}

/// <summary>
/// Fired after a class loadout has been applied to a character.
/// </summary>
public interface ILoadoutAppliedListener
{
	/// <summary>
	/// Called after loadout items have been granted to a character.
	/// </summary>
	void OnLoadoutApplied( HexPlayerComponent player, Characters.HexCharacter character, List<Items.ItemInstance> items );
}
