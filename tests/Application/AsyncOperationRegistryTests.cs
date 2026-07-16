#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class AsyncOperationRegistryTests
{
	[TestMethod]
	public async Task CompletedOperationsSelfPruneBeforeDrain()
	{
		var registry = new AsyncOperationRegistry();

		Assert.IsTrue( registry.TryStart( "value", static () => ValueTask.CompletedTask ) );
		Assert.IsTrue( registry.TryStartTask( "task", static () => Task.CompletedTask ) );
		Assert.AreEqual( 0, registry.ActiveOperationCount );

		var drained = await registry.DrainAsync();

		Assert.IsTrue( drained.Succeeded );
		Assert.AreEqual( 2, drained.AcceptedOperationCount );
		Assert.AreEqual( 2, drained.CompletedOperationCount );
		Assert.IsEmpty( drained.Failures );
	}

	[TestMethod]
	public async Task DrainAggregatesSynchronousAsynchronousAndCancellationFailures()
	{
		var registry = new AsyncOperationRegistry();
		var cancellation = new CancellationToken( canceled: true );

		Assert.IsTrue( registry.TryStartTask( "synchronous", static () =>
			throw new InvalidOperationException( "sync" ) ) );
		Assert.IsTrue( registry.TryStartTask( "asynchronous", static () =>
			Task.FromException( new ArgumentException( "async" ) ) ) );
		Assert.IsTrue( registry.TryStartTask( "canceled", () => Task.FromCanceled( cancellation ) ) );

		var drained = await registry.DrainAsync();

		Assert.IsFalse( drained.Succeeded );
		Assert.AreEqual( 3, drained.AcceptedOperationCount );
		Assert.AreEqual( 3, drained.CompletedOperationCount );
		Assert.HasCount( 3, drained.Failures );
		CollectionAssert.AreEquivalent(
			new[] { "synchronous", "asynchronous", "canceled" },
			drained.Failures.Select( value => value.Name ).ToArray() );
		Assert.IsTrue( drained.Failures.Single( value => value.Name == "canceled" ).WasCanceled );
		Assert.AreEqual( 0, registry.ActiveOperationCount );
	}

	[TestMethod]
	public async Task StopAndDrainAreIdempotentAndRejectNewWorkWhileWaiting()
	{
		var registry = new AsyncOperationRegistry();
		var release = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		Assert.IsTrue( registry.TryStartTask( "pending", () => release.Task ) );

		Assert.IsTrue( registry.StopAdmission() );
		Assert.IsFalse( registry.StopAdmission() );
		var first = registry.DrainAsync().AsTask();
		var second = registry.DrainAsync().AsTask();
		Assert.AreSame( first, second );
		Assert.IsFalse( registry.IsAccepting );
		Assert.IsFalse( registry.TryStartTask( "late", static () => Task.CompletedTask ) );
		Assert.IsFalse( first.IsCompleted );

		release.SetResult();
		var drained = await first;

		Assert.IsTrue( drained.Succeeded );
		Assert.AreEqual( 1, drained.AcceptedOperationCount );
		Assert.AreEqual( 1, drained.CompletedOperationCount );
		Assert.AreEqual( 0, registry.ActiveOperationCount );
		Assert.AreSame( drained, await second );
	}
}
