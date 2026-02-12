namespace Hexagon.Characters;

/// <summary>
/// Manages character CRUD operations, auto-save, and CharVar metadata discovery.
/// Initialized by HexagonFramework on startup.
/// </summary>
public static class CharacterManager
{
	private static readonly Dictionary<string, CharVarInfo> _charVars = new();
	private static readonly Dictionary<ulong, List<HexCharacterData>> _characterLists = new();
	private static readonly Dictionary<string, HexCharacter> _activeCharacters = new();

	private static Type _characterDataType;
	private static TimeUntil _nextAutoSave;

	/// <summary>
	/// All discovered CharVar metadata.
	/// </summary>
	public static IReadOnlyDictionary<string, CharVarInfo> CharVars => _charVars;

	internal static void Initialize()
	{
		DiscoverCharacterDataType();
		DiscoverCharVars();

		_nextAutoSave = Config.HexConfig.Get<float>( "framework.saveInterval", 300f );

		Log.Info( $"Hexagon: CharacterManager initialized. Data type: {_characterDataType?.Name ?? "NONE"}, {_charVars.Count} CharVar(s) discovered." );
	}

	/// <summary>
	/// Called every frame by the framework to handle auto-save.
	/// </summary>
	internal static void Update()
	{
		if ( _nextAutoSave <= 0 )
		{
			SaveAll();
			_nextAutoSave = Config.HexConfig.Get<float>( "framework.saveInterval", 300f );
		}
	}

	// --- CharVar Discovery ---

	private static void DiscoverCharacterDataType()
	{
		// Find the concrete subclass of HexCharacterData defined by the schema
		var types = TypeLibrary.GetTypes<HexCharacterData>()
			.Where( t => !t.IsAbstract && t.TargetType != typeof( HexCharacterData ) )
			.ToList();

		if ( types.Count == 0 )
		{
			Log.Warning( "Hexagon: No HexCharacterData subclass found. Define one in your schema!" );
			_characterDataType = typeof( HexCharacterData );
			return;
		}

		if ( types.Count > 1 )
		{
			Log.Warning( $"Hexagon: Multiple HexCharacterData subclasses found: {string.Join( ", ", types.Select( t => t.Name ) )}. Using first." );
		}

		_characterDataType = types[0].TargetType;
	}

	private static void DiscoverCharVars()
	{
		_charVars.Clear();

		if ( _characterDataType == null ) return;

		var typeDesc = TypeLibrary.GetType( _characterDataType );
		if ( typeDesc == null ) return;

		foreach ( var prop in typeDesc.Properties )
		{
			var attr = prop.GetCustomAttribute<CharVarAttribute>();
			if ( attr == null ) continue;

			_charVars[prop.Name] = new CharVarInfo
			{
				Name = prop.Name,
				PropertyType = prop.PropertyType,
				Attribute = attr,
				Property = prop
			};
		}
	}

	/// <summary>
	/// Get CharVar metadata by property name.
	/// </summary>
	public static CharVarInfo GetCharVarInfo( string name )
	{
		return _charVars.GetValueOrDefault( name );
	}

	/// <summary>
	/// Get all CharVars that are public (not Local, not NoNetworking).
	/// </summary>
	public static IEnumerable<CharVarInfo> GetPublicCharVars()
	{
		return _charVars.Values.Where( v => !v.Attribute.Local && !v.Attribute.NoNetworking );
	}

	/// <summary>
	/// Get all CharVars that are Local (owner-only networking).
	/// </summary>
	public static IEnumerable<CharVarInfo> GetLocalCharVars()
	{
		return _charVars.Values.Where( v => v.Attribute.Local && !v.Attribute.NoNetworking );
	}

	// --- Player Connection Flow ---

