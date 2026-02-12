using Hexagon.Items.Bases;

namespace Hexagon.Schema;

/// <summary>
/// Reference skeleton schema demonstrating how to build on the Hexagon framework.
/// Registers factions, classes, items, config values, and gameplay hooks.
///
/// Schema developers: use this as a template for your own schema plugin.
/// </summary>
[HexPlugin( "Skeleton Schema", Description = "Reference RP schema demonstrating all Hexagon features", Author = "Hexagon", Version = "1.0", Priority = 50 )]
public class SkeletonPlugin : IHexPlugin
{
	public void OnPluginLoaded()
	{
		RegisterFactions();
		RegisterClasses();
		RegisterItems();
		RegisterConfigs();

		// Add SkeletonHooks component to the framework GO for event hooks.
		// Must be a Component so Scene.GetAll<T>() discovers it for listener interfaces.
		var framework = HexagonFramework.Instance;
		if ( framework != null )
		{
			framework.GameObject.GetOrAddComponent<SkeletonHooks>();
		}
	}

	private void RegisterFactions()
	{
		var citizens = new FactionDefinition();
		citizens.UniqueId = "citizen";
		citizens.Name = "Citizens";
		citizens.Description = "Regular citizens of the city.";
		citizens.IsDefault = true;
		citizens.MaxPlayers = 0;
		citizens.Color = new Color( 0.29f, 0.565f, 0.886f ); // #4A90E2
		citizens.StartingMoney = 100;
		citizens.Order = 1;
		FactionManager.Register( citizens );

		var police = new FactionDefinition();
		police.UniqueId = "police";
		police.Name = "Civil Protection";
		police.Description = "Law enforcement officers protecting the city.";
		police.IsDefault = false;
		police.MaxPlayers = 4;
		police.Color = new Color( 0.886f, 0.29f, 0.29f ); // #E24A4A
		police.StartingMoney = 200;
		police.Order = 2;
		FactionManager.Register( police );
	}

	private void RegisterClasses()
	{
		var civilian = new ClassDefinition();
		civilian.UniqueId = "civilian";
		civilian.Name = "Civilian";
		civilian.Description = "An ordinary citizen.";
		civilian.FactionId = "citizen";
		civilian.MaxPlayers = 0;
		civilian.Order = 1;
		FactionManager.RegisterClass( civilian );

		var officer = new ClassDefinition();
		officer.UniqueId = "officer";
		officer.Name = "Officer";
		officer.Description = "A Civil Protection officer.";
		officer.FactionId = "police";
		officer.MaxPlayers = 0;
		officer.Order = 1;
		FactionManager.RegisterClass( officer );

		var chief = new ClassDefinition();
		chief.UniqueId = "chief";
		chief.Name = "Chief";
		chief.Description = "The Chief of Civil Protection.";
		chief.FactionId = "police";
		chief.MaxPlayers = 1;
		chief.Order = 2;
		FactionManager.RegisterClass( chief );
	}

