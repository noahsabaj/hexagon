namespace Hexagon.Interaction;

/// <summary>
/// Client-side action bar state and RPCs.
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class ActionBarPlayerComponent : Component
{
	/// <summary>
	/// Client-side: start time of the current action bar.
	/// </summary>
	public float ActionStartTime { get; private set; }

	/// <summary>
	/// Client-side: end time of the current action bar.
	/// </summary>
	public float ActionEndTime { get; private set; }

	/// <summary>
	/// Client-side: text label for the current action bar.
	/// </summary>
	public string ActionText { get; private set; } = "";

	/// <summary>
	/// Client-side: fired when action bar state changes.
	/// </summary>
	public event Action OnActionBarChanged;

	/// <summary>
	/// Server sends action bar state to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveActionBar( float startTime, float endTime, string text )
	{
		ActionStartTime = startTime;
		ActionEndTime = endTime;
		ActionText = text;
		OnActionBarChanged?.Invoke();
	}

	/// <summary>
	/// Server clears the action bar on the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveActionBarReset()
	{
		ActionStartTime = 0;
		ActionEndTime = 0;
		ActionText = "";
		OnActionBarChanged?.Invoke();
	}
}
