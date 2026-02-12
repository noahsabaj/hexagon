namespace Hexagon.Factions;

/// <summary>
/// Manages faction and class definitions. Factions auto-register when their
/// GameResource assets are loaded by s&box.
/// </summary>
public static class FactionManager
{
	private static readonly Dictionary<string, FactionDefinition> _factions = new();
	private static readonly Dictionary<string, ClassDefinition> _classes = new();
	private static readonly Dictionary<string, List<ClassDefinition>> _factionClasses = new();

	/// <summary>
	/// All registered factions.
	/// </summary>
	public static IReadOnlyDictionary<string, FactionDefinition> Factions => _factions;

	/// <summary>
	/// All registered classes.
	/// </summary>
	public static IReadOnlyDictionary<string, ClassDefinition> Classes => _classes;

	internal static void Initialize()
	{
		Log.Info( $"Hexagon: FactionManager initialized. {_factions.Count} faction(s), {_classes.Count} class(es)." );
	}

	/// <summary>
	/// Register a faction definition. Called automatically by FactionDefinition.PostLoad().
	/// </summary>
	public static void Register( FactionDefinition faction )
	{
		_factions[faction.UniqueId] = faction;

		if ( !_factionClasses.ContainsKey( faction.UniqueId ) )
			_factionClasses[faction.UniqueId] = new();
	}

	/// <summary>
	/// Register a class definition. Called automatically by ClassDefinition.PostLoad().
	/// </summary>
	public static void RegisterClass( ClassDefinition classDef )
	{
		_classes[classDef.UniqueId] = classDef;

		if ( !string.IsNullOrEmpty( classDef.FactionId ) )
		{
			if ( !_factionClasses.ContainsKey( classDef.FactionId ) )
				_factionClasses[classDef.FactionId] = new();

			// Remove existing entry for same ID, then add
			_factionClasses[classDef.FactionId].RemoveAll( c => c.UniqueId == classDef.UniqueId );
			_factionClasses[classDef.FactionId].Add( classDef );
		}
	}

	/// <summary>
	/// Get a faction by its unique ID.
	/// </summary>
	public static FactionDefinition GetFaction( string uniqueId )
	{
		return _factions.GetValueOrDefault( uniqueId );
	}

	/// <summary>
	/// Get a class by its unique ID.
	/// </summary>
	public static ClassDefinition GetClass( string uniqueId )
	{
		return _classes.GetValueOrDefault( uniqueId );
	}

	/// <summary>
	/// Get all classes belonging to a faction.
	/// </summary>
	public static List<ClassDefinition> GetClassesForFaction( string factionId )
	{
		return _factionClasses.GetValueOrDefault( factionId ) ?? new();
	}

	/// <summary>
	/// Get all factions available for character creation (IsDefault = true).
	/// </summary>
	public static List<FactionDefinition> GetDefaultFactions()
	{
		return _factions.Values
			.Where( f => f.IsDefault )
			.OrderBy( f => f.Order )
			.ToList();
	}

	/// <summary>
	/// Get all factions, sorted by order.
	/// </summary>
	public static List<FactionDefinition> GetAllFactions()
	{
		return _factions.Values
			.OrderBy( f => f.Order )
			.ToList();
	}

	/// <summary>
	/// Check how many active players are in a faction.
	/// </summary>
	public static int GetFactionPlayerCount( string factionId )
	{
		return Characters.CharacterManager.GetActiveCharacters()
			.Count( kvp => kvp.Value.Data.Faction == factionId );
	}

	/// <summary>
	/// Check if a faction has room for another player.
	/// </summary>
	public static bool CanJoinFaction( string factionId )
	{
		var faction = GetFaction( factionId );
		if ( faction == null ) return false;
		if ( faction.MaxPlayers <= 0 ) return true;
		return GetFactionPlayerCount( factionId ) < faction.MaxPlayers;
	}

	/// <summary>
	/// Check how many active players are in a class.
	/// </summary>
	public static int GetClassPlayerCount( string classId )
	{
		return Characters.CharacterManager.GetActiveCharacters()
			.Count( kvp => kvp.Value.Data.Class == classId );
	}

	/// <summary>
	/// Check if a class has room for another player.
	/// </summary>
	public static bool CanJoinClass( string classId )
	{
		var classDef = GetClass( classId );
		if ( classDef == null ) return false;
		if ( classDef.MaxPlayers <= 0 ) return true;
		return GetClassPlayerCount( classId ) < classDef.MaxPlayers;
	}
}
