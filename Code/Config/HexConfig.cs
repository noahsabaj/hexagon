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
public sealed class HexConfig : GameObjectSystem<HexConfig>
{
	private static HexConfig _instance;
	private static HexConfig Instance => _instance;

	private readonly Dictionary<string, ConfigEntry> _entries = new();
	private readonly Dictionary<string, object> _overrides = new();
	private const string SaveCollection = "config";
	private const string SaveKey = "server";

	public HexConfig( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	/// <summary>
	/// All registered config entries.
	/// </summary>
	public static IReadOnlyDictionary<string, ConfigEntry> Entries => Instance?._entries;

	/// <summary>
	/// Fired after any config key is changed via Set().
	/// Parameters: (key, newValue).
	/// Used by ConVarBridge to push HexConfig changes into ConVars.
	/// </summary>
	public static event Action<string, object> OnChanged;

	internal static void Initialize()
	{
		if ( Instance == null ) return;
		Load();
		Log.Info( $"Hexagon: Config initialized with {Instance._entries.Count} entries, {Instance._overrides.Count} overrides loaded." );
	}

	/// <summary>
	/// Register a new config entry with a default value.
	/// Call this during plugin/schema initialization.
	/// </summary>
	public static void Add( string key, object defaultValue, string description = "", string category = "General", Action<object, object> onChange = null )
	{
		if ( Instance == null ) return;

		Instance._entries[key] = new ConfigEntry
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
		if ( Instance == null ) return fallback;

		if ( Instance._overrides.TryGetValue( key, out var overrideValue ) )
		{
			try
			{
				return (T)Convert.ChangeType( overrideValue, typeof( T ) );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"Hexagon: HexConfig.Get failed to convert override for '{key}' to {typeof( T ).Name}: {ex.Message}" );
				// Fall through to default
			}
		}

		if ( Instance._entries.TryGetValue( key, out var entry ) && entry.DefaultValue is T typedDefault )
		{
			return typedDefault;
		}

		return fallback;
	}

	/// <summary>
	/// Set a config value (creates an override). Triggers OnChange callback if registered
	/// and fires the static OnChanged event for bridge integrations.
	/// </summary>
	public static void Set( string key, object value )
	{
		if ( Instance == null ) return;

		var oldValue = Instance._overrides.TryGetValue( key, out var existing ) ? existing : Instance._entries.GetValueOrDefault( key )?.DefaultValue;
		Instance._overrides[key] = value;

		if ( Instance._entries.TryGetValue( key, out var entry ) )
		{
			entry.OnChange?.Invoke( oldValue, value );
		}

		OnChanged?.Invoke( key, value );
	}

	/// <summary>
	/// Reset a config value back to its default.
	/// </summary>
	public static void Reset( string key )
	{
		Instance?._overrides.Remove( key );
	}

	/// <summary>
	/// Save all config overrides to disk.
	/// </summary>
	public static void Save()
	{
		if ( Instance == null ) return;
		Persistence.DatabaseManager.Save( SaveCollection, SaveKey, Instance._overrides );
	}

	/// <summary>
	/// Load config overrides from disk.
	/// </summary>
	public static void Load()
	{
		if ( Instance == null ) return;
		Instance.LoadInternal();
	}

	private void LoadInternal()
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

	public override void Dispose()
	{
		Save();
		if ( _instance == this ) _instance = null;
		base.Dispose();
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
