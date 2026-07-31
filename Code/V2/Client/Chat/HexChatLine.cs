#nullable enable

namespace Hexagon.V2.Client.Chat;

/// <summary>
/// One rendered chat line. Deliberately flat and already-resolved: the author name here is whatever
/// the HOST decided this viewer should see, so a panel cannot accidentally render an identity the
/// viewer has not earned. A client-only line (command output, a refusal) simply has no author.
/// <para>
/// <c>Key</c> identifies the line across renders and is the ONLY thing the fade needs from the
/// caller. The panel stamps arrival itself the first time it sees a key, because the alternative —
/// letting the caller pass a timestamp — asks every consumer to keep per-message first-seen
/// bookkeeping, and the one consumer that tried froze a single clock at construction and made
/// closed chat invisible after the first fade window. Any stable per-line string will do: a message
/// id for chat, a counter for client-authored output. Two lines sharing a key share a fade.
/// </para>
/// </summary>
public sealed record HexChatLine(
	string Text,
	string Key,
	string Author = "",
	string ChannelLabel = "",
	string? Colour = null );
