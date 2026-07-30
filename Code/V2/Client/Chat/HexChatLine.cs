#nullable enable

namespace Hexagon.V2.Client.Chat;

/// <summary>
/// One rendered chat line. Deliberately flat and already-resolved: the author name here is whatever
/// the HOST decided this viewer should see, so a panel cannot accidentally render an identity the
/// viewer has not earned. A client-only line (command output, a refusal) simply has no author.
/// </summary>
/// <param name="ReceivedAt">
/// <c>RealTime.Now</c> when the line arrived, which is what the fade is measured against. Wall-clock
/// send time is not usable for this — it would fade a line by how old the MESSAGE is rather than how
/// long this client has had it on screen.
/// </param>
public sealed record HexChatLine(
	string Text,
	string Author = "",
	string ChannelLabel = "",
	string? Colour = null,
	float ReceivedAt = 0f );
