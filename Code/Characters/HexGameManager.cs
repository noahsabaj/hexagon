namespace Hexagon.Characters;

/// <summary>
/// Handles player connections and spawning. Attach this to a GameObject in your scene
/// alongside HexagonFramework.
///
/// When a player connects:
/// 1. Spawns the PlayerPrefab (or a default first-person player if no prefab is assigned)
/// 2. Loads their character list
/// 3. Fires IPlayerConnectedListener
/// 4. Auto-loads their last character (or sends character list to client)
///
/// When no PlayerPrefab is set, the default player includes PlayerController (movement,
/// camera, interaction), a citizen model with Dresser, configured for first-person RP.
/// </summary>
public sealed class HexGameManager : Component, Component.INetworkListener
{
	/// <summary>
	/// Optional prefab to spawn for each connecting player. If null, a default first-person
	/// player is created with PlayerController, citizen model, and Dresser.
	/// If set, must have a HexPlayerComponent (one will be added automatically if missing).
	/// </summary>
	[Property] public GameObject PlayerPrefab { get; set; }

	/// <summary>
	/// World position to spawn players at. Override via IPlayerSpawnListener.
	/// </summary>
	[Property] public Vector3 SpawnPosition { get; set; } = new( 0, 0, 100 );

	/// <summary>
	/// All currently connected players.
	/// </summary>
	public static readonly Dictionary<ulong, HexPlayerComponent> Players = new();

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

	public void OnActive( Connection connection )
	{
		Log.Info( $"Hexagon: Player connecting - {connection.DisplayName} ({connection.SteamId})" );

		// Determine spawn position
		var spawnPos = SpawnPosition;
		spawnPos = HexEvents.Reduce<IPlayerSpawnListener, Vector3>(
			spawnPos, ( listener, pos ) => listener.GetSpawnPosition( connection, pos )
		);

		// Spawn player GameObject
		GameObject playerGo;

		if ( PlayerPrefab != null )
		{
			playerGo = PlayerPrefab.Clone( new Transform( spawnPos ) );
		}
		else
		{
			playerGo = new GameObject( true, $"Player - {connection.DisplayName}" );
			playerGo.WorldPosition = spawnPos;
			HexPlayerSetup.BuildDefaultPlayer( playerGo );
		}

		// Ensure HexPlayerComponent exists
		var player = playerGo.GetOrAddComponent<HexPlayerComponent>();
		player.SteamId = connection.SteamId;
		player.DisplayName = connection.DisplayName;
		player.Connection = connection;

		// Network the player GameObject
		playerGo.NetworkSpawn( connection );

		// Track the player
		Players[connection.SteamId] = player;

		// Fire connection event
		HexEvents.Fire<IPlayerConnectedListener>( x => x.OnPlayerConnected( player, connection ) );

		// Load character data
		CharacterManager.OnPlayerConnected( player );
	}

	public void OnDisconnected( Connection connection )
	{
		Log.Info( $"Hexagon: Player disconnecting - {connection.DisplayName} ({connection.SteamId})" );

		if ( Players.TryGetValue( connection.SteamId, out var player ) )
		{
			// Save and unload character
			if ( player.Character != null )
			{
				CharacterManager.UnloadCharacter( player );
			}

			HexEvents.Fire<IPlayerDisconnectedListener>( x => x.OnPlayerDisconnected( player, connection ) );
			Players.Remove( connection.SteamId );
		}
	}
}

// --- Player lifecycle interfaces ---

/// <summary>
/// Called when a player has fully connected and their GameObject is spawned.
/// </summary>
public interface IPlayerConnectedListener
{
	void OnPlayerConnected( HexPlayerComponent player, Connection connection );
}

/// <summary>
/// Called when a player disconnects.
/// </summary>
public interface IPlayerDisconnectedListener
{
	void OnPlayerDisconnected( HexPlayerComponent player, Connection connection );
}

/// <summary>
/// Override spawn positions for connecting players.
/// </summary>
public interface IPlayerSpawnListener
{
	Vector3 GetSpawnPosition( Connection connection, Vector3 currentPosition );
}

/// <summary>
/// Called when a character is loaded for a player.
/// </summary>
public interface ICharacterLoadedListener
{
	void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character );
}

/// <summary>
/// Called when a character is unloaded (player switches or disconnects).
/// </summary>
public interface ICharacterUnloadedListener
{
	void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character );
}

/// <summary>
/// Permission hook: can a player create a character? Return false to block.
/// </summary>
public interface ICanCharacterCreate
{
	bool CanCharacterCreate( HexPlayerComponent player, HexCharacterData data );
}

/// <summary>
/// Called after a new character is created.
/// </summary>
public interface ICharacterCreatedListener
{
	void OnCharacterCreated( HexPlayerComponent player, HexCharacter character );
}
