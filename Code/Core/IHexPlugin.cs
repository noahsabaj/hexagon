namespace Hexagon.Core;

/// <summary>
/// Interface for Hexagon plugins. Implement this alongside [HexPlugin] attribute
/// to create a plugin that the framework auto-discovers and loads.
/// </summary>
public interface IHexPlugin
{
	/// <summary>
	/// Called when the plugin is loaded during framework initialization.
	/// Register your systems (items, factions, commands, etc.) here.
	/// </summary>
	void OnPluginLoaded();

	/// <summary>
	/// Called when the plugin is unloaded during framework shutdown.
	/// Clean up any resources here.
	/// </summary>
	void OnPluginUnloaded() { }
}

/// <summary>
/// Marks a class as a Hexagon plugin for auto-discovery.
/// The class must also implement IHexPlugin.
/// </summary>
[AttributeUsage( AttributeTargets.Class, AllowMultiple = false )]
public class HexPluginAttribute : Attribute
{
	public string Name { get; set; }
	public string Description { get; set; }
	public string Author { get; set; }
	public string Version { get; set; }
	public int Priority { get; set; } = 100; // Lower = loads first

	public HexPluginAttribute( string name )
	{
		Name = name;
	}
}
