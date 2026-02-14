namespace Hexagon.Characters;

/// <summary>
/// Handles player connections and spawning. Auto-added to HexagonFramework.
///
/// When a player connects:
/// 1. Creates a bare networked GameObject with HexPlayerComponent (no body yet)
/// 2. Loads their character list and shows CharacterSelect UI
/// 3. After character selection, builds the full player body (PlayerController, model, etc.)
///
/// The player has no physical presence until they select a character.
/// </summary>
public sealed class HexGameManager : Component, Component.INetworkListener
{
	/// <summary>
	/// Optional prefab to spawn for each player when their character loads.
	/// If null, a default first-person player is built (PlayerController, citizen model, Dresser).
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

		// Determine spawn position (stored for when body is built later)
		var spawnPos = SpawnPosition;
		spawnPos = HexEvents.Reduce<IPlayerSpawnListener, Vector3>(
			spawnPos, ( listener, pos ) => listener.GetSpawnPosition( connection, pos )
		);

		// Create bare networking object — no body until character loads
		var playerGo = new GameObject( true, $"Player - {connection.DisplayName}" );
		playerGo.WorldPosition = spawnPos;

		var player = playerGo.GetOrAddComponent<HexPlayerComponent>();
		player.SteamId = connection.SteamId;
		player.DisplayName = connection.DisplayName;
		player.Connection = connection;

		playerGo.NetworkSpawn( connection );

		Players[connection.SteamId] = player;

		HexEvents.Fire<IPlayerConnectedListener>( x => x.OnPlayerConnected( player, connection ) );

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
