namespace Hexagon.Core;

/// <summary>
/// Discovers and manages Hexagon plugins. Scans all loaded assemblies for classes
/// marked with [HexPlugin] that implement IHexPlugin.
/// </summary>
public sealed class PluginManager : GameObjectSystem<PluginManager>
{
	private static PluginManager _instance;
	private static PluginManager Instance => _instance;

	private readonly List<PluginEntry> _plugins = new();

	public PluginManager( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	/// <summary>
	/// All currently loaded plugins.
	/// </summary>
	public static IReadOnlyList<PluginEntry> Plugins => Instance?._plugins;

	internal static void Initialize()
	{
		if ( Instance == null ) return;
		Instance._plugins.Clear();
		Instance.DiscoverPlugins();
		Instance.LoadPlugins();
	}

	internal static void Shutdown() => Instance?.ShutdownInternal();

	private void ShutdownInternal()
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

	private void DiscoverPlugins()
	{
		var pluginTypes = TypeLibrary.GetTypes<IHexPlugin>()
			.Where( t => t.GetAttribute<HexPluginAttribute>() != null && !t.IsAbstract );

		foreach ( var type in pluginTypes )
		{
			var attr = type.GetAttribute<HexPluginAttribute>();

			// Attempt to read package ID from assembly metadata if available
			var packageId = "";
			try
			{
				packageId = type.TargetType?.Assembly?.GetName()?.Name ?? "";
			}
			catch ( Exception ex )
			{
				Log.Warning( $"Hexagon: PluginManager could not read assembly name for plugin type '{type.Name}': {ex.Message}" );
			}

			_plugins.Add( new PluginEntry
			{
				Name = attr.Name,
				Description = attr.Description ?? "",
				Author = attr.Author ?? "",
				Version = attr.Version ?? "1.0",
				Priority = attr.Priority,
				PackageId = packageId,
				Type = type,
				Instance = null // Created during LoadPlugins
			} );
		}

		// Sort by priority (lower = loads first)
		_plugins.Sort( ( a, b ) => a.Priority.CompareTo( b.Priority ) );

		Log.Info( $"Hexagon: Discovered {_plugins.Count} plugin(s)." );
	}

	private void LoadPlugins()
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
		return Instance?._plugins.FirstOrDefault( p => p.Name == name )?.Instance;
	}

	public override void Dispose()
	{
		ShutdownInternal();
		if ( _instance == this ) _instance = null;
		base.Dispose();
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
	/// <summary>
	/// The package/addon ID this plugin comes from, if determinable from assembly metadata.
	/// </summary>
	public string PackageId { get; set; } = "";
	public TypeDescription Type { get; set; }
	public IHexPlugin Instance { get; set; }
}
