namespace Hexagon.Interaction;

/// <summary>
/// Timed action bar events — start permission, completion, cancellation, UI updates.
/// </summary>
public interface IHexActionEvent : ISceneEvent<IHexActionEvent>
{
	bool CanStartAction( HexPlayerComponent player, string actionText ) => true;
	void OnActionCompleted( HexPlayerComponent player, string actionText ) { }
	void OnActionCancelled( HexPlayerComponent player, string actionText ) { }
	void OnActionBarUpdated( HexPlayerComponent player ) { }
}
