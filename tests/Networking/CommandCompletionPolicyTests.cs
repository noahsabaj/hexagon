#nullable enable

using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class CommandCompletionPolicyTests
{
	[TestMethod]
	public void OnlyAStaleLeaseWithoutASuccessfulExecutionIsRejected()
	{
		// (leaseCurrent, executionStarted, executionSucceeded) -> rejectAsStale. A current lease
		// always preserves the outcome; a stale one is overridden unless execution already
		// started and returned success, which stays authoritative.
		var table = new (bool Current, bool Started, bool Succeeded, bool Rejected)[]
		{
			(true, false, false, false),
			(true, true, false, false),
			(true, true, true, false),
			(false, false, false, true),
			(false, false, true, true),
			(false, true, false, true),
			(false, true, true, false)
		};

		foreach ( var row in table )
			Assert.AreEqual(
				row.Rejected,
				CommandCompletionPolicy.ShouldRejectAsStale( row.Current, row.Started, row.Succeeded ),
				$"current={row.Current} started={row.Started} succeeded={row.Succeeded}" );
	}
}
