#nullable enable

namespace Hexagon.Logic;

/// <summary>
/// When handing something over may start, and when a waiting offer lapses. Nothing changes hands until
/// the other side accepts, and an offer lasts only while both could still hand it over.
/// </summary>
public static class OfferRules
{
	/// <summary>How long an offer waits for an answer, in seconds.</summary>
	public const float Seconds = 30f;

	/// <summary>Why an offer cannot be made, or null when it can.</summary>
	public static string? ToOffer( bool giverIncapable, bool recipientThere, float distance, float reach, bool recipientIncapable, bool recipientBusy ) =>
		giverIncapable ? "You cannot do that now."
		: !recipientThere ? "There is nobody there."
		: distance > reach ? "You are too far away."
		: recipientIncapable ? "They are in no state to take it."
		: recipientBusy ? "They are already being offered something."
		: null;

	/// <summary>Why a waiting offer lapses now, for the journal, or null while it still stands.</summary>
	public static string? Lapse( float age, bool giverThere, bool eitherIncapable, float distance, float reach ) =>
		age > Seconds ? "time"
		: !giverThere ? "gone"
		: eitherIncapable ? "incapable"
		: distance > reach ? "apart"
		: null;
}
