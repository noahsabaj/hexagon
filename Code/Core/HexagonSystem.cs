namespace Hexagon.Core;

/// <summary>
/// Core Hexagon framework system. Initializes all subsystems on scene startup
/// and manages player connections/disconnections.
///
/// This is a GameObjectSystem (scene-level singleton), following the walker pattern.
/// Place a <see cref="HexagonConfigComponent"/> in your scene to configure spawn settings.
/// </summary>
public sealed class HexagonSystem : GameObjectSystem<HexagonSystem>, Component.INetworkListener, ISceneStartup
{
	private static HexagonSystem _instance;

	/// <summary>
	/// Whether the framework has finished initialization.
	/// </summary>
	public static bool IsInitialized { get; private set; }

	private readonly Dictionary<ulong, HexPlayerComponent> _players = new();

	/// <summary>
	/// All currently connected players keyed by Steam ID.
	/// </summary>
	public static IReadOnlyDictionary<ulong, HexPlayerComponent> Players =>
		(IReadOnlyDictionary<ulong, HexPlayerComponent>)_instance?._players ?? new Dictionary<ulong, HexPlayerComponent>();

	public HexagonSystem( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	/// <summary>
	/// Get a player component by Steam ID.
	/// </summary>
	public static HexPlayerComponent GetPlayer( ulong steamId )
	{
		return _instance?._players.GetValueOrDefault( steamId );
	}

	/// <summary>
	/// Get a player component by Connection.
	/// </summary>
	public static HexPlayerComponent GetPlayer( Connection connection )
	{
		return _instance?._players.GetValueOrDefault( connection.SteamId );
	}

	// --- Initialization ---

	void ISceneStartup.OnHostInitialize()
	{
		Log.Info( "Hexagon: Initializing framework..." );

		// Foundation — database must come first (everything else persists through it)
		Persistence.DatabaseManager.Initialize();

		// Configuration — load saved overrides before plugins register their keys
		Config.DefaultConfigs.Register();
		Config.HexConfig.Initialize();

		// ConVar bridge — after HexConfig.Initialize() so saved overrides are already loaded
		Config.ConVarBridge.Initialize();

		// Plugins — before CharacterManager so plugins can register their HexCharacterData
		PluginManager.Initialize();

		// CharacterManager depends on plugin registrations being complete
		Characters.CharacterManager.Initialize();

		// UI
		UI.HexUISetup.EnsureUI( Scene );

		IsInitialized = true;
		IHexFrameworkEvent.Post( x => x.OnFrameworkInit() );

		Log.Info( "Hexagon: Framework initialized." );
	}

	// --- Player Connection ---

	void Component.INetworkListener.OnActive( Connection connection )
	{
		Log.Info( $"Hexagon: Player connecting - {connection.DisplayName} ({connection.SteamId})" );

		var config = Scene.GetAll<HexagonConfigComponent>().FirstOrDefault();
		var spawnPos = config?.SpawnPosition ?? new Vector3( 0, 0, 100 );

		spawnPos = SceneEventExtensions.Reduce<IHexPlayerEvent, Vector3>(
			spawnPos, ( listener, pos ) => listener.GetSpawnPosition( connection, pos )
		);

		// Create bare networking object — no body until character loads
		var playerGo = new GameObject( true, $"Player - {connection.DisplayName}" );
		playerGo.WorldPosition = spawnPos;

		var player = playerGo.GetOrAddComponent<HexPlayerComponent>();
		playerGo.GetOrAddComponent<Characters.CharacterCrudComponent>();
		playerGo.GetOrAddComponent<Interaction.ActionBarComponent>();
		playerGo.GetOrAddComponent<Interaction.ActionBarPlayerComponent>();
		playerGo.GetOrAddComponent<Characters.RecognitionPlayerComponent>();
		playerGo.GetOrAddComponent<Characters.IntroducePlayerComponent>();
		playerGo.GetOrAddComponent<UI.NotificationPlayerComponent>();

		player.SteamId = connection.SteamId;
		player.DisplayName = connection.DisplayName;
		player.Connection = connection;

		playerGo.NetworkSpawn( connection );

		_players[connection.SteamId] = player;

		IHexPlayerEvent.Post( x => x.OnPlayerConnected( player, connection ) );

		Characters.CharacterManager.OnPlayerConnected( player );
	}

	void Component.INetworkListener.OnDisconnected( Connection connection )
	{
		Log.Info( $"Hexagon: Player disconnecting - {connection.DisplayName} ({connection.SteamId})" );

		if ( _players.TryGetValue( connection.SteamId, out var player ) )
		{
			if ( player.Character != null )
			{
				Characters.CharacterManager.UnloadCharacter( player );
			}

			IHexPlayerEvent.Post( x => x.OnPlayerDisconnected( player, connection ) );
			_players.Remove( connection.SteamId );
		}
	}

	// --- Shutdown ---

	public override void Dispose()
	{
		if ( IsInitialized )
		{
			Log.Info( "Hexagon: Shutting down framework..." );
			IHexFrameworkEvent.Post( x => x.OnFrameworkShutdown() );

			// Save all registered doors (each door manages its own persistence)
			foreach ( var door in Doors.DoorManager.GetAllDoors()?.Values ?? Enumerable.Empty<Doors.DoorComponent>() )
				door.SaveData();

			// Explicit save order: inventories and characters must complete before DB cache clears
			Inventory.InventoryManager.SaveAll();
			Characters.CharacterManager.SaveAll();
			Config.HexConfig.Save();

			// Clean up ConVar bridge event subscription
			Config.ConVarBridge.Shutdown();

			// Database shutdown — explicit here so it runs after saves above.
			// PluginManager and WorldItemManager clean up via their own Dispose().
			Persistence.DatabaseManager.Shutdown();

			IsInitialized = false;
			_players.Clear();
		}

		if ( _instance == this ) _instance = null;
		base.Dispose();
	}
}
