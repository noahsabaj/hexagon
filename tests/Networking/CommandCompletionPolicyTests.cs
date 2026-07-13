#nullable enable

using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class CommandCompletionPolicyTests
{
	[TestMethod]
	public void SuccessfulExecutionRemainsAuthoritativeAfterLeaseBecomesStale()
	{
		Assert.IsFalse( CommandCompletionPolicy.ShouldRejectAsStale(
			leaseIsCurrentAtCompletion: false,
			executionStarted: true,
			executionSucceeded: true ) );
	}

	[TestMethod]
	public void StaleLeaseBeforeExecutionIsRejectedEvenIfPresentedWithSuccess()
	{
		Assert.IsTrue( CommandCompletionPolicy.ShouldRejectAsStale(
			leaseIsCurrentAtCompletion: false,
			executionStarted: false,
			executionSucceeded: true ) );
	}

	[TestMethod]
	public void FailedExecutionUnderStaleLeaseRemainsRejected()
	{
		Assert.IsTrue( CommandCompletionPolicy.ShouldRejectAsStale(
			leaseIsCurrentAtCompletion: false,
			executionStarted: true,
			executionSucceeded: false ) );
	}

	[TestMethod]
	public void CurrentLeasePreservesOriginalOutcome()
	{
		Assert.IsFalse( CommandCompletionPolicy.ShouldRejectAsStale(
			leaseIsCurrentAtCompletion: true,
			executionStarted: false,
			executionSucceeded: false ) );
	}
}
