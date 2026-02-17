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
	/// <summary>
	/// Whether the framework has finished initialization.
	/// </summary>
	public static bool IsInitialized { get; private set; }

	/// <summary>
	/// All currently connected players keyed by Steam ID.
	/// </summary>
	public static readonly Dictionary<ulong, HexPlayerComponent> Players = new();

	public HexagonSystem( Scene scene ) : base( scene )
	{
	}

	/// <summary>
	/// Get a player component by Steam ID.
	/// </summary>
	public static HexPlayerComponent GetPlayer( ulong steamId )
	{
		return Players.GetValueOrDefault( steamId );
	}

	/// <summary>
	/// Get a player component by Connection.
	/// </summary>
	public static HexPlayerComponent GetPlayer( Connection connection )
	{
		return Players.GetValueOrDefault( connection.SteamId );
	}

	// --- Initialization ---

	void ISceneStartup.OnHostInitialize()
	{
		Log.Info( "Hexagon: Initializing framework..." );

		// Foundation
		Persistence.DatabaseManager.Initialize();
		Config.DefaultConfigs.Register();
		Config.HexConfig.Initialize();
		PluginManager.Initialize();

		// Systems with required initialization
		Characters.CharacterManager.Initialize();
		Permissions.PermissionManager.Initialize();
		Chat.ChatManager.Initialize();
		Commands.CommandManager.Initialize();

		// Service components
		var servicesGo = new GameObject( true, "Hexagon Services" );
		servicesGo.GetOrAddComponent<Chat.HexChatComponent>();
		servicesGo.GetOrAddComponent<Inventory.HexInventoryComponent>();
		servicesGo.GetOrAddComponent<Characters.HexModelHandler>();

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

		Players[connection.SteamId] = player;

		IHexPlayerEvent.Post( x => x.OnPlayerConnected( player, connection ) );

		Characters.CharacterManager.OnPlayerConnected( player );
	}

	void Component.INetworkListener.OnDisconnected( Connection connection )
	{
		Log.Info( $"Hexagon: Player disconnecting - {connection.DisplayName} ({connection.SteamId})" );

		if ( Players.TryGetValue( connection.SteamId, out var player ) )
		{
			if ( player.Character != null )
			{
				Characters.CharacterManager.UnloadCharacter( player );
			}

			IHexPlayerEvent.Post( x => x.OnPlayerDisconnected( player, connection ) );
			Players.Remove( connection.SteamId );
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
			foreach ( var door in Doors.DoorManager.GetAllDoors().Values )
				door.SaveData();

			Inventory.InventoryManager.SaveAll();
			Characters.CharacterManager.SaveAll();
			Config.HexConfig.Save();
			Persistence.DatabaseManager.Shutdown();
			PluginManager.Shutdown();
			IsInitialized = false;
			Players.Clear();
		}

		base.Dispose();
	}
}
