namespace Hexagon.Chat;

/// <summary>
/// Defines a chat class (IC, OOC, Yell, Whisper, etc.).
/// Implement this interface and register with ChatManager to add custom chat types.
/// </summary>
public interface IChatClass
{
	/// <summary>
	/// Display name of this chat class (e.g. "In-Character", "OOC").
	/// </summary>
	string Name { get; }

	/// <summary>
	/// The prefix that triggers this chat class (e.g. "/y", "/w", "/me").
	/// Empty string means this is the default chat class (no prefix needed).
	/// </summary>
	string Prefix { get; }

	/// <summary>
	/// Maximum range in units. 0 = global (all players).
	/// </summary>
	float Range { get; }

	/// <summary>
	/// The color of this chat class in the chat panel.
	/// </summary>
	Color Color { get; }

	/// <summary>
	/// Check if a listener can hear a message from this speaker.
	/// Called per-recipient after range filtering.
	/// </summary>
	bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener );

	/// <summary>
	/// Check if a speaker is allowed to send a message in this chat class.
	/// Return false to block (e.g. rate limits, permissions).
	/// </summary>
	bool CanSay( HexPlayerComponent speaker, string message );

	/// <summary>
	/// Format the message for display (e.g. '{Name} says "{message}"').
	/// </summary>
	string Format( HexPlayerComponent speaker, string message );
}
