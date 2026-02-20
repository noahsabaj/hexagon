namespace Hexagon.Characters;

/// <summary>
/// Manages character CRUD operations and CharVar metadata discovery.
///
/// Converted from a static class to a GameObjectSystem so that per-scene mutable state
/// (_characterLists, _activeCharacters) is scoped to the scene and cleared on scene reload.
/// CharVar metadata (_charVars, _characterDataType) is discovered once and held for the
/// scene's lifetime. All public methods remain static via facades — callers need no changes.
///
/// Auto-save is handled by <see cref="Persistence.AutoSaveSystem"/>.
/// </summary>
public sealed class CharacterManager : GameObjectSystem<CharacterManager>
{
	private static CharacterManager _instance;

	private Dictionary<string, CharVarInfo> _charVars = new();
	private Dictionary<ulong, List<HexCharacterData>> _characterLists = new();
	private Dictionary<string, HexCharacter> _activeCharacters = new();
	private Type _characterDataType;

	public CharacterManager( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	public static event Func<HexPlayerComponent, HexCharacterData, bool> OnCanCharacterCreate;

	public override void Dispose()
	{
		SaveAll();
		_characterLists.Clear();
		_activeCharacters.Clear();
		_charVars.Clear();

		if ( _instance == this )
			_instance = null;

		base.Dispose();
	}

	private static CharacterManager Instance => _instance;

	// --- Initialization (called explicitly before plugins load) ---

	internal static void Initialize()
	{
		// Accessing Instance triggers GameObjectSystem auto-creation if not yet created.
		// InitializeInternal() must run before plugins so CharVars are discovered first.
		if ( Instance == null )
		{
			Log.Warning( "Hexagon: CharacterManager.Initialize() called but instance not yet created." );
			return;
		}

		Instance.InitializeInternal();
	}

	private void InitializeInternal()
	{
		DiscoverCharacterDataType();
		DiscoverCharVars();

		Log.Info( $"Hexagon: CharacterManager initialized. Data type: {_characterDataType?.Name ?? "NONE"}, {_charVars.Count} CharVar(s) discovered." );
	}

	// --- CharVar Discovery ---

	private void DiscoverCharacterDataType()
	{
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

	private void DiscoverCharVars()
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

	// --- Public Static Facade (unchanged signatures) ---

	/// <summary>
	/// All discovered CharVar metadata.
	/// </summary>
	public static IReadOnlyDictionary<string, CharVarInfo> CharVars =>
		(IReadOnlyDictionary<string, CharVarInfo>)Instance?._charVars ?? new Dictionary<string, CharVarInfo>();

	/// <summary>
	/// Get CharVar metadata by property name.
	/// </summary>
	public static CharVarInfo GetCharVarInfo( string name )
	{
		return Instance?._charVars.GetValueOrDefault( name );
	}

	/// <summary>
	/// Get all CharVars that are public (not Local, not NoNetworking).
	/// </summary>
	public static IEnumerable<CharVarInfo> GetPublicCharVars()
	{
		return Instance?._charVars.Values.Where( v => !v.Attribute.Local && !v.Attribute.NoNetworking )
			?? Enumerable.Empty<CharVarInfo>();
	}

	/// <summary>
	/// Get all CharVars that are Local (owner-only networking).
	/// </summary>
	public static IEnumerable<CharVarInfo> GetLocalCharVars()
	{
		return Instance?._charVars.Values.Where( v => v.Attribute.Local && !v.Attribute.NoNetworking )
			?? Enumerable.Empty<CharVarInfo>();
	}

	// --- Player Connection Flow ---

	/// <summary>
	/// Called when a player connects. Loads their character list and auto-loads
	/// their last played character.
	/// </summary>
	internal static void OnPlayerConnected( HexPlayerComponent player )
	{
		if ( Instance == null ) return;

		var steamId = player.SteamId;

		var characters = Persistence.DatabaseManager.Select<HexCharacterData>(
			"characters",
			c => c.SteamId == steamId && !c.IsBanned
		);

		characters.Sort( ( a, b ) => b.LastPlayedAt.CompareTo( a.LastPlayedAt ) );
		Instance._characterLists[steamId] = characters;

		Log.Info( $"Hexagon: Loaded {characters.Count} character(s) for {player.DisplayName}" );

		var autoLoad = Config.HexConfig.Get<bool>( "character.autoLoad", false );

		if ( autoLoad && characters.Count > 0 )
		{
			LoadCharacter( player, characters[0].Id );
		}
		else
		{
			player.GetComponent<CharacterCrudComponent>()?.SendCharacterListToOwner();

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
		if ( Instance == null ) return null;

		if ( OnCanCharacterCreate != null )
		{
			foreach ( Func<HexPlayerComponent, HexCharacterData, bool> handler in OnCanCharacterCreate.GetInvocationList() )
			{
				if ( !handler( player, data ) )
				{
					Log.Warning( $"Hexagon: Character creation blocked for {player.DisplayName}" );
					return null;
				}
			}
		}

		var maxChars = Config.HexConfig.Get<int>( "character.maxPerPlayer", 5 );
		var existingCount = GetCharacterList( player.SteamId ).Count;
		if ( existingCount >= maxChars )
		{
			Log.Warning( $"Hexagon: {player.DisplayName} already has {existingCount}/{maxChars} characters" );
			return null;
		}

		var validationError = ValidateCharacterData( data );
		if ( validationError != null )
		{
			Log.Warning( $"Hexagon: Character validation failed: {validationError}" );
			return null;
		}

		data.Id = Persistence.DatabaseManager.NewId();
		data.SteamId = player.SteamId;
		data.Slot = existingCount;
		data.CreatedAt = DateTime.UtcNow;
		data.LastPlayedAt = DateTime.UtcNow;

		foreach ( var varInfo in Instance._charVars.Values )
		{
			var currentValue = varInfo.GetValue( data );
			if ( currentValue == null && varInfo.Attribute.Default != null )
			{
				varInfo.SetValue( data, varInfo.Attribute.Default );
			}
		}

		Persistence.DatabaseManager.Save( "characters", data.Id, data );

		if ( !Instance._characterLists.ContainsKey( player.SteamId ) )
			Instance._characterLists[player.SteamId] = new();
		Instance._characterLists[player.SteamId].Add( data );

		var character = new HexCharacter( data );

		IHexCharacterEvent.Post( x => x.OnCharacterCreated( player, character ) );
		Factions.LoadoutManager.OnCharacterCreated( player, character );

		Log.Info( $"Hexagon: Character '{data.Id}' created for {player.DisplayName}" );

		return character;
	}

	/// <summary>
	/// Load a character for a player by character ID.
	/// </summary>
	public static bool LoadCharacter( HexPlayerComponent player, string characterId )
	{
		if ( Instance == null ) return false;

		if ( player.Character != null )
		{
			UnloadCharacter( player );
		}

		var data = Persistence.DatabaseManager.Load<HexCharacterData>( "characters", characterId );
		if ( data == null )
		{
			Log.Warning( $"Hexagon: Character '{characterId}' not found" );
			return false;
		}

		if ( data.SteamId != player.SteamId )
		{
			Log.Warning( $"Hexagon: Character '{characterId}' doesn't belong to {player.DisplayName}" );
			return false;
		}

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

		var character = new HexCharacter( data ) { Player = player };
		player.Character = character;

		data.LastPlayedAt = DateTime.UtcNow;

		Instance._activeCharacters[characterId] = character;

		player.SyncPublicData();
		player.SyncPrivateData();

		IHexCharacterEvent.Post( x => x.OnCharacterLoaded( player, character ) );
		Factions.LoadoutManager.OnCharacterLoaded( player, character );

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

		IHexCharacterEvent.Post( x => x.OnCharacterUnloaded( player, character ) );

		character.Save();

		Instance?._activeCharacters.Remove( character.Id );
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
		if ( player.Character?.Id == characterId )
		{
			UnloadCharacter( player );
		}

		var data = Persistence.DatabaseManager.Load<HexCharacterData>( "characters", characterId );
		if ( data == null || data.SteamId != player.SteamId )
			return false;

		Persistence.DatabaseManager.Delete( "characters", characterId );

		if ( Instance?._characterLists.TryGetValue( player.SteamId, out var list ) == true )
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
		return Instance?._characterLists.GetValueOrDefault( steamId ) ?? new();
	}

	/// <summary>
	/// Get an active character by ID.
	/// </summary>
	public static HexCharacter GetActiveCharacter( string characterId )
	{
		return Instance?._activeCharacters.GetValueOrDefault( characterId );
	}

	/// <summary>
	/// Get all active characters.
	/// </summary>
	public static IReadOnlyDictionary<string, HexCharacter> GetActiveCharacters() =>
		(IReadOnlyDictionary<string, HexCharacter>)Instance?._activeCharacters ?? new Dictionary<string, HexCharacter>();

	// --- Validation ---

	/// <summary>
	/// Validate character data against CharVar constraints. Returns null if valid,
	/// or an error message string if invalid.
	/// </summary>
	public static string ValidateCharacterData( HexCharacterData data )
	{
		if ( Instance == null ) return null;

		foreach ( var varInfo in Instance._charVars.Values )
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
		if ( Instance == null ) return;

		var saved = 0;

		foreach ( var character in Instance._activeCharacters.Values )
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
		if ( Instance == null ) return null;

		var data = TypeLibrary.Create<HexCharacterData>( Instance._characterDataType );

		foreach ( var varInfo in Instance._charVars.Values )
		{
			if ( varInfo.Attribute.Default != null )
			{
				varInfo.SetValue( data, varInfo.Attribute.Default );
			}
		}

		return data;
	}
}
