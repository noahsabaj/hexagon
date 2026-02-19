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

	/// <summary>
	/// Typed accessor for this character's attribute values.
	/// Equivalent to calling AttributeManager directly, but with nicer syntax.
	/// </summary>
	public Attributes.AttributeSet Attributes { get; }

	public HexCharacter( HexCharacterData data )
	{
		Data = data;
		Attributes = new Attributes.AttributeSet( this );
	}

	// --- Identity ---

	public string Id => Data.Id;
	public ulong SteamId => Data.SteamId;
	public string Faction => Data.Faction;
	public string Class => Data.Class;

	// --- Flags ---

	/// <summary>
	/// Check if this character has a specific permission flag.
	/// </summary>
	public bool HasFlag( string flag )
	{
		return Data.Flags.Contains( flag );
	}

	/// <summary>
	/// Check if this character has all flags represented by each character in the string
	/// (e.g. HasFlags("as") checks for both "a" and "s").
	/// </summary>
	public bool HasFlags( string flags )
	{
		if ( string.IsNullOrEmpty( flags ) ) return true;
		return flags.All( f => HasFlag( f.ToString() ) );
	}

	/// <summary>
	/// Grant a permission flag to this character.
	/// </summary>
	public void GiveFlag( string flag )
	{
		if ( !Data.Flags.Add( flag ) ) return;
		MarkDirty( nameof( Data.Flags ) );
	}

	/// <summary>
	/// Remove a permission flag from this character.
	/// </summary>
	public void TakeFlag( string flag )
	{
		if ( !Data.Flags.Remove( flag ) ) return;
		MarkDirty( nameof( Data.Flags ) );
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
		catch ( Exception ex )
		{
			Log.Warning( $"Hexagon: GetVar failed to cast '{name}' to {typeof( T ).Name}: {ex.Message}" );
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
		return Data.RecognizedIds;
	}

	/// <summary>
	/// Add a character ID to this character's recognition list.
	/// </summary>
	public void AddRecognized( string characterId )
	{
		if ( !Data.RecognizedIds.Add( characterId ) ) return;
		MarkDirty( nameof( Data.RecognizedIds ) );
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
