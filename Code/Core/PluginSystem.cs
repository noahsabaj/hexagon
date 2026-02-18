namespace Hexagon.Core;

/// <summary>
/// GameObjectSystem shell that gives PluginManager proper scene lifecycle.
///
/// Initialization (PluginManager.Initialize) is still called explicitly by HexagonSystem
/// at the correct point in the init order (after DatabaseManager, HexConfig, and
/// CharacterManager are ready). Shutdown is handled by PluginManager.Dispose() directly,
/// so this system requires no additional logic.
/// </summary>
public sealed class PluginSystem : GameObjectSystem<PluginSystem>
{
	public PluginSystem( Scene scene ) : base( scene ) { }
}
