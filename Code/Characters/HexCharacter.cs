namespace Hexagon.Characters;

/// <summary>
/// Runtime wrapper around a character's persistent data. Provides gameplay methods,
/// dirty tracking for efficient saves, and networking helpers.
///
/// Created when a player loads a character. One active HexCharacter per player at a time.
/// </summary>
public class HexCharacter
{
	/// <summary>
	/// The underlying persistent data.
	/// </summary>
	public HexCharacterData Data { get; }

	/// <summary>
	/// The player component this character is attached to. Null if character is not active.
	/// </summary>
	public HexPlayerComponent Player { get; internal set; }

	/// <summary>
	/// Whether any data has changed since last save.
	/// </summary>
	public bool IsDirty { get; private set; }

	private readonly HashSet<string> _dirtyFields = new();

	public HexCharacter( HexCharacterData data )
	{
		Data = data;
	}

	// --- Identity ---

	public string Id => Data.Id;
	public ulong SteamId => Data.SteamId;
	public string Faction => Data.Faction;
	public string Class => Data.Class;

	// --- Flags ---

	/// <summary>
	/// Check if this character has a specific flag.
	/// </summary>
	public bool HasFlag( char flag )
	{
		return Data.Flags?.Contains( flag ) ?? false;
	}

	/// <summary>
	/// Check if this character has all specified flags.
	/// </summary>
	public bool HasFlags( string flags )
	{
		if ( string.IsNullOrEmpty( flags ) ) return true;
		return flags.All( f => HasFlag( f ) );
	}

	/// <summary>
	/// Grant a flag to this character.
	/// </summary>
	public void GiveFlag( char flag )
	{
		if ( HasFlag( flag ) ) return;
		Data.Flags += flag;
		MarkDirty( nameof( Data.Flags ) );
	}

	/// <summary>
	/// Remove a flag from this character.
	/// </summary>
	public void TakeFlag( char flag )
	{
		if ( !HasFlag( flag ) ) return;
		Data.Flags = Data.Flags.Replace( flag.ToString(), "" );
		MarkDirty( nameof( Data.Flags ) );
	}

	// --- Generic Data ---

	/// <summary>
	/// Get a value from the character's generic data store.
	/// </summary>
	public T GetData<T>( string key, T defaultValue = default )
	{
		if ( Data.Data == null || !Data.Data.TryGetValue( key, out var value ) )
			return defaultValue;

		try
		{
			return (T)Convert.ChangeType( value, typeof( T ) );
		}
		catch
		{
			return defaultValue;
		}
	}

	/// <summary>
	/// Set a value in the character's generic data store.
	/// </summary>
	public void SetData( string key, object value )
	{
		Data.Data ??= new();
		Data.Data[key] = value;
		MarkDirty( "Data" );
	}

	// --- Character Variable Access ---

	/// <summary>
	/// Get a character variable value by name using reflection.
	/// Prefer using the typed property on your HexCharacterData subclass directly.
	/// </summary>
	public T GetVar<T>( string name, T defaultValue = default )
	{
		var varInfo = CharacterManager.GetCharVarInfo( name );
		if ( varInfo == null ) return defaultValue;

		var value = varInfo.GetValue( Data );
		if ( value == null ) return defaultValue;

		try
		{
			return (T)value;
		}
		catch
		{
			return defaultValue;
		}
	}

	/// <summary>
	/// Set a character variable value by name using reflection.
	/// Prefer using the typed property on your HexCharacterData subclass directly,
	/// then calling MarkDirty().
	/// </summary>
	public void SetVar( string name, object value )
	{
		var varInfo = CharacterManager.GetCharVarInfo( name );
		if ( varInfo == null )
		{
			Log.Warning( $"Hexagon: Unknown CharVar '{name}'" );
			return;
		}

		if ( varInfo.Attribute.ReadOnly )
		{
			Log.Warning( $"Hexagon: CharVar '{name}' is read-only" );
			return;
		}

		varInfo.SetValue( Data, value );
		MarkDirty( name );

		// Sync public vars to all players via the player component
		if ( Player != null && !varInfo.Attribute.NoNetworking && !varInfo.Attribute.Local )
		{
			Player.SyncPublicData();
		}
	}

	// --- Faction/Class ---

	/// <summary>
	/// Set this character's faction.
	/// </summary>
	public void SetFaction( string factionId )
	{
		Data.Faction = factionId;
		Data.Class = null;
		MarkDirty( nameof( Data.Faction ) );
		MarkDirty( nameof( Data.Class ) );
		Player?.SyncPublicData();
	}

	/// <summary>
	/// Set this character's class within their current faction.
	/// </summary>
	public void SetClass( string classId )
	{
		Data.Class = classId;
		MarkDirty( nameof( Data.Class ) );
		Player?.SyncPublicData();
	}

	// --- Ban ---

	/// <summary>
	/// Ban this character. Pass null duration for permanent ban.
	/// </summary>
	public void Ban( TimeSpan? duration = null )
	{
		Data.IsBanned = true;
		Data.BanExpiry = duration.HasValue ? DateTime.UtcNow + duration.Value : null;
		MarkDirty( nameof( Data.IsBanned ) );

		if ( Player != null )
		{
			CharacterManager.UnloadCharacter( Player );
		}
	}

	/// <summary>
	/// Unban this character.
	/// </summary>
	public void Unban()
	{
		Data.IsBanned = false;
		Data.BanExpiry = null;
		MarkDirty( nameof( Data.IsBanned ) );
	}

	// --- Recognition ---

	/// <summary>
	/// Get the set of character IDs this character recognizes.
	/// </summary>
	public HashSet<string> GetRecognizedIds()
	{
		var raw = Data.Data?.GetValueOrDefault( "recognized" );
		if ( raw == null ) return new HashSet<string>();

		var str = raw.ToString();
		if ( string.IsNullOrEmpty( str ) ) return new HashSet<string>();

		return new HashSet<string>( str.Split( ',', StringSplitOptions.RemoveEmptyEntries ) );
	}

	/// <summary>
	/// Add a character ID to this character's recognition list.
	/// </summary>
	public void AddRecognized( string characterId )
	{
		var ids = GetRecognizedIds();
		if ( !ids.Add( characterId ) ) return;

		Data.Data ??= new();
		Data.Data["recognized"] = string.Join( ",", ids );
		MarkDirty( "Data" );
	}

	// --- Persistence ---

	/// <summary>
	/// Mark a field as dirty (changed since last save).
	/// Call this after modifying character data properties directly.
	/// </summary>
	public void MarkDirty( string fieldName = null )
	{
		IsDirty = true;
		if ( fieldName != null )
			_dirtyFields.Add( fieldName );
	}

	/// <summary>
	/// Save this character to the database if dirty.
	/// </summary>
	public void Save()
	{
		if ( !IsDirty ) return;

		Data.LastPlayedAt = DateTime.UtcNow;
		Persistence.DatabaseManager.Save( "characters", Data.Id, Data );

		IsDirty = false;
		_dirtyFields.Clear();
	}

	/// <summary>
	/// Get the list of fields that have changed since last save.
	/// </summary>
	public IReadOnlySet<string> GetDirtyFields() => _dirtyFields;
}
