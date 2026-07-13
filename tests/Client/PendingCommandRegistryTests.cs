#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Client;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Client;

[TestClass]
public sealed class PendingCommandRegistryTests
{
	[TestMethod]
	public async Task CorrelatedResultsCompleteOnlyTheirMatchingRequests()
	{
		using var registry = new PendingCommandRegistry( TimeSpan.FromSeconds( 5 ) );
		var first = registry.Register().Value;
		var second = registry.Register().Value;
		Assert.IsFalse( first.Completion.IsCompleted );
		Assert.IsFalse( second.Completion.IsCompleted );

		var denied = OperationResult.Failure( ErrorCode.PolicyDenied, "Denied." );
		Assert.IsTrue( registry.Complete( second.RequestId, denied ) );
		Assert.AreEqual( ErrorCode.PolicyDenied, (await second.Completion).Error!.Code );
		Assert.IsFalse( first.Completion.IsCompleted );

		Assert.IsTrue( registry.Complete( first.RequestId, OperationResult.Success() ) );
		Assert.IsTrue( (await first.Completion).Succeeded );
		Assert.AreEqual( 0, registry.Count );
	}

	[TestMethod]
	public async Task TimeoutProducesAStableFailureAndRemovesPendingRequest()
	{
		using var registry = new PendingCommandRegistry( TimeSpan.FromMilliseconds( 25 ) );
		var pending = registry.Register().Value;

		var result = await pending.Completion;

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( ErrorCode.InternalError, result.Error!.Code );
		StringAssert.Contains( result.Error.Message, "timed out" );
		Assert.AreEqual( 0, registry.Count );
		Assert.IsFalse( registry.Complete( pending.RequestId, OperationResult.Success() ) );
	}

	[TestMethod]
	public async Task CallerCancellationCancelsCompletionAndRemovesPendingRequest()
	{
		using var cancellation = new CancellationTokenSource();
		using var registry = new PendingCommandRegistry( TimeSpan.FromSeconds( 5 ) );
		var pending = registry.Register( cancellation.Token ).Value;

		cancellation.Cancel();

		await Assert.ThrowsExactlyAsync<TaskCanceledException>( () => pending.Completion.AsTask() );
		Assert.AreEqual( 0, registry.Count );
	}

	[TestMethod]
	public async Task DisposeCompletesEveryOutstandingRequest()
	{
		var registry = new PendingCommandRegistry( TimeSpan.FromSeconds( 5 ) );
		var first = registry.Register().Value;
		var second = registry.Register().Value;

		registry.Dispose();

		Assert.IsTrue( (await first.Completion).Failed );
		Assert.IsTrue( (await second.Completion).Failed );
		Assert.AreEqual( 0, registry.Count );
	}

	[TestMethod]
	public void PendingLimitFailsBeforeAllocatingAnotherRequest()
	{
		using var registry = new PendingCommandRegistry( TimeSpan.FromSeconds( 5 ), 1 );
		Assert.IsTrue( registry.Register().Succeeded );

		var rejected = registry.Register();

		Assert.IsTrue( rejected.Failed );
		Assert.AreEqual( ErrorCode.Conflict, rejected.Error!.Code );
		Assert.AreEqual( 1, registry.Count );
	}
}
