namespace Hexagon.Characters;

/// <summary>
/// Lightweight DTO for character list sent to clients.
/// </summary>
public class CharacterListEntry
{
	public string Id { get; set; }
	public string Name { get; set; }
	public string Description { get; set; }
	public string Faction { get; set; }
	public string Class { get; set; }
	public DateTime LastPlayed { get; set; }
}

/// <summary>
/// Component attached to each player's GameObject. Holds the networked character data
/// that other players need to see, plus a server-side reference to the full HexCharacter.
///
/// Public data ([Sync]): visible to all players (name, model, faction).
/// Private data: synced only to the owner via RPC when it changes.
/// </summary>
public sealed class HexPlayerComponent : Component
{
	// --- Player Identity (synced to all) ---

	[Sync] public ulong SteamId { get; set; }
	[Sync] public string DisplayName { get; set; }

	// --- Public Character Data (synced to all) ---

	[Sync] public string CharacterName { get; set; } = "";
	[Sync] public string CharacterModel { get; set; } = "";
	[Sync] public string CharacterDescription { get; set; } = "";
	[Sync] public string FactionId { get; set; } = "";
	[Sync] public string ClassId { get; set; } = "";
	[Sync] public string CharacterId { get; set; } = "";
	[Sync] public bool HasActiveCharacter { get; set; }
	[Sync] public bool IsDead { get; set; }

	// --- Server-Side Only (not networked) ---

	/// <summary>
	/// The active character for this player. Only valid on the server.
	/// </summary>
	public HexCharacter Character { get; internal set; }

	/// <summary>
	/// The network connection for this player.
	/// </summary>
	public Connection Connection { get; internal set; }

	// --- Client-Side State ---

	/// <summary>
	/// Client-side character list received from the server.
	/// </summary>
	public List<CharacterListEntry> ClientCharacterList { get; private set; } = new();

	/// <summary>
	/// Fired on the client when the character list is received from the server.
	/// </summary>
	public event Action OnCharacterListReceived;

	/// <summary>
	/// Fired on the client when a character creation result is received.
	/// </summary>
	public event Action<bool, string> OnCharacterCreateResult;

	// --- Action Bar Client State ---

	/// <summary>
	/// Client-side: start time of the current action bar.
	/// </summary>
	public float ActionStartTime { get; private set; }

	/// <summary>
	/// Client-side: end time of the current action bar.
	/// </summary>
	public float ActionEndTime { get; private set; }

	/// <summary>
	/// Client-side: text label for the current action bar.
	/// </summary>
	public string ActionText { get; private set; } = "";

	/// <summary>
	/// Client-side: fired when action bar state changes.
	/// </summary>
	public event Action OnActionBarChanged;

	// --- Recognition Client State ---

	private HashSet<string> _recognizedIds = new();
	private bool _recognitionDataReceived;

	// --- Server-bound RPCs (client calls these) ---

