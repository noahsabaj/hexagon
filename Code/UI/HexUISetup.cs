namespace Hexagon.UI;

/// <summary>
/// Static helper that auto-creates the full Hexagon UI hierarchy if not already present.
/// Creates a ScreenPanel root with HexUIManager and all 9 default panels.
/// </summary>
public static class HexUISetup
{
	/// <summary>
	/// Ensure HexUIManager and all default panels exist in the scene.
	/// If a HexUIManager is already present, this is a no-op.
	/// </summary>
	public static void EnsureUI( Scene scene )
	{
		// Don't create UI on headless/dedicated server
		if ( Application.IsHeadless ) return;

		// Guard: if HexUIManager already exists, skip
		if ( scene.GetAll<HexUIManager>().Any() ) return;

		var go = new GameObject( true, "Hexagon UI" );

		// ScreenPanel is the root for all Razor panel components
		go.AddComponent<ScreenPanel>();

		// State machine
		go.AddComponent<HexUIManager>();

		// All 9 default panels (must be on same GO as ScreenPanel)
		go.AddComponent<CharacterSelect>();
		go.AddComponent<CharacterCreate>();
		go.AddComponent<HudPanel>();
		go.AddComponent<ChatPanel>();
		go.AddComponent<InventoryPanel>();
		go.AddComponent<StoragePanel>();
		go.AddComponent<VendorPanel>();
		go.AddComponent<Scoreboard>();
		go.AddComponent<DeathScreen>();
	}
}
