#nullable enable

using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class ApplicationConnectionLatchTests
{
	[TestMethod]
	public void EveryConditionOrderProducesExactlyOneNotificationAfterHello()
	{
		var conditions = new Func<ApplicationConnectionLatch, bool>[]
		{
			static latch => latch.ObserveNonceBound(),
			static latch => latch.ObserveHelloIssued(),
			static latch => latch.ObserveHostReady()
		};
		var permutations = new[]
		{
			new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
			new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
		};

		foreach ( var order in permutations )
		{
			var latch = new ApplicationConnectionLatch();
			var results = order.Select( index => conditions[index]( latch ) ).ToArray();
			CollectionAssert.AreEqual( new[] { false, false, true }, results );
			Assert.AreEqual( ApplicationConnectionState.Notifying, latch.State );
			Assert.IsFalse( latch.IsConnected );
			Assert.IsTrue( latch.CompleteNotification( true ) );
			Assert.IsTrue( latch.IsConnected );
			Assert.IsFalse( latch.ObserveHostReady() );
			Assert.IsFalse( latch.ObserveNonceBound() );
			Assert.IsFalse( latch.ObserveHelloIssued() );
		}
	}

	[TestMethod]
	public void CommandsRemainBlockedUntilNotificationSucceeds()
	{
		var latch = new ApplicationConnectionLatch();
		Assert.IsFalse( latch.ObserveNonceBound() );
		Assert.IsFalse( latch.ObserveHostReady() );
		Assert.IsFalse( latch.IsConnected );
		Assert.IsTrue( latch.ObserveHelloIssued() );
		Assert.IsFalse( latch.IsConnected );
		Assert.IsTrue( latch.CompleteNotification( true ) );
		Assert.IsTrue( latch.IsConnected );
	}

	[TestMethod]
	public void FailedOrDisconnectedNotificationCannotRestoreAuthority()
	{
		var failed = new ApplicationConnectionLatch();
		Assert.IsFalse( failed.ObserveNonceBound() );
		Assert.IsFalse( failed.ObserveHostReady() );
		Assert.IsTrue( failed.ObserveHelloIssued() );
		Assert.IsFalse( failed.CompleteNotification( false ) );
		Assert.AreEqual( ApplicationConnectionState.Failed, failed.State );
		Assert.IsFalse( failed.IsConnected );
		Assert.IsFalse( failed.ObserveNonceBound() );

		var disconnected = new ApplicationConnectionLatch();
		Assert.IsFalse( disconnected.ObserveHostReady() );
		Assert.IsFalse( disconnected.ObserveNonceBound() );
		Assert.IsTrue( disconnected.ObserveHelloIssued() );
		Assert.IsFalse( disconnected.Disconnect() );
		Assert.IsFalse( disconnected.CompleteNotification( true ) );
		Assert.AreEqual( ApplicationConnectionState.Disconnected, disconnected.State );
		Assert.IsFalse( disconnected.IsConnected );
	}

	[TestMethod]
	public async Task ConcurrentConditionsCannotLoseOrDuplicateNotification()
	{
		for ( var iteration = 0; iteration < 1_000; iteration++ )
		{
			var latch = new ApplicationConnectionLatch();
			var results = await Task.WhenAll(
				Task.Run( latch.ObserveNonceBound ),
				Task.Run( latch.ObserveHostReady ),
				Task.Run( latch.ObserveHelloIssued ) );
			Assert.AreEqual( 1, results.Count( value => value ) );
			Assert.IsTrue( latch.CompleteNotification( true ) );
			Assert.IsTrue( latch.IsConnected );
		}
	}
}
