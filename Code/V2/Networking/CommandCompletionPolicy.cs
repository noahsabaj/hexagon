#nullable enable

namespace Hexagon.V2.Networking;

/// <summary>
/// Defines the command-session linearization boundary independently of the
/// s&amp;box transport. Once execution has started and returned a successful
/// application result, that result remains authoritative even if its lease is
/// stale by the time the transport finishes the request.
/// </summary>
internal static class CommandCompletionPolicy
{
	public static bool ShouldRejectAsStale(
		bool leaseIsCurrentAtCompletion,
		bool executionStarted,
		bool executionSucceeded ) =>
		!leaseIsCurrentAtCompletion && (!executionStarted || !executionSucceeded);
}
