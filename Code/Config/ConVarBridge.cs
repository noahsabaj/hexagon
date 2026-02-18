namespace Hexagon.Config;

/// <summary>
/// Manages the HexConfig ↔ ConVar integration.
///
/// With the new HexConVars design, ConVar properties delegate directly to HexConfig:
///   - ConVar getter  →  HexConfig.Get(key)
///   - ConVar setter  →  HexConfig.Set(key, value)
///
/// This means console commands ("hex_walk_speed 300") automatically update HexConfig,
/// and code that reads HexConfig.Get("gameplay.walkSpeed") sees the latest value.
///
/// The bridge subscribes to HexConfig.OnChanged for diagnostics and any future
/// integrations that need to react to all config changes in one place.
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

		HexConfig.OnChanged += OnHexConfigChanged;
		_initialized = true;

		Log.Info( "Hexagon: ConVarBridge initialized." );
	}

	/// <summary>
	/// Unsubscribe on framework shutdown.
	/// </summary>
	internal static void Shutdown()
	{
		HexConfig.OnChanged -= OnHexConfigChanged;
		_initialized = false;
	}

	private static void OnHexConfigChanged( string key, object value )
	{
		// ConVar properties already read directly from HexConfig, so no push needed.
		// This hook is available for future integrations (e.g., replication to clients,
		// admin event logging) that want to observe all HexConfig changes in one place.
	}
}
