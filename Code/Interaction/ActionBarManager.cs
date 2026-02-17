namespace Hexagon.Interaction;

/// <summary>
/// Thin facade over per-player <see cref="ActionBarComponent"/> instances.
/// Preserves the existing static API so callers don't need changes.
///
/// Usage: ActionBarManager.SetAction(player, "Searching...", 3f, callback)
/// Stared: ActionBarManager.DoStaredAction(player, target, "Lockpicking...", 5f, callback, onCancel)
/// </summary>
public static class ActionBarManager
{
	/// <summary>
	/// Start a timed action with a progress bar for the given player.
	/// Any existing action is replaced.
	/// </summary>
	public static void SetAction( HexPlayerComponent player, string text, float time, Action<HexPlayerComponent> callback )
	{
		player?.GetComponent<ActionBarComponent>()?.SetAction( text, time, callback );
	}

	/// <summary>
	/// Start a stared action that cancels if the player looks away from the target
	/// or moves too far.
	/// </summary>
	public static void DoStaredAction( HexPlayerComponent player, GameObject target, string text, float time,
		Action<HexPlayerComponent> callback, Action onCancel = null, float maxDistance = 130f )
	{
		player?.GetComponent<ActionBarComponent>()?.DoStaredAction( target, text, time, callback, onCancel, maxDistance );
	}

	/// <summary>
	/// Cancel the current action for a player. Fires the cancel callback if set.
	/// </summary>
	public static void CancelAction( HexPlayerComponent player )
	{
		player?.GetComponent<ActionBarComponent>()?.CancelAction();
	}

	/// <summary>
	/// Returns true if the player has an active timed action.
	/// </summary>
	public static bool HasAction( HexPlayerComponent player )
	{
		return player?.GetComponent<ActionBarComponent>()?.HasAction ?? false;
	}

	/// <summary>
	/// Remove all actions for a player (e.g., on disconnect).
	/// </summary>
	internal static void RemovePlayer( HexPlayerComponent player )
	{
		player?.GetComponent<ActionBarComponent>()?.ClearAction();
	}
}
