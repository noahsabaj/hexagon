#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class ConnectionSessionBoundaryTests
{
	[TestMethod]
	public void CharacterTransitionCancelsStableLeaseButNotConnectionLease()
	{
		using var boundary = new ConnectionSessionBoundary( ConnectionEpoch.New() );
		var firstCharacter = CharacterId.New();
		var stable = boundary.Capture( firstCharacter, true );
		var connection = boundary.Capture( firstCharacter, false );

		boundary.ObserveCharacter( CharacterId.New() );

		Assert.IsTrue( stable.CancellationToken.IsCancellationRequested );
		Assert.IsFalse( connection.CancellationToken.IsCancellationRequested );
		Assert.IsFalse( boundary.IsCurrent( stable ) );
		Assert.IsTrue( boundary.IsCurrent( connection ) );
	}

	[TestMethod]
	public void DisconnectCancelsEveryLeaseAndRejectsCurrentChecks()
	{
		using var boundary = new ConnectionSessionBoundary( ConnectionEpoch.New() );
		var stable = boundary.Capture( CharacterId.New(), true );
		var connection = boundary.Capture( boundary.CharacterId, false );

		boundary.Disconnect();

		Assert.IsTrue( stable.CancellationToken.IsCancellationRequested );
		Assert.IsTrue( connection.CancellationToken.IsCancellationRequested );
		Assert.IsFalse( boundary.IsCurrent( stable ) );
		Assert.IsFalse( boundary.IsCurrent( connection ) );
	}

	[TestMethod]
	public void PublishedEpochOrdersStateAndOnlyAdvancesCharacterOnIdentityChange()
	{
		using var boundary = new ConnectionSessionBoundary( ConnectionEpoch.New() );
		var character = CharacterId.New();

		var first = boundary.Publish( null );
		var second = boundary.Publish( character );
		var third = boundary.Publish( character );
		var fourth = boundary.Publish( null );

		Assert.AreEqual( 0L, first.Character );
		Assert.AreEqual( 1L, second.Character );
		Assert.AreEqual( 1L, third.Character );
		Assert.AreEqual( 2L, fourth.Character );
		Assert.AreEqual( 1L, first.Revision );
		Assert.AreEqual( 4L, fourth.Revision );
		Assert.AreEqual( first.Connection, fourth.Connection );
	}

	[TestMethod]
	public void ThrowingCancellationCallbackCannotEscapeDisconnectBoundary()
	{
		var diagnostics = new List<Exception>();
		using var boundary = new ConnectionSessionBoundary( ConnectionEpoch.New(), diagnostics.Add );
		var lease = boundary.Capture( CharacterId.New(), true );
		using var registration = lease.CancellationToken.Register( () => throw new InvalidOperationException( "callback" ) );

		boundary.Disconnect();

		Assert.IsTrue( lease.CancellationToken.IsCancellationRequested );
		Assert.HasCount( 1, diagnostics );
	}
}
