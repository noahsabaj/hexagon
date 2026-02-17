namespace Hexagon.Chat;

/// <summary>
/// Chat message events — permission, server-side dispatch, client-side receipt, focus.
/// </summary>
public interface IHexChatEvent : ISceneEvent<IHexChatEvent>
{
	bool CanSendChatMessage( HexPlayerComponent sender, IChatClass chatClass, string message ) => true;
	void OnChatMessage( HexPlayerComponent sender, IChatClass chatClass, string rawMessage, string formattedMessage ) { }
	void OnChatMessageReceived( string senderName, string chatClassName, string formattedMessage, Color color ) { }
	void OnChatFocusRequested() { }
}
