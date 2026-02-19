namespace Hexagon.UI;

/// <summary>
/// Configuration for a UI state.
/// </summary>
public class UIStateConfig
{
	/// <summary>
	/// The callback executed when the state is activated (typically used to Open target panels)
	/// </summary>
	public Action OnEnter { get; set; }

	/// <summary>
	/// If true, the mouse cursor is permanently visible (e.g. Character Select, Death Screen)
	/// </summary>
	public bool ForceCursorVisible { get; set; }

	/// <summary>
	/// If true, core HUD mechanics (Tab for Scoreboard, I for Inventory) are permitted
	/// </summary>
	public bool AllowGameplayInput { get; set; }
}