	private void RegisterItems()
	{
		// Pistol
		var pistol = new WeaponItemDef();
		pistol.UniqueId = "weapon_pistol";
		pistol.DisplayName = "Pistol";
		pistol.Description = "A standard 9mm pistol.";
		pistol.Width = 1;
		pistol.Height = 2;
		pistol.Category = "Weapons";
		pistol.ClipSize = 12;
		pistol.AmmoType = "9mm";
		ItemManager.Register( pistol );

		// Baton (melee — no ammo)
		var baton = new WeaponItemDef();
		baton.UniqueId = "weapon_baton";
		baton.DisplayName = "Baton";
		baton.Description = "A standard-issue police baton.";
		baton.Width = 1;
		baton.Height = 2;
		baton.Category = "Weapons";
		baton.ClipSize = 0;
		baton.AmmoType = "";
		ItemManager.Register( baton );

		// Cash
		var cash = new CurrencyItemDef();
		cash.UniqueId = "money_cash";
		cash.DisplayName = "Cash";
		cash.Description = "A stack of bills.";
		cash.Width = 1;
		cash.Height = 1;
		cash.Category = "Currency";
		cash.DefaultAmount = 100;
		ItemManager.Register( cash );

		// Backpack
		var backpack = new BagItemDef();
		backpack.UniqueId = "bag_backpack";
		backpack.DisplayName = "Backpack";
		backpack.Description = "A sturdy backpack for extra storage.";
		backpack.Width = 1;
		backpack.Height = 2;
		backpack.Category = "Storage";
		backpack.BagWidth = 6;
		backpack.BagHeight = 4;
		ItemManager.Register( backpack );

		// Police Uniform
		var policeUniform = new OutfitItemDef();
		policeUniform.UniqueId = "outfit_police";
		policeUniform.DisplayName = "Police Uniform";
		policeUniform.Description = "A standard Civil Protection uniform.";
		policeUniform.Width = 1;
		policeUniform.Height = 2;
		policeUniform.Category = "Clothing";
		policeUniform.Slot = "torso";
		ItemManager.Register( policeUniform );

		// Pistol Ammo
		var ammo = new AmmoItemDef();
		ammo.UniqueId = "ammo_9mm";
		ammo.DisplayName = "9mm Ammo";
		ammo.Description = "A box of 9mm rounds.";
		ammo.Width = 1;
		ammo.Height = 1;
		ammo.Category = "Ammo";
		ammo.AmmoType = "9mm";
		ammo.AmmoAmount = 12;
		ItemManager.Register( ammo );
	}

	private void RegisterConfigs()
	{
		Config.HexConfig.Add( "gameplay.walkSpeed", 200f, "Default walk speed", "Gameplay" );
		Config.HexConfig.Add( "gameplay.runSpeed", 320f, "Default run speed", "Gameplay" );
	}
}

/// <summary>
/// Component that handles gameplay hooks for the skeleton schema.
/// Added to the HexagonFramework GameObject during SkeletonPlugin.OnPluginLoaded().
/// Must be a Component so Scene.GetAll&lt;T&gt;() discovers it for listener interfaces.
/// </summary>
public class SkeletonHooks : Component,
	ICharacterCreatedListener,
	ICharacterLoadedListener,
	IDeathScreenRespawnListener
{
	// --- Character Created ---

	public void OnCharacterCreated( HexPlayerComponent player, HexCharacter character )
	{
		// Create default inventory for the new character
		var inventory = InventoryManager.CreateDefault( character.Id );

		// Give starting cash
		var cash = ItemManager.CreateInstance( "money_cash", character.Id );
		if ( cash != null )
			inventory.Add( cash );

		// Police faction gets extra starting gear
		if ( character.Data.Faction == "police" )
		{
			var baton = ItemManager.CreateInstance( "weapon_baton", character.Id );
			if ( baton != null )
				inventory.Add( baton );

			var uniform = ItemManager.CreateInstance( "outfit_police", character.Id );
			if ( uniform != null )
				inventory.Add( uniform );
		}
	}

	// --- Character Loaded ---

	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Apply movement speeds from config
		var walkSpeed = Config.HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
		var runSpeed = Config.HexConfig.Get<float>( "gameplay.runSpeed", 320f );

		character.SetData( "walkSpeed", walkSpeed );
		character.SetData( "runSpeed", runSpeed );

		// Load and sync inventory to the player
		var inventories = InventoryManager.LoadForCharacter( character.Data.Id );
		foreach ( var inv in inventories )
		{
			inv.AddReceiver( player.Connection );
		}
	}

	// --- Death Screen Respawn ---

	public void OnRespawnRequested( HexPlayerComponent player )
	{
		// Called client-side from the death screen UI — send RPC to server
		ServerRespawn();
	}

	[Rpc.Host]
	private void ServerRespawn()
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || !player.IsDead ) return;

		player.IsDead = false;

		// Teleport to spawn position
		var spawnPos = new Vector3( 0, 0, 100 );
		foreach ( var gm in Scene.GetAll<HexGameManager>() )
		{
			spawnPos = gm.SpawnPosition;
			break;
		}

		player.GameObject.WorldPosition = spawnPos;
	}
}
