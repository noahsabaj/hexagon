namespace Hexagon.UI;

/// <summary>
/// Interface for all Hexagon UI panels. Implement on PanelComponents to allow
/// HexUIManager to discover and coordinate them.
///
/// To override a default panel, create your own PanelComponent implementing IHexPanel
/// with the same PanelName (e.g. "CharacterSelect") on any GameObject in your scene.
/// The framework default is automatically disabled — no configuration needed.
/// </summary>
public interface IHexPanel
{
	/// <summary>
	/// Unique name for this panel (e.g. "CharacterSelect", "Inventory").
	/// </summary>
	string PanelName { get; }

	/// <summary>
	/// Whether this panel is currently visible.
	/// </summary>
	bool IsOpen { get; }

	/// <summary>
	/// Show the panel.
	/// </summary>
	void Open();

	/// <summary>
	/// Hide the panel.
	/// </summary>
	void Close();
}

/// <summary>
/// Fired when the death screen respawn button is pressed.
/// Schema devs implement this to handle respawn logic.
/// </summary>
public interface IDeathScreenRespawnListener
{
	void OnRespawnRequested( HexPlayerComponent player );
}
