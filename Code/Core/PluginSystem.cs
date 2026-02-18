namespace Hexagon.Core;

/// <summary>
/// GameObjectSystem wrapper that gives PluginManager proper scene lifecycle.
///
/// Initialization (PluginManager.Initialize) is still called explicitly by HexagonSystem
/// at the correct point in the init order (after DatabaseManager, HexConfig, and
/// CharacterManager are ready). This system handles shutdown only — ensuring plugins
/// are cleanly unloaded when the scene ends, even if HexagonSystem.Dispose is not called.
/// </summary>
public sealed class PluginSystem : GameObjectSystem<PluginSystem>
{
	private static PluginSystem _instance;

	public PluginSystem( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	public override void Dispose()
	{
		PluginManager.Shutdown();

		if ( _instance == this )
			_instance = null;

		base.Dispose();
	}
}
