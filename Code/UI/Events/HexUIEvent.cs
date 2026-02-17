namespace Hexagon.UI;

/// <summary>
/// UI notification and death screen events.
/// </summary>
public interface IHexUIEvent : ISceneEvent<IHexUIEvent>
{
	void OnNotificationReceived( string message, float duration ) { }
	void OnRespawnRequested( HexPlayerComponent player ) { }
}
