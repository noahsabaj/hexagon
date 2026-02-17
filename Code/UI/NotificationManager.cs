namespace Hexagon.UI;

/// <summary>
/// Server-side notification system. Sends toast notifications to specific players.
/// Use this for transient feedback (command results, errors) instead of chat messages.
/// </summary>
public static class NotificationManager
{
	/// <summary>
	/// Send a notification to a specific player with the default duration.
	/// </summary>
	public static void Send( HexPlayerComponent target, string message )
	{
		var duration = Config.HexConfig.Get<float>( "notification.defaultDuration", 8f );
		Send( target, message, duration );
	}

	/// <summary>
	/// Send a notification to a specific player with a custom duration in seconds.
	/// </summary>
	public static void Send( HexPlayerComponent target, string message, float duration )
	{
		if ( target == null || string.IsNullOrEmpty( message ) ) return;

		target.GetComponent<NotificationPlayerComponent>()?.ReceiveNotification( message, duration );
	}

	/// <summary>
	/// Send a notification to all connected players with the default duration.
	/// </summary>
	public static void SendAll( string message )
	{
		var duration = Config.HexConfig.Get<float>( "notification.defaultDuration", 8f );
		SendAll( message, duration );
	}

	/// <summary>
	/// Send a notification to all connected players with a custom duration.
	/// </summary>
	public static void SendAll( string message, float duration )
	{
		if ( string.IsNullOrEmpty( message ) ) return;

		foreach ( var kvp in HexagonSystem.Players )
		{
			Send( kvp.Value, message, duration );
		}
	}

}
