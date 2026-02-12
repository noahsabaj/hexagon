namespace Hexagon.Core;

/// <summary>
/// Discovers and manages Hexagon plugins. Scans all loaded assemblies for classes
/// marked with [HexPlugin] that implement IHexPlugin.
/// </summary>
public static class PluginManager
{
	private static readonly List<PluginEntry> _plugins = new();

	/// <summary>
	/// All currently loaded plugins.
	/// </summary>
	public static IReadOnlyList<PluginEntry> Plugins => _plugins;

	internal static void Initialize()
	{
		_plugins.Clear();
		DiscoverPlugins();
		LoadPlugins();
	}

	internal static void Shutdown()
	{
		foreach ( var entry in _plugins )
		{
			try
			{
				entry.Instance.OnPluginUnloaded();
				Log.Info( $"Hexagon: Plugin '{entry.Name}' unloaded." );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Error unloading plugin '{entry.Name}': {ex}" );
			}
		}

		_plugins.Clear();
	}

	private static void DiscoverPlugins()
	{
		var pluginTypes = TypeLibrary.GetTypes<IHexPlugin>()
			.Where( t => t.GetAttribute<HexPluginAttribute>() != null && !t.IsAbstract );

		foreach ( var type in pluginTypes )
		{
			var attr = type.GetAttribute<HexPluginAttribute>();

			_plugins.Add( new PluginEntry
			{
				Name = attr.Name,
				Description = attr.Description ?? "",
				Author = attr.Author ?? "",
				Version = attr.Version ?? "1.0",
				Priority = attr.Priority,
				Type = type,
				Instance = null // Created during LoadPlugins
			} );
		}

		// Sort by priority (lower = loads first)
		_plugins.Sort( ( a, b ) => a.Priority.CompareTo( b.Priority ) );

		Log.Info( $"Hexagon: Discovered {_plugins.Count} plugin(s)." );
	}

	private static void LoadPlugins()
	{
		foreach ( var entry in _plugins )
		{
			try
			{
				entry.Instance = entry.Type.Create<IHexPlugin>();
				entry.Instance.OnPluginLoaded();
				Log.Info( $"Hexagon: Plugin '{entry.Name}' v{entry.Version} loaded." );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Failed to load plugin '{entry.Name}': {ex}" );
			}
		}
	}

	/// <summary>
	/// Get a loaded plugin by name.
	/// </summary>
	public static IHexPlugin Get( string name )
	{
		return _plugins.FirstOrDefault( p => p.Name == name )?.Instance;
	}
}

/// <summary>
/// Holds metadata and instance reference for a loaded plugin.
/// </summary>
public class PluginEntry
{
	public string Name { get; set; }
	public string Description { get; set; }
	public string Author { get; set; }
	public string Version { get; set; }
	public int Priority { get; set; }
	public TypeDescription Type { get; set; }
	public IHexPlugin Instance { get; set; }
}
