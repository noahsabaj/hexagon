namespace Hexagon.UI;

/// <summary>
/// Static helper that auto-creates the Hexagon UI panel hierarchy if not already present.
/// Creates a ScreenPanel root with a character event bridge and all 13 default panels,
/// each on their own child GameObject.
///
/// HexUIManager is a GameObjectSystem and is auto-created by the scene — this method
/// only constructs the visual hierarchy (ScreenPanel + panel components).
/// </summary>
public static class HexUISetup
{
	/// <summary>
	/// The runtime-created UI root GameObject. Any panel that is a descendant of this
	/// object is considered a framework default. Schema overrides live elsewhere.
	/// </summary>
	public static GameObject UIObject { get; private set; }

	/// <summary>
	/// Ensure the Hexagon UI panel hierarchy exists in the scene.
	/// If the hierarchy already exists (UIObject is valid), this is a no-op.
	/// Uses IsValid() instead of null check because UIObject is static and can hold
	/// stale references to destroyed GameObjects across editor play/stop cycles.
	/// </summary>
	public static void EnsureUI( Scene scene )
	{
		if ( Application.IsHeadless ) return;
		if ( UIObject.IsValid() ) return;

		var root = new GameObject( true, "Hexagon UI" );
		UIObject = root;

		// ScreenPanel — root for all Razor PanelComponents
		root.AddComponent<ScreenPanel>();

		// Character event bridge — HexUIManager is a GameObjectSystem and cannot receive
		// ISceneEvent dispatch directly; this Component forwards those events to it.
		root.AddComponent<HexUIManagerBridge>();

		// Default panels — each on its own child GameObject for independent lifecycle
		AddPanel<IntroPanel>( root, "Intro" );
		AddPanel<CharacterSelect>( root, "CharacterSelect" );
		AddPanel<CharacterCreate>( root, "CharacterCreate" );
		AddPanel<HudPanel>( root, "HUD" );
		AddPanel<ChatPanel>( root, "Chat" );
		AddPanel<InventoryPanel>( root, "Inventory" );
		AddPanel<StoragePanel>( root, "Storage" );
		AddPanel<VendorPanel>( root, "Vendor" );
		AddPanel<Scoreboard>( root, "Scoreboard" );
		AddPanel<DeathScreen>( root, "DeathScreen" );
		AddPanel<ActionBar>( root, "ActionBar" );
		AddPanel<IntroduceMenu>( root, "IntroduceMenu" );
		AddPanel<NotificationPanel>( root, "Notifications" );
		AddPanel<CrosshairPanel>( root, "Crosshair" );
	}

	private static void AddPanel<T>( GameObject parent, string name ) where T : Component, new()
	{
		var child = new GameObject( true, $"Panel - {name}" );
		child.Parent = parent;
		child.AddComponent<T>();
	}
}
