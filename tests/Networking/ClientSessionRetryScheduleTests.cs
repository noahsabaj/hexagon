#nullable enable

using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class ClientSessionRetryScheduleTests
{
	[TestMethod]
	public void AttemptsImmediatelyThenBacksOffToTheTwoSecondCap()
	{
		var schedule = new ClientSessionRetrySchedule( 1_000 );

		Assert.IsTrue( schedule.TryBeginAttempt( 0 ) );
		Assert.IsFalse( schedule.TryBeginAttempt( 249 ) );
		Assert.IsTrue( schedule.TryBeginAttempt( 250 ) );
		Assert.IsFalse( schedule.TryBeginAttempt( 749 ) );
		Assert.IsTrue( schedule.TryBeginAttempt( 750 ) );
		Assert.IsFalse( schedule.TryBeginAttempt( 1_749 ) );
		Assert.IsTrue( schedule.TryBeginAttempt( 1_750 ) );
		Assert.IsFalse( schedule.TryBeginAttempt( 3_749 ) );
		Assert.IsTrue( schedule.TryBeginAttempt( 3_750 ) );
		Assert.IsFalse( schedule.TryBeginAttempt( 5_749 ) );
		Assert.IsTrue( schedule.TryBeginAttempt( 5_750 ) );
	}

	[TestMethod]
	public void ExactHelloOrDisposalStopsEveryLaterAttempt()
	{
		var hello = new ClientSessionRetrySchedule( 1_000 );
		Assert.IsTrue( hello.TryBeginAttempt( 0 ) );
		Assert.IsTrue( hello.Complete() );
		Assert.IsFalse( hello.Complete() );
		Assert.IsTrue( hello.IsComplete );
		Assert.IsFalse( hello.TryBeginAttempt( long.MaxValue ) );

		var disposed = new ClientSessionRetrySchedule( 1_000 );
		Assert.IsTrue( disposed.Complete() );
		Assert.IsFalse( disposed.TryBeginAttempt( 0 ) );
	}

	[TestMethod]
	public async Task ConcurrentPollingCannotBeginMoreThanOneAttemptPerWindow()
	{
		for ( var iteration = 0; iteration < 1_000; iteration++ )
		{
			var schedule = new ClientSessionRetrySchedule( 1_000 );
			var initial = await Task.WhenAll(
				Enumerable.Range( 0, 8 ).Select( _ => Task.Run( () => schedule.TryBeginAttempt( 0 ) ) ) );
			Assert.AreEqual( 1, initial.Count( result => result ) );

			var retry = await Task.WhenAll(
				Enumerable.Range( 0, 8 ).Select( _ => Task.Run( () => schedule.TryBeginAttempt( 250 ) ) ) );
			Assert.AreEqual( 1, retry.Count( result => result ) );
		}
	}
}
