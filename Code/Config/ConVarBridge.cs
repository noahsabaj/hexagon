namespace Hexagon.Config;

/// <summary>
/// Lifecycle stub for the HexConfig ↔ ConVar integration.
///
/// HexConVars properties delegate directly to HexConfig (getter reads, setter writes),
/// so console commands ("hex_walk_speed 300") automatically update HexConfig and all
/// code reading HexConfig.Get sees the latest value. No event subscription needed.
/// </summary>
public static class ConVarBridge
{
	private static bool _initialized;

	/// <summary>
	/// Initialize the bridge. Call after HexConfig.Initialize() has loaded overrides.
	/// </summary>
	internal static void Initialize()
	{
		if ( _initialized ) return;
		_initialized = true;
		Log.Info( "Hexagon: ConVarBridge initialized." );
	}

	/// <summary>
	/// Lifecycle stub for framework shutdown.
	/// </summary>
	internal static void Shutdown()
	{
		_initialized = false;
	}
}
