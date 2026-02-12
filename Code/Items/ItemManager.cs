namespace Hexagon.Items;

/// <summary>
/// Manages item definitions and active item instances.
/// Definitions auto-register when .item GameResource assets load.
/// Instances are created/loaded from the database.
/// </summary>
public static class ItemManager
{
	private static readonly Dictionary<string, ItemDefinition> _definitions = new();
	private static readonly Dictionary<string, ItemInstance> _instances = new();

	/// <summary>
	/// All registered item definitions.
	/// </summary>
	public static IReadOnlyDictionary<string, ItemDefinition> Definitions => _definitions;

	/// <summary>
	/// All active item instances.
	/// </summary>
	public static IReadOnlyDictionary<string, ItemInstance> Instances => _instances;

	internal static void Initialize()
	{
		Log.Info( $"Hexagon: ItemManager initialized. {_definitions.Count} definition(s)." );
	}

	/// <summary>
	/// Register an item definition. Called automatically by ItemDefinition.PostLoad().
	/// </summary>
	public static void Register( ItemDefinition definition )
	{
		_definitions[definition.UniqueId] = definition;
	}

	/// <summary>
	/// Get an item definition by its unique ID.
	/// </summary>
	public static ItemDefinition GetDefinition( string uniqueId )
	{
		if ( string.IsNullOrEmpty( uniqueId ) ) return null;
		return _definitions.GetValueOrDefault( uniqueId );
	}

	/// <summary>
	/// Create a new item instance from a definition and persist it.
	/// </summary>
	public static ItemInstance CreateInstance( string definitionId, string characterId = null, Dictionary<string, object> data = null )
	{
		var def = GetDefinition( definitionId );
		if ( def == null )
		{
			Log.Warning( $"Hexagon: Unknown item definition '{definitionId}'" );
			return null;
		}

		var instance = new ItemInstance
		{
			Id = Persistence.DatabaseManager.NewId(),
			DefinitionId = definitionId,
			CharacterId = characterId,
			Data = data ?? new()
		};

		// Persist
		Persistence.DatabaseManager.Save( "items", instance.Id, instance );

		// Track
		_instances[instance.Id] = instance;

		// Notify definition
		def.OnInstanced( instance );

		return instance;
	}

	/// <summary>
	/// Get an active item instance by ID.
	/// </summary>
	public static ItemInstance GetInstance( string instanceId )
	{
		if ( string.IsNullOrEmpty( instanceId ) ) return null;

		// Check active cache
		if ( _instances.TryGetValue( instanceId, out var inst ) )
			return inst;

		// Try loading from DB
		var loaded = Persistence.DatabaseManager.Load<ItemInstance>( "items", instanceId );
		if ( loaded != null )
			_instances[loaded.Id] = loaded;

		return loaded;
	}

	/// <summary>
	/// Remove an item instance permanently (from memory and database).
	/// </summary>
	public static void DestroyInstance( string instanceId )
	{
		if ( _instances.TryGetValue( instanceId, out var instance ) )
		{
			instance.Definition?.OnRemoved( instance );
			_instances.Remove( instanceId );
		}

		Persistence.DatabaseManager.Delete( "items", instanceId );
	}

	/// <summary>
	/// Save all dirty item instances to the database.
	/// </summary>
	public static void SaveAll()
	{
		var saved = 0;

		foreach ( var instance in _instances.Values )
		{
			if ( instance.IsDirty )
			{
				instance.Save();
				saved++;
			}
		}

		if ( saved > 0 )
			Log.Info( $"Hexagon: Saved {saved} item instance(s)." );
	}

	/// <summary>
	/// Load all item instances for a specific character from the database.
	/// </summary>
	public static List<ItemInstance> LoadInstancesForCharacter( string characterId )
	{
		var items = Persistence.DatabaseManager.Select<ItemInstance>(
			"items",
			i => i.CharacterId == characterId
		);

		foreach ( var item in items )
		{
			_instances[item.Id] = item;
		}

		return items;
	}

	/// <summary>
	/// Get all definitions in a specific category.
	/// </summary>
	public static List<ItemDefinition> GetDefinitionsByCategory( string category )
	{
		return _definitions.Values
			.Where( d => d.Category == category )
			.OrderBy( d => d.Order )
			.ToList();
	}

	/// <summary>
	/// Get all item categories.
	/// </summary>
	public static List<string> GetCategories()
	{
		return _definitions.Values
			.Select( d => d.Category )
			.Distinct()
			.OrderBy( c => c )
			.ToList();
	}
}
