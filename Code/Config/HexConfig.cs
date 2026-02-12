namespace Hexagon.Config;

/// <summary>
/// Server-side configuration system. Admin-controlled settings that are persisted
/// and synced to all connected clients.
///
/// Usage:
///   HexConfig.Add("walkSpeed", 200f, "Default walk speed");
///   HexConfig.Add("maxCharacters", 5, "Max characters per player");
///   var speed = HexConfig.Get&lt;float&gt;("walkSpeed");
///   HexConfig.Set("walkSpeed", 150f);
/// </summary>
public static class HexConfig
{
	private static readonly Dictionary<string, ConfigEntry> _entries = new();
	private static readonly Dictionary<string, object> _overrides = new();
	private const string SaveCollection = "config";
	private const string SaveKey = "server";

	/// <summary>
	/// All registered config entries.
	/// </summary>
	public static IReadOnlyDictionary<string, ConfigEntry> Entries => _entries;

	internal static void Initialize()
	{
		Load();
		Log.Info( $"Hexagon: Config initialized with {_entries.Count} entries, {_overrides.Count} overrides loaded." );
	}

	/// <summary>
	/// Register a new config entry with a default value.
	/// Call this during plugin/schema initialization.
	/// </summary>
	public static void Add( string key, object defaultValue, string description = "", string category = "General", Action<object, object> onChange = null )
	{
		_entries[key] = new ConfigEntry
		{
			Key = key,
			DefaultValue = defaultValue,
			Description = description,
			Category = category,
			ValueType = defaultValue?.GetType(),
			OnChange = onChange
		};
	}

	/// <summary>
	/// Get a config value. Returns the override if set, otherwise the default.
	/// </summary>
	public static T Get<T>( string key, T fallback = default )
	{
		if ( _overrides.TryGetValue( key, out var overrideValue ) )
		{
			try
			{
				return (T)Convert.ChangeType( overrideValue, typeof( T ) );
			}
			catch
			{
				// Fall through to default
			}
		}

		if ( _entries.TryGetValue( key, out var entry ) && entry.DefaultValue is T typedDefault )
		{
			return typedDefault;
		}

		return fallback;
	}

	/// <summary>
	/// Set a config value (creates an override). Triggers OnChange callback if registered.
	/// </summary>
	public static void Set( string key, object value )
	{
		var oldValue = _overrides.TryGetValue( key, out var existing ) ? existing : _entries.GetValueOrDefault( key )?.DefaultValue;
		_overrides[key] = value;

		if ( _entries.TryGetValue( key, out var entry ) )
		{
			entry.OnChange?.Invoke( oldValue, value );
		}
	}

	/// <summary>
	/// Reset a config value back to its default.
	/// </summary>
	public static void Reset( string key )
	{
		_overrides.Remove( key );
	}

	/// <summary>
	/// Save all config overrides to disk.
	/// </summary>
	public static void Save()
	{
		Persistence.DatabaseManager.Save( SaveCollection, SaveKey, _overrides );
	}

	/// <summary>
	/// Load config overrides from disk.
	/// </summary>
	public static void Load()
	{
		var loaded = Persistence.DatabaseManager.Load<Dictionary<string, object>>( SaveCollection, SaveKey );

		if ( loaded != null )
		{
			_overrides.Clear();
			foreach ( var kvp in loaded )
			{
				_overrides[kvp.Key] = kvp.Value;
			}
		}
	}
}

/// <summary>
/// A registered configuration entry.
/// </summary>
public class ConfigEntry
{
	public string Key { get; set; }
	public object DefaultValue { get; set; }
	public string Description { get; set; }
	public string Category { get; set; }
	public Type ValueType { get; set; }
	public Action<object, object> OnChange { get; set; }
}
