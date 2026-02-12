namespace Hexagon.Items;

/// <summary>
/// Defines a context menu action for an item (like Helix's ITEM.functions).
/// </summary>
public class ItemAction
{
	/// <summary>
	/// Display name of the action.
	/// </summary>
	public string Name { get; set; }

	/// <summary>
	/// Icon name (material icon).
	/// </summary>
	public string Icon { get; set; }

	/// <summary>
	/// Server-side callback when the action is executed. Return false to keep item, true to consume it.
	/// </summary>
	public Func<HexPlayerComponent, ItemInstance, bool> OnRun { get; set; }

	/// <summary>
	/// Permission check - should this action be visible/available?
	/// </summary>
	public Func<HexPlayerComponent, ItemInstance, bool> OnCanRun { get; set; }
}
