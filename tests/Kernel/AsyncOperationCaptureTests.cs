using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Kernel;

[TestClass]
public sealed class AsyncOperationCaptureTests
{
	[TestMethod]
	public async Task SynchronousThrowIsCapturedAsAFailureOutcome()
	{
		var thrown = new InvalidOperationException( "boom" );
		var outcome = await AsyncOperation.Capture( () => throw thrown );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public async Task SynchronousThrowIsCapturedAsAFailureOutcomeWithValue()
	{
		var thrown = new InvalidOperationException( "boom" );
		var outcome = await AsyncOperation.Capture<int>( () => throw thrown );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public void CompletedOperationTakesTheSynchronousFastPath()
	{
		var task = AsyncOperation.Capture( () => ValueTask.CompletedTask );

		Assert.IsTrue( task.IsCompletedSuccessfully, "A completed ValueTask must not allocate a continuation." );
		Assert.IsTrue( task.Result.Succeeded );
	}

	[TestMethod]
	public void CompletedOperationWithValueTakesTheSynchronousFastPath()
	{
		var task = AsyncOperation.Capture( () => ValueTask.FromResult( 42 ) );

		Assert.IsTrue( task.IsCompletedSuccessfully, "A completed ValueTask must not allocate a continuation." );
		Assert.IsTrue( task.Result.Succeeded );
		Assert.AreEqual( 42, task.Result.Value );
	}

	[TestMethod]
	public async Task AsynchronousFaultIsUnwrappedFromItsAggregateException()
	{
		var thrown = new InvalidOperationException( "inner" );
		var outcome = await AsyncOperation.Capture(
			() => new ValueTask( Task.FromException( thrown ) ) );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public async Task AsynchronousFaultWithValueIsUnwrappedFromItsAggregateException()
	{
		var thrown = new InvalidOperationException( "inner" );
		var outcome = await AsyncOperation.Capture(
			() => new ValueTask<int>( Task.FromException<int>( thrown ) ) );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public async Task ThrowAfterAnAwaitPointIsCaptured()
	{
		var thrown = new InvalidOperationException( "late" );
		var outcome = await AsyncOperation.Capture( async () =>
		{
			await Task.Yield();
			throw thrown;
		} );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public async Task CancellationBecomesAFailureOutcomeInsteadOfAThrow()
	{
		var canceled = new CancellationToken( canceled: true );
		var outcome = await AsyncOperation.Capture(
			() => new ValueTask( Task.FromCanceled( canceled ) ) );

		Assert.IsFalse( outcome.Succeeded );
		Assert.IsInstanceOfType<OperationCanceledException>( outcome.Exception );
	}

	[TestMethod]
	public async Task CancellationWithValueBecomesAFailureOutcomeInsteadOfAThrow()
	{
		var canceled = new CancellationToken( canceled: true );
		var outcome = await AsyncOperation.Capture(
			() => new ValueTask<int>( Task.FromCanceled<int>( canceled ) ) );

		Assert.IsFalse( outcome.Succeeded );
		Assert.IsInstanceOfType<OperationCanceledException>( outcome.Exception );
	}

	[TestMethod]
	public void CaptureSynchronousConvertsAThrowingActionIntoAFailure()
	{
		var thrown = new InvalidOperationException( "boom" );
		var outcome = AsyncOperation.CaptureSynchronous( () => throw thrown );

		Assert.IsFalse( outcome.Succeeded );
		Assert.AreSame( thrown, outcome.Exception );
	}

	[TestMethod]
	public void CaptureSynchronousReturnsTheProducedValueOnSuccess()
	{
		var outcome = AsyncOperation.CaptureSynchronous( () => 42 );

		Assert.IsTrue( outcome.Succeeded );
		Assert.AreEqual( 42, outcome.Value );
	}
}