	/// <summary>
	/// Called when a player connects. Loads their character list and auto-loads
	/// their last played character.
	/// </summary>
	internal static void OnPlayerConnected( HexPlayerComponent player )
	{
		var steamId = player.SteamId;

		// Load all characters for this player
		var characters = Persistence.DatabaseManager.Select<HexCharacterData>(
			"characters",
			c => c.SteamId == steamId && !c.IsBanned
		);

		// Sort by last played, cast to correct type if needed
		characters.Sort( ( a, b ) => b.LastPlayedAt.CompareTo( a.LastPlayedAt ) );
		_characterLists[steamId] = characters;

		Log.Info( $"Hexagon: Loaded {characters.Count} character(s) for {player.DisplayName}" );

		var autoLoad = Config.HexConfig.Get<bool>( "character.autoLoad", false );

		if ( autoLoad && characters.Count > 0 )
		{
			// Auto-load the most recently played character
			LoadCharacter( player, characters[0].Id );
		}
		else
		{
			// Send character list to client for selection UI
			player.SendCharacterListToOwner();

			if ( characters.Count == 0 )
				Log.Info( $"Hexagon: No characters found for {player.DisplayName}. Awaiting character creation." );
		}
	}

	// --- CRUD Operations ---

	/// <summary>
	/// Create a new character for a player.
	/// </summary>
	public static HexCharacter CreateCharacter( HexPlayerComponent player, HexCharacterData data )
	{
		// Permission check
		if ( !HexEvents.CanAll<ICanCharacterCreate>( x => x.CanCharacterCreate( player, data ) ) )
		{
			Log.Warning( $"Hexagon: Character creation blocked for {player.DisplayName}" );
			return null;
		}

		// Check max characters
		var maxChars = Config.HexConfig.Get<int>( "character.maxPerPlayer", 5 );
		var existingCount = GetCharacterList( player.SteamId ).Count;
		if ( existingCount >= maxChars )
		{
			Log.Warning( $"Hexagon: {player.DisplayName} already has {existingCount}/{maxChars} characters" );
			return null;
		}

		// Validate CharVars
		var validationError = ValidateCharacterData( data );
		if ( validationError != null )
		{
			Log.Warning( $"Hexagon: Character validation failed: {validationError}" );
			return null;
		}

		// Assign metadata
		data.Id = Persistence.DatabaseManager.NewId();
		data.SteamId = player.SteamId;
		data.Slot = existingCount;
		data.CreatedAt = DateTime.UtcNow;
		data.LastPlayedAt = DateTime.UtcNow;

		// Apply defaults for unset CharVars
		foreach ( var varInfo in _charVars.Values )
		{
			var currentValue = varInfo.GetValue( data );
			if ( currentValue == null && varInfo.Attribute.Default != null )
			{
				varInfo.SetValue( data, varInfo.Attribute.Default );
			}
		}

		// Save to DB
		Persistence.DatabaseManager.Save( "characters", data.Id, data );

		// Add to local list
		if ( !_characterLists.ContainsKey( player.SteamId ) )
			_characterLists[player.SteamId] = new();
		_characterLists[player.SteamId].Add( data );

		// Create runtime wrapper
		var character = new HexCharacter( data );

		// Fire event
		HexEvents.Fire<ICharacterCreatedListener>( x => x.OnCharacterCreated( player, character ) );

		Log.Info( $"Hexagon: Character '{data.Id}' created for {player.DisplayName}" );

		return character;
	}

	/// <summary>
	/// Load a character for a player by character ID.
	/// </summary>
	public static bool LoadCharacter( HexPlayerComponent player, string characterId )
	{
		// Unload current character if any
		if ( player.Character != null )
		{
			UnloadCharacter( player );
		}

		// Find character data
		var data = Persistence.DatabaseManager.Load<HexCharacterData>( "characters", characterId );
		if ( data == null )
		{
			Log.Warning( $"Hexagon: Character '{characterId}' not found" );
			return false;
		}

		// Verify ownership
		if ( data.SteamId != player.SteamId )
		{
			Log.Warning( $"Hexagon: Character '{characterId}' doesn't belong to {player.DisplayName}" );
			return false;
		}

		// Check ban
		if ( data.IsBanned )
		{
			if ( data.BanExpiry.HasValue && data.BanExpiry.Value < DateTime.UtcNow )
			{
				data.IsBanned = false;
				data.BanExpiry = null;
			}
			else
			{
				Log.Warning( $"Hexagon: Character '{characterId}' is banned" );
				return false;
			}
		}

		// Create runtime wrapper
		var character = new HexCharacter( data ) { Player = player };
		player.Character = character;

		// Update last played
		data.LastPlayedAt = DateTime.UtcNow;

		// Track active character
		_activeCharacters[characterId] = character;

		// Sync networked data
		player.SyncPublicData();
		player.SyncPrivateData();

		// Fire event
		HexEvents.Fire<ICharacterLoadedListener>( x => x.OnCharacterLoaded( player, character ) );

		Log.Info( $"Hexagon: Character loaded for {player.DisplayName} (faction: {data.Faction ?? "none"})" );

		return true;
	}

