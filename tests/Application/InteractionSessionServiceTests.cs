using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class InteractionSessionServiceTests
{
	[TestMethod]
	public void OpeningSameKindReplacesPreviousSession()
	{
		var clock = new FakeClock();
		var service = new InteractionSessionService( clock );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var revoked = new List<InteractionSession>();
		service.SessionRevoked += revoked.Add;

		var first = service.Open( InteractionSessionKind.Storage, connection, character, SceneEntityId.New() );
		var second = service.Open( InteractionSessionKind.Storage, connection, character, SceneEntityId.New() );

		Assert.AreNotEqual( first.Id, second.Id );
		Assert.HasCount( 1, revoked );
		Assert.AreEqual( first.Id, revoked[0].Id );
	}

	[TestMethod]
	public void SessionCannotBeReusedByAnotherCharacter()
	{
		var clock = new FakeClock();
		var service = new InteractionSessionService( clock );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var target = SceneEntityId.New();
		var session = service.Open( InteractionSessionKind.Vendor, connection, character, target );

		Assert.IsFalse( service.TryTouch( session.Id, connection, CharacterId.New(), target, out _ ) );
	}

	[TestMethod]
	public void IdleSessionExpiresFailClosed()
	{
		var clock = new FakeClock();
		var service = new InteractionSessionService( clock, TimeSpan.FromSeconds( 5 ) );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var target = SceneEntityId.New();
		var session = service.Open( InteractionSessionKind.Scanner, connection, character, target );
		clock.Advance( TimeSpan.FromSeconds( 6 ) );

		Assert.IsFalse( service.TryTouch( session.Id, connection, character, target, out _ ) );
		Assert.AreEqual( 0, service.TrackedSessionCount );
	}

	[TestMethod]
	public void TerminalSessionChurnDoesNotRetainHistoryAndCallbacksRemainExactOnce()
	{
		var clock = new FakeClock();
		var service = new InteractionSessionService( clock );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var revoked = new List<InteractionSession>();
		service.SessionRevoked += revoked.Add;

		for ( var index = 0; index < 2_000; index++ )
		{
			var session = service.Open(
				InteractionSessionKind.Storage,
				connection,
				character,
				SceneEntityId.New() );
			Assert.IsTrue( service.Revoke( session.Id, "closed" ) );
			Assert.IsFalse( service.Revoke( session.Id, "duplicate" ) );
		}

		Assert.HasCount( 2_000, revoked );
		Assert.IsTrue( revoked.All( session => session.Revoked && session.RevokeReason == "closed" ) );
		Assert.AreEqual( 0, service.TrackedSessionCount );
		Assert.IsEmpty( service.ActiveSessions );
	}

	[TestMethod]
	public void ReplacementRemovesTerminalRecordBeforePublishingCallback()
	{
		var service = new InteractionSessionService( new FakeClock() );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var callbackCounts = new List<int>();
		service.SessionRevoked += _ => callbackCounts.Add( service.TrackedSessionCount );

		service.Open( InteractionSessionKind.Vendor, connection, character, SceneEntityId.New() );
		service.Open( InteractionSessionKind.Vendor, connection, character, SceneEntityId.New() );

		CollectionAssert.AreEqual( new[] { 1 }, callbackCounts );
		Assert.AreEqual( 1, service.TrackedSessionCount );
	}

	[TestMethod]
	public void SessionProofRejectsRevocationExpiryAndIdentifierReuse()
	{
		var clock = new FakeClock();
		var repeatedId = InteractionSessionId.New();
		var service = new InteractionSessionService(
			clock,
			TimeSpan.FromSeconds( 5 ),
			() => repeatedId );
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var target = InteractionTarget.SceneEntity( SceneEntityId.New() );
		var first = service.Open( InteractionSessionKind.Vendor, connection, character, target );
		var revokedProof = service.Prove( first )!;

		Assert.IsTrue( revokedProof.IsCurrent() );
		Assert.IsNull( revokedProof.Validate( null! ) );
		Assert.IsTrue( service.Revoke( first.Id, "closed" ) );
		Assert.IsFalse( revokedProof.IsCurrent() );
		Assert.IsNotNull( revokedProof.Validate( null! ) );
		var replacement = service.Open( InteractionSessionKind.Vendor, connection, character, target );
		Assert.AreEqual( repeatedId, replacement.Id );
		Assert.IsNotNull( revokedProof.Validate( null! ), "A reused identifier must not revive an older proof." );

		var expiryProof = service.Prove( replacement )!;
		clock.Advance( TimeSpan.FromSeconds( 5 ) );
		Assert.IsFalse( expiryProof.IsCurrent() );
		Assert.IsNotNull( expiryProof.Validate( null! ) );
		Assert.IsNull( service.Prove( replacement ) );
	}

	private sealed class FakeClock : IHexClock
	{
		public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
		public void Advance( TimeSpan time ) => UtcNow += time;
	}
}
