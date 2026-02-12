namespace Hexagon.Chat;

/// <summary>
/// Network bridge for the chat system. Singleton component that lives on the
/// HexagonFramework GameObject. Provides RPCs for sending and receiving chat messages.
///
/// Color is passed as 3 floats (r, g, b) for RPC safety.
/// </summary>
public sealed class HexChatComponent : Component
{
	public static HexChatComponent Instance { get; private set; }

	protected override void OnStart()
	{
		if ( Instance != null && Instance != this )
		{
			Destroy();
			return;
		}

		Instance = this;
	}

	protected override void OnDestroy()
	{
		if ( Instance == this )
			Instance = null;
	}

	/// <summary>
	/// Called by clients to send a raw chat message to the server.
	/// The server will parse, permission-check, and route it.
	/// </summary>
	[Rpc.Host]
	public void SendMessage( string rawMessage )
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return;

		var player = HexGameManager.GetPlayer( caller );
		if ( player == null ) return;

		ChatManager.ProcessMessage( player, rawMessage );
	}

	/// <summary>
	/// Server-to-client broadcast of a formatted chat message.
	/// Only sent to filtered recipients via Rpc.FilterInclude.
	/// </summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void ReceiveMessage( string senderName, string chatClassName,
		string formattedMessage, float colorR, float colorG, float colorB )
	{
		var color = new Color( colorR, colorG, colorB );

		// Fire client-side event for UI
		HexEvents.Fire<IChatMessageReceivedListener>(
			x => x.OnChatMessageReceived( senderName, chatClassName, formattedMessage, color ) );
	}
}