	/// <summary>
	/// Unload the active character from a player (save + disconnect).
	/// </summary>
	public static void UnloadCharacter( HexPlayerComponent player )
	{
		var character = player.Character;
		if ( character == null ) return;

		// Fire event before unloading
		HexEvents.Fire<ICharacterUnloadedListener>( x => x.OnCharacterUnloaded( player, character ) );

		// Save
		character.Save();

		// Clean up
		_activeCharacters.Remove( character.Id );
		character.Player = null;
		player.Character = null;
		player.HasActiveCharacter = false;
		player.CharacterName = "";
		player.CharacterModel = "";
		player.FactionId = "";
		player.ClassId = "";

		Log.Info( $"Hexagon: Character unloaded for {player.DisplayName}" );
	}

	/// <summary>
	/// Delete a character permanently.
	/// </summary>
	public static bool DeleteCharacter( HexPlayerComponent player, string characterId )
	{
		// Must not be the active character
		if ( player.Character?.Id == characterId )
		{
			UnloadCharacter( player );
		}

		// Verify ownership
		var data = Persistence.DatabaseManager.Load<HexCharacterData>( "characters", characterId );
		if ( data == null || data.SteamId != player.SteamId )
			return false;

		// Remove from DB
		Persistence.DatabaseManager.Delete( "characters", characterId );

		// Remove from local list
		if ( _characterLists.TryGetValue( player.SteamId, out var list ) )
		{
			list.RemoveAll( c => c.Id == characterId );
		}

		Log.Info( $"Hexagon: Character '{characterId}' deleted for {player.DisplayName}" );
		return true;
	}

	// --- Queries ---

	/// <summary>
	/// Get the character list for a player by Steam ID.
	/// </summary>
	public static List<HexCharacterData> GetCharacterList( ulong steamId )
	{
		return _characterLists.GetValueOrDefault( steamId ) ?? new();
	}

	/// <summary>
	/// Get an active character by ID.
	/// </summary>
	public static HexCharacter GetActiveCharacter( string characterId )
	{
		return _activeCharacters.GetValueOrDefault( characterId );
	}

	/// <summary>
	/// Get all active characters.
	/// </summary>
	public static IReadOnlyDictionary<string, HexCharacter> GetActiveCharacters() => _activeCharacters;

	// --- Validation ---

	/// <summary>
	/// Validate character data against CharVar constraints. Returns null if valid,
	/// or an error message string if invalid.
	/// </summary>
	public static string ValidateCharacterData( HexCharacterData data )
	{
		foreach ( var varInfo in _charVars.Values )
		{
			var value = varInfo.GetValue( data );

			if ( value is string str )
			{
				if ( varInfo.Attribute.MinLength > 0 && str.Length < varInfo.Attribute.MinLength )
					return $"{varInfo.Name} must be at least {varInfo.Attribute.MinLength} characters";

				if ( varInfo.Attribute.MaxLength > 0 && str.Length > varInfo.Attribute.MaxLength )
					return $"{varInfo.Name} must be at most {varInfo.Attribute.MaxLength} characters";
			}
		}

		return null;
	}

	// --- Auto-Save ---

	/// <summary>
	/// Save all active characters that have dirty data.
	/// </summary>
	public static void SaveAll()
	{
		var saved = 0;

		foreach ( var character in _activeCharacters.Values )
		{
			if ( character.IsDirty )
			{
				character.Save();
				saved++;
			}
		}

		if ( saved > 0 )
			Log.Info( $"Hexagon: Auto-saved {saved} character(s)." );
	}

	/// <summary>
	/// Create an instance of the schema's character data type with defaults applied.
	/// </summary>
	public static HexCharacterData CreateDefaultData()
	{
		var data = TypeLibrary.Create<HexCharacterData>( _characterDataType );

		foreach ( var varInfo in _charVars.Values )
		{
			if ( varInfo.Attribute.Default != null )
			{
				varInfo.SetValue( data, varInfo.Attribute.Default );
			}
		}

		return data;
	}
}
