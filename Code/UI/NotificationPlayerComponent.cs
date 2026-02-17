namespace Hexagon.UI;

/// <summary>
/// Receives toast notifications from the server.
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class NotificationPlayerComponent : Component
{
	/// <summary>
	/// Server sends a toast notification to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveNotification( string message, float duration )
	{
		IHexUIEvent.Post(
			x => x.OnNotificationReceived( message, duration ) );
	}
}