	/// <summary>
	/// Client requests their character list from the server.
	/// </summary>
	[Rpc.Host]
	public void RequestCharacterList()
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || player != this ) return;

		SendCharacterListToOwner();
	}

	/// <summary>
	/// Client requests to load a specific character.
	/// </summary>
	[Rpc.Host]
	public void RequestLoadCharacter( string characterId )
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || player != this ) return;

		if ( string.IsNullOrEmpty( characterId ) ) return;

		var success = CharacterManager.LoadCharacter( player, characterId );
		if ( !success )
		{
			ReceiveCharacterCreateResult( false, "Failed to load character." );
		}
	}

	/// <summary>
	/// Client requests to create a new character from JSON data.
	/// </summary>
	[Rpc.Host]
	public void RequestCreateCharacter( string json )
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || player != this ) return;

		if ( string.IsNullOrEmpty( json ) )
		{
			ReceiveCharacterCreateResult( false, "Invalid character data." );
			return;
		}

		try
		{
			// Create a default data instance
			var data = CharacterManager.CreateDefaultData();

			// Deserialize the client-supplied values
			var values = Json.Deserialize<Dictionary<string, object>>( json );
			if ( values == null )
			{
				ReceiveCharacterCreateResult( false, "Invalid character data." );
				return;
			}

			// Apply values to CharVars
			foreach ( var kvp in values )
			{
				if ( kvp.Key == "Faction" )
				{
					data.Faction = kvp.Value?.ToString();
					continue;
				}

				if ( kvp.Key == "Class" )
				{
					data.Class = kvp.Value?.ToString();
					continue;
				}

				var varInfo = CharacterManager.GetCharVarInfo( kvp.Key );
				if ( varInfo == null ) continue;
				if ( !varInfo.Attribute.ShowInCreation ) continue;

				if ( varInfo.PropertyType == typeof( string ) )
				{
					varInfo.SetValue( data, kvp.Value?.ToString() ?? "" );
				}
				else if ( varInfo.PropertyType == typeof( int ) )
				{
					if ( int.TryParse( kvp.Value?.ToString(), out var intVal ) )
						varInfo.SetValue( data, intVal );
				}
				else
				{
					varInfo.SetValue( data, kvp.Value );
				}
			}

			// Create the character
			var character = CharacterManager.CreateCharacter( player, data );
			if ( character == null )
			{
				ReceiveCharacterCreateResult( false, "Character creation failed. Check server logs." );
				return;
			}

			ReceiveCharacterCreateResult( true, "Character created successfully." );
			SendCharacterListToOwner();
		}
		catch ( Exception ex )
		{
			Log.Error( $"Hexagon: RequestCreateCharacter error: {ex}" );
			ReceiveCharacterCreateResult( false, "An error occurred during character creation." );
		}
	}

	/// <summary>
	/// Client requests to delete a character.
	/// </summary>
	[Rpc.Host]
	public void RequestDeleteCharacter( string characterId )
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || player != this ) return;

		if ( string.IsNullOrEmpty( characterId ) ) return;

		var success = CharacterManager.DeleteCharacter( player, characterId );
		if ( success )
		{
			SendCharacterListToOwner();
		}
	}

	// --- Client-bound RPCs (server calls these) ---

	/// <summary>
	/// Server sends the character list to the owning client.
	/// </summary>
	[Rpc.Owner]
	private void ReceiveCharacterList( string json )
	{
		try
		{
			ClientCharacterList = Json.Deserialize<List<CharacterListEntry>>( json ) ?? new();
		}
		catch
		{
			ClientCharacterList = new();
		}

		OnCharacterListReceived?.Invoke();
	}

	/// <summary>
	/// Server sends the result of a character creation attempt to the owning client.
	/// </summary>
	[Rpc.Owner]
	private void ReceiveCharacterCreateResult( bool success, string message )
	{
		OnCharacterCreateResult?.Invoke( success, message );
	}

	// --- Server-side helper ---

	/// <summary>
	/// Build and send the character list to the owning client.
	/// </summary>
	internal void SendCharacterListToOwner()
	{
		var characters = CharacterManager.GetCharacterList( SteamId );
		var entries = characters.Select( c =>
		{
			// Read name from CharVar
			var nameInfo = CharacterManager.GetCharVarInfo( "Name" );
			var descInfo = CharacterManager.GetCharVarInfo( "Description" );

			return new CharacterListEntry
			{
				Id = c.Id,
				Name = nameInfo?.GetValue( c )?.ToString() ?? "Unknown",
				Description = descInfo?.GetValue( c )?.ToString() ?? "",
				Faction = c.Faction ?? "",
				Class = c.Class ?? "",
				LastPlayed = c.LastPlayedAt
			};
		} ).ToList();

		var json = Json.Serialize( entries );
		ReceiveCharacterList( json );
	}

	// --- Existing Sync Logic ---

	/// <summary>
	/// Push the current character's public data to the [Sync] properties.
	/// Called when character data changes.
	/// </summary>
	internal void SyncPublicData()
	{
		if ( IsProxy ) return;
		if ( Character?.Data == null )
		{
			HasActiveCharacter = false;
			return;
		}

		HasActiveCharacter = true;
		CharacterId = Character.Id;
		FactionId = Character.Data.Faction ?? "";
		ClassId = Character.Data.Class ?? "";

		// Sync CharVar properties that are public (not Local, not NoNetworking)
		foreach ( var varInfo in CharacterManager.GetPublicCharVars() )
		{
			var value = varInfo.GetValue( Character.Data )?.ToString() ?? "";

			switch ( varInfo.Name.ToLower() )
			{
				case "name":
					CharacterName = value;
					break;
				case "description":
					CharacterDescription = value;
					break;
				case "model":
					CharacterModel = value;
					break;
			}
		}
	}

	/// <summary>
	/// Send private character data to the owning player.
	/// Called when local-only CharVar values change.
	/// </summary>
	internal void SyncPrivateData()
	{
		if ( IsProxy ) return;
		if ( Character?.Data == null ) return;

		var privateVars = new Dictionary<string, string>();

		foreach ( var varInfo in CharacterManager.GetLocalCharVars() )
		{
			var value = varInfo.GetValue( Character.Data );
			privateVars[varInfo.Name] = value != null ? Json.Serialize( value ) : "";
		}

		// Also send flags
		privateVars["Flags"] = Character.Data.Flags ?? "";

		ReceivePrivateData( Json.Serialize( privateVars ) );

		// Sync recognition data
		RecognitionManager.SyncRecognitionToClient( this );
	}

	[Rpc.Owner]
	private void ReceivePrivateData( string json )
	{
		_privateData = Json.Deserialize<Dictionary<string, string>>( json );
	}

	private Dictionary<string, string> _privateData = new();

	/// <summary>
	/// Client-side: get a private CharVar value that was synced from the server.
	/// </summary>
	public T GetPrivateVar<T>( string name, T defaultValue = default )
	{
		if ( !_privateData.TryGetValue( name, out var json ) || string.IsNullOrEmpty( json ) )
			return defaultValue;

		try
		{
			return Json.Deserialize<T>( json );
		}
		catch
		{
			return defaultValue;
		}
	}

	// --- Action Bar RPCs ---

	/// <summary>
	/// Server sends action bar state to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveActionBar( float startTime, float endTime, string text )
	{
		ActionStartTime = startTime;
		ActionEndTime = endTime;
		ActionText = text;
		OnActionBarChanged?.Invoke();
	}

	/// <summary>
	/// Server clears the action bar on the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveActionBarReset()
	{
		ActionStartTime = 0;
		ActionEndTime = 0;
		ActionText = "";
		OnActionBarChanged?.Invoke();
	}

	// --- Recognition RPCs ---

	/// <summary>
	/// Server sends updated recognition data to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveRecognitionData( string json )
	{
		try
		{
			_recognizedIds = Json.Deserialize<HashSet<string>>( json ) ?? new();
		}
		catch
		{
			_recognizedIds = new();
		}

		_recognitionDataReceived = true;
	}

	/// <summary>
	/// Client-side: check if this player recognizes a target player.
	/// </summary>
	public bool DoesRecognizeLocal( HexPlayerComponent target )
	{
		if ( target == null || !target.HasActiveCharacter ) return true;
		if ( target == this ) return true;

		// If recognition data was never synced, feature is likely disabled
		if ( !_recognitionDataReceived ) return true;

		// Check if target's faction is globally recognized
		if ( !string.IsNullOrEmpty( target.FactionId ) )
		{
			var faction = Factions.FactionManager.GetFaction( target.FactionId );
			if ( faction != null && faction.IsGloballyRecognized )
				return true;
		}

		return _recognizedIds.Contains( target.CharacterId );
	}

	// --- Introduce RPC ---

	/// <summary>
	/// Client requests to introduce themselves. Level: 0=look-at, 1=whisper, 2=talk, 3=yell.
	/// </summary>
	[Rpc.Host]
	public void RequestIntroduce( int level )
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null || player != this ) return;

		if ( player.Character == null )
		{
			UI.NotificationManager.Send( player, "You have no active character." );
			return;
		}

		switch ( level )
		{
			case 0:
			{
				// Look-at target
				var pc = player.GameObject.GetComponent<PlayerController>();
				if ( pc == null ) break;

				var from = pc.EyePosition;
				var to = from + pc.EyeAngles.Forward * 200f;
				var tr = player.Scene.Trace.Ray( from, to )
					.IgnoreGameObjectHierarchy( player.GameObject )
					.Run();

				if ( tr.Hit && tr.GameObject != null )
				{
					var targetPlayer = tr.GameObject.GetComponent<HexPlayerComponent>();
					if ( targetPlayer != null && targetPlayer.Character != null )
					{
						if ( RecognitionManager.IntroduceToTarget( player, targetPlayer ) )
							UI.NotificationManager.Send( player, "You introduced yourself." );
						else
							UI.NotificationManager.Send( player, "They already know who you are." );
					}
					else
					{
						UI.NotificationManager.Send( player, "You must be looking at a player." );
					}
				}
				else
				{
					UI.NotificationManager.Send( player, "You must be looking at a player." );
				}
				break;
			}
			case 1:
			{
				var range = Config.HexConfig.Get<float>( "chat.whisperRange", 100f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
			case 2:
			{
				var range = Config.HexConfig.Get<float>( "chat.icRange", 300f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
			case 3:
			{
				var range = Config.HexConfig.Get<float>( "chat.yellRange", 600f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
		}
	}

	// --- Notification RPC ---

	/// <summary>
	/// Server sends a toast notification to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveNotification( string message, float duration )
	{
		HexEvents.Fire<UI.INotificationReceivedListener>(
			x => x.OnNotificationReceived( message, duration ) );
	}

	// --- Lifecycle ---

	protected override void OnDestroy()
	{
		// Clean up action bar
		Interaction.ActionBarManager.RemovePlayer( SteamId );

		// Save character on player disconnect
		if ( !IsProxy && Character != null )
		{
			Character.Save();
			Character.Player = null;
			Character = null;
		}
	}
}
