namespace Hexagon.Core;

/// <summary>
/// The core bootstrap component for the Hexagon roleplay framework.
/// Attach this to a persistent GameObject in your scene to initialize all systems.
/// </summary>
public sealed class HexagonFramework : Component
{
	public static HexagonFramework Instance { get; private set; }

	/// <summary>
	/// Whether the framework has finished initialization.
	/// </summary>
	public static bool IsInitialized { get; private set; }

	protected override void OnStart()
	{
		if ( Instance != null && Instance.IsValid() && Instance != this )
		{
			Log.Warning( "HexagonFramework: Duplicate instance detected, destroying." );
			GameObject.Destroy();
			return;
		}

		Instance = this;
		Initialize();
	}

	private void Initialize()
	{
		Log.Info( "Hexagon: Initializing framework..." );

		// Phase 1 - Foundation
		Persistence.DatabaseManager.Initialize();
		Config.DefaultConfigs.Register();
		Config.HexConfig.Initialize();
		PluginManager.Initialize();

		// Phase 2 - Characters & Factions
		Factions.FactionManager.Initialize();
		Characters.CharacterManager.Initialize();
		Characters.RecognitionManager.Initialize();

		// Phase 3 - Items & Inventory
		Items.ItemManager.Initialize();
		Inventory.InventoryManager.Initialize();

		// Phase 4 - Interaction Systems
		Permissions.PermissionManager.Initialize();
		Currency.CurrencyManager.Initialize();
		Chat.ChatManager.Initialize();
		Commands.CommandManager.Initialize();
		Attributes.AttributeManager.Initialize();
		Interaction.ActionBarManager.Initialize();
		GameObject.GetOrAddComponent<Chat.HexChatComponent>();

		// Phase 5 - World Systems
		Logging.HexLog.Initialize();
		Doors.DoorManager.Initialize();
		Vendors.VendorManager.Initialize();

		// Phase 6 - UI Bridge
		GameObject.GetOrAddComponent<Inventory.HexInventoryComponent>();

		// Phase 7 - Auto-setup
		UI.HexUISetup.EnsureUI( Scene );
		GameObject.GetOrAddComponent<Characters.HexModelHandler>();

		IsInitialized = true;
		HexEvents.Fire<IFrameworkInitListener>( x => x.OnFrameworkInit() );

		Log.Info( "Hexagon: Framework initialized." );
	}

	protected override void OnUpdate()
	{
		if ( !IsInitialized ) return;

		// Character auto-save tick
		Characters.CharacterManager.Update();

		// Tick action bar (stared actions, completions)
		Interaction.ActionBarManager.Update();
	}

	protected override void OnDestroy()
	{
		if ( Instance == this )
		{
			Log.Info( "Hexagon: Shutting down framework..." );
			HexEvents.Fire<IFrameworkShutdownListener>( x => x.OnFrameworkShutdown() );
			Doors.DoorManager.SaveAll();
			Inventory.InventoryManager.SaveAll();
			Characters.CharacterManager.SaveAll();
			Config.HexConfig.Save();
			Persistence.DatabaseManager.Shutdown();
			PluginManager.Shutdown();
			Instance = null;
			IsInitialized = false;
		}
	}
}
