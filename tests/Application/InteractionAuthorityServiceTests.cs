#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class InteractionAuthorityServiceTests
{
	[TestMethod]
	public void ContinuingSessionRejectsEveryStaleActorBinding()
	{
		var fixture = new InteractionFixture();
		var inventoryId = InventoryId.New();
		fixture.SetGrant( inventoryId );
		var session = fixture.BeginSession();

		var wrongConnection = fixture.Service.Continue(
			session.Id,
			ConnectionId.New(),
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );
		var wrongCharacter = fixture.Service.Continue(
			session.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			CharacterId.New(),
			fixture.Target );
		var wrongTarget = fixture.Service.Continue(
			session.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			InteractionTarget.SceneEntity( SceneEntityId.New() ) );
		var legitimate = fixture.Service.Continue(
			session.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );
		var forgedAccount = fixture.Service.Continue(
			session.Id,
			fixture.Actor.ConnectionId,
			new AccountId( fixture.Actor.AccountId.Value + 1 ),
			fixture.Actor.CharacterId,
			fixture.Target );

		Assert.AreEqual( ErrorCode.Unauthorized, wrongConnection.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, wrongCharacter.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, wrongTarget.Error!.Code );
		Assert.IsTrue( legitimate.Succeeded, legitimate.Error?.Message );
		Assert.AreEqual( ErrorCode.Unauthorized, forgedAccount.Error!.Code );
		Assert.IsFalse( fixture.HasGrant( inventoryId ) );
	}

	[TestMethod]
	public void BeginReconstructsAndRejectsOutOfRangeOrOccludedTargets()
	{
		var fixture = new InteractionFixture();
		fixture.World.TargetPosition = new WorldPoint( 131, 0, 0 );

		var distant = fixture.Service.Begin(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );
		fixture.World.TargetPosition = new WorldPoint( 130, 0, 0 );
		fixture.World.LineOfSight = false;
		var occluded = fixture.Service.Begin(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );

		Assert.AreEqual( ErrorCode.PolicyDenied, distant.Error!.Code );
		Assert.AreEqual( ErrorCode.PolicyDenied, occluded.Error!.Code );
		Assert.IsEmpty( fixture.Sessions.ActiveSessions );
	}

	[TestMethod]
	public void NonFiniteGeometryCannotBeConstructedAndHugeFiniteDistanceShortCircuitsAuthorization()
	{
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new WorldPoint( float.NaN, 0, 0 ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new WorldPoint( 0, float.PositiveInfinity, 0 ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new InteractionRange( double.NaN ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new InteractionRange( double.PositiveInfinity ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new InteractionRange( 0 ) );

		var fixture = new InteractionFixture();
		fixture.World.TargetPosition = new WorldPoint( float.MaxValue, float.MaxValue, float.MaxValue );
		var result = fixture.Service.Begin(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );

		Assert.AreEqual( ErrorCode.PolicyDenied, result.Error!.Code );
		Assert.AreEqual( 0, fixture.World.LineOfSightCount );
		Assert.AreEqual( 0, fixture.Interactable.AuthorizeCount );
	}

	[TestMethod]
	public void OneShotAuthorizationRevalidatesWithoutOpeningSessionOrGrantingInventory()
	{
		var fixture = new InteractionFixture();
		var inventoryId = InventoryId.New();
		fixture.SetGrant( inventoryId );

		var authorized = fixture.Service.AuthorizeOneShot(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target );

		Assert.IsTrue( authorized.Succeeded, authorized.Error?.Message );
		Assert.IsEmpty( fixture.Sessions.ActiveSessions );
		Assert.IsFalse( fixture.HasGrant( inventoryId ) );
		Assert.AreEqual( 1, fixture.World.BuildCount );
		Assert.AreEqual( 1, fixture.Interactable.AuthorizeCount );
	}

	[TestMethod]
	public void ScheduledValidationRunsAtFourHertzAndRevokesInvalidTargetGrant()
	{
		var fixture = new InteractionFixture();
		var inventoryId = InventoryId.New();
		fixture.SetGrant( inventoryId );
		fixture.BeginSession();
		var baselineBuilds = fixture.World.BuildCount;
		fixture.World.Available = false;

		var immediate = fixture.Service.RevalidateActiveSessions();
		fixture.Clock.Advance( TimeSpan.FromMilliseconds( 249 ) );
		var early = fixture.Service.RevalidateActiveSessions();
		fixture.Clock.Advance( TimeSpan.FromMilliseconds( 1 ) );
		var due = fixture.Service.RevalidateActiveSessions();

		Assert.AreEqual( 0, immediate );
		Assert.AreEqual( 0, early );
		Assert.AreEqual( baselineBuilds + 1, fixture.World.BuildCount );
		Assert.AreEqual( 1, due );
		Assert.IsEmpty( fixture.Sessions.ActiveSessions );
		Assert.IsFalse( fixture.HasGrant( inventoryId ) );
	}

	[TestMethod]
	public void SessionExpiresAtSixtyIdleSecondsAndRevokesGrant()
	{
		var fixture = new InteractionFixture();
		var inventoryId = InventoryId.New();
		fixture.SetGrant( inventoryId );
		fixture.BeginSession();
		fixture.Clock.Advance( InteractionSessionService.DefaultIdleTimeout );

		fixture.Service.RevalidateActiveSessions();

		Assert.IsEmpty( fixture.Sessions.ActiveSessions );
		Assert.IsFalse( fixture.HasGrant( inventoryId ) );
	}

	[TestMethod]
	public void TimedActionRejectsSelfAndRevalidatesAtCompletion()
	{
		var fixture = new InteractionFixture();
		var buildsBeforeSelfAttempt = fixture.World.BuildCount;

		var self = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			InteractionTarget.Character( fixture.Actor.CharacterId ),
			TimeSpan.FromSeconds( 3 ) );
		var valid = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 3 ) );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 3 ) );
		fixture.World.LineOfSight = false;
		var completed = fixture.Service.CompleteTimedAction(
			valid.Value.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId );

		Assert.AreEqual( ErrorCode.PolicyDenied, self.Error!.Code );
		Assert.AreEqual( buildsBeforeSelfAttempt + 2, fixture.World.BuildCount );
		Assert.AreEqual( ErrorCode.PolicyDenied, completed.Error!.Code );
	}

	[TestMethod]
	public void TimedActionValidatesAtBothStartAndSuccessfulCompletion()
	{
		var fixture = new InteractionFixture();

		var ticket = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 3 ) );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 3 ) );
		var completed = fixture.Service.CompleteTimedAction(
			ticket.Value.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId );

		Assert.IsTrue( ticket.Succeeded, ticket.Error?.Message );
		Assert.IsTrue( completed.Succeeded, completed.Error?.Message );
		Assert.AreEqual( 2, fixture.Interactable.AuthorizeCount );
		Assert.AreEqual( 2, fixture.World.BuildCount );
	}

	[TestMethod]
	public void TimedActionCancellationRequiresTheBoundActorAndMakesTicketStale()
	{
		var fixture = new InteractionFixture();
		var ticket = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 3 ) ).Value;

		Assert.IsFalse( fixture.Service.CancelTimedAction(
			ticket.Id, ConnectionId.New(), fixture.Actor.CharacterId ) );
		Assert.IsTrue( fixture.Service.CancelTimedAction(
			ticket.Id, fixture.Actor.ConnectionId, fixture.Actor.CharacterId ) );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 3 ) );
		var completed = fixture.Service.CompleteTimedAction(
			ticket.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId );

		Assert.AreEqual( ErrorCode.Unauthorized, completed.Error!.Code );
	}

	[TestMethod]
	public void TimedActionsEnforceActorCapMaximumDurationAndCompletionGrace()
	{
		var fixture = new InteractionFixture();
		var tooLong = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			InteractionAuthorityService.MaximumTimedActionDuration + TimeSpan.FromTicks( 1 ) );
		var first = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 1 ) );
		var second = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 1 ) );

		Assert.AreEqual( ErrorCode.InvalidArgument, tooLong.Error!.Code );
		Assert.IsTrue( first.Succeeded );
		Assert.AreEqual( ErrorCode.Conflict, second.Error!.Code );
		Assert.AreEqual( 1, fixture.Service.TrackedTimedActionCount );

		fixture.Clock.Advance(
			TimeSpan.FromSeconds( 1 ) + InteractionAuthorityService.TimedActionCompletionGrace + TimeSpan.FromTicks( 1 ) );
		Assert.AreEqual( 1, fixture.Service.SweepTimedActions() );
		Assert.AreEqual( 0, fixture.Service.TrackedTimedActionCount );
	}

	[TestMethod]
	public async Task ConcurrentTimedBeginsPermitExactlyOneTicketPerActor()
	{
		var fixture = new InteractionFixture();
		var gate = new ManualResetEventSlim();
		var first = Task.Run( () =>
		{
			gate.Wait();
			return fixture.Service.BeginTimedAction(
				fixture.Actor.ConnectionId, fixture.Actor.AccountId, fixture.Actor.CharacterId,
				fixture.Target, TimeSpan.FromSeconds( 1 ) );
		} );
		var second = Task.Run( () =>
		{
			gate.Wait();
			return fixture.Service.BeginTimedAction(
				fixture.Actor.ConnectionId, fixture.Actor.AccountId, fixture.Actor.CharacterId,
				fixture.Target, TimeSpan.FromSeconds( 1 ) );
		} );
		gate.Set();
		var results = await Task.WhenAll( first, second );

		Assert.AreEqual( 1, results.Count( result => result.Succeeded ) );
		Assert.AreEqual( 1, results.Count( result => result.Error?.Code == ErrorCode.Conflict ) );
		Assert.AreEqual( 1, fixture.Service.TrackedTimedActionCount );
	}

	[TestMethod]
	public void TimedActionExpiryQueueRemainsBoundedUnderCancellationChurn()
	{
		var fixture = new InteractionFixture();
		for ( var index = 0; index < 200; index++ )
		{
			var ticket = fixture.Service.BeginTimedAction(
				fixture.Actor.ConnectionId, fixture.Actor.AccountId, fixture.Actor.CharacterId,
				fixture.Target, TimeSpan.FromMinutes( 5 ) ).Value;
			Assert.IsTrue( fixture.Service.CancelTimedAction(
				ticket.Id, fixture.Actor.ConnectionId, fixture.Actor.CharacterId ) );
		}

		Assert.AreEqual( 0, fixture.Service.TrackedTimedActionCount );
		Assert.IsLessThanOrEqualTo( 64, fixture.Service.QueuedTimedActionExpiryCount );
	}

	[TestMethod]
	public void InvalidatingTimedTargetMakesTicketStale()
	{
		var fixture = new InteractionFixture();
		var ticket = fixture.Service.BeginTimedAction(
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId,
			fixture.Target,
			TimeSpan.FromSeconds( 1 ) );
		fixture.Service.TargetInvalidated( fixture.Target, "destroyed" );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 1 ) );

		var completed = fixture.Service.CompleteTimedAction(
			ticket.Value.Id,
			fixture.Actor.ConnectionId,
			fixture.Actor.AccountId,
			fixture.Actor.CharacterId );

		Assert.AreEqual( ErrorCode.Unauthorized, completed.Error!.Code );
	}

	[TestMethod]
	public void CloseCharacterChangeDisconnectAndTargetInvalidationRevokeSessionGrants()
	{
		var fixture = new InteractionFixture();

		var closedInventory = InventoryId.New();
		fixture.SetGrant( closedInventory );
		var closed = fixture.BeginSession();
		Assert.IsTrue( fixture.HasGrant( closedInventory ) );
		fixture.Service.Close( closed.Id );
		Assert.IsFalse( fixture.HasGrant( closedInventory ) );

		var changedInventory = InventoryId.New();
		fixture.SetGrant( changedInventory );
		fixture.BeginSession();
		Assert.IsTrue( fixture.HasGrant( changedInventory ) );
		fixture.Service.CharacterChanged( fixture.Actor.ConnectionId, fixture.Actor.CharacterId );
		Assert.IsFalse( fixture.HasGrant( changedInventory ) );

		var disconnectedInventory = InventoryId.New();
		fixture.SetGrant( disconnectedInventory );
		fixture.BeginSession();
		Assert.IsTrue( fixture.HasGrant( disconnectedInventory ) );
		fixture.Service.Disconnected( fixture.Actor.ConnectionId );
		Assert.IsFalse( fixture.HasGrant( disconnectedInventory ) );

		var invalidTargetInventory = InventoryId.New();
		fixture.SetGrant( invalidTargetInventory );
		fixture.BeginSession();
		Assert.IsTrue( fixture.HasGrant( invalidTargetInventory ) );
		fixture.Service.TargetInvalidated( fixture.Target, "destroyed" );
		Assert.IsFalse( fixture.HasGrant( invalidTargetInventory ) );
		Assert.IsEmpty( fixture.Sessions.ActiveSessions );
	}

	private sealed class InteractionFixture
	{
		public InteractionFixture()
		{
			Actor = ApplicationServiceTestEnvironment.Actor();
			Target = InteractionTarget.SceneEntity( SceneEntityId.New() );
			Clock = new MutableClock();
			World = new FakeWorld( Actor, Target );
			Interactable = new FakeInteractable( Target );
			Directory = new FakeDirectory( Interactable );
			Sessions = new InteractionSessionService( Clock );
			Access = new InventoryAccessService();
			Service = new InteractionAuthorityService(
				World,
				Directory,
				Sessions,
				Access,
				ApplicationServiceTestEnvironment.AllowPolicy<ServerInteractionContext>(),
				Clock );
		}

		public InventoryActor Actor { get; }
		public InteractionTarget Target { get; }
		public MutableClock Clock { get; }
		public FakeWorld World { get; }
		public FakeInteractable Interactable { get; }
		public FakeDirectory Directory { get; }
		public InteractionSessionService Sessions { get; }
		public InventoryAccessService Access { get; }
		public InteractionAuthorityService Service { get; }

		public void SetGrant( InventoryId inventoryId )
		{
			Interactable.Offer = new InteractionOffer
			{
				SessionKind = InteractionSessionKind.Storage,
				InventoryGrants = new[]
				{
					new InteractionInventoryGrant(
						inventoryId,
						InventoryCapability.View | InventoryCapability.TransferOut,
						InventoryGrantKind.InteractionSession )
				}
			};
		}

		public InteractionSession BeginSession()
		{
			var opened = Service.Begin(
				Actor.ConnectionId,
				Actor.AccountId,
				Actor.CharacterId,
				Target );
			Assert.IsTrue( opened.Succeeded, opened.Error?.Message );
			Assert.IsNotNull( opened.Value.Session );
			return opened.Value.Session;
		}

		public bool HasGrant( InventoryId inventoryId ) => Access.Has(
			Actor.ConnectionId,
			Actor.CharacterId,
			inventoryId,
			InventoryCapability.View );
	}

	private sealed class FakeWorld : IServerInteractionWorld
	{
		private readonly InventoryActor _actor;
		private readonly InteractionTarget _target;

		public FakeWorld( InventoryActor actor, InteractionTarget target )
		{
			_actor = actor;
			_target = target;
		}

		public bool Available { get; set; } = true;
		public bool LineOfSight { get; set; } = true;
		public bool IsAlive { get; set; } = true;
		public bool IsRestrained { get; set; }
		public WorldPoint ActorPosition { get; set; } = new( 0, 0, 0 );
		public WorldPoint TargetPosition { get; set; } = new( 10, 0, 0 );
		public int BuildCount { get; private set; }
		public int LineOfSightCount { get; private set; }

		public bool TryBuildContext(
			ConnectionId connectionId,
			CharacterId characterId,
			InteractionTarget target,
			out ServerInteractionContext context )
		{
			BuildCount++;
			if ( !Available || connectionId != _actor.ConnectionId ||
				characterId != _actor.CharacterId || target != _target )
			{
				context = default!;
				return false;
			}

			context = new ServerInteractionContext
			{
				ConnectionId = _actor.ConnectionId,
				AccountId = _actor.AccountId,
				CharacterId = _actor.CharacterId,
				Target = _target,
				ActorPosition = ActorPosition,
				TargetPosition = TargetPosition,
				IsAlive = IsAlive,
				IsRestrained = IsRestrained
			};
			return true;
		}

		public bool HasLineOfSight( ServerInteractionContext context )
		{
			LineOfSightCount++;
			return LineOfSight;
		}
	}

	private sealed class FakeDirectory : IInteractionDirectory
	{
		private readonly IReadOnlyDictionary<InteractionTarget, IHexInteractable> _interactables;

		public FakeDirectory( IHexInteractable interactable ) =>
			_interactables = new Dictionary<InteractionTarget, IHexInteractable>
			{
				[interactable.Target] = interactable
			};

		public bool TryResolve( InteractionTarget target, out IHexInteractable interactable ) =>
			_interactables.TryGetValue( target, out interactable! );
	}

	private sealed class FakeInteractable : IHexInteractable
	{
		public FakeInteractable( InteractionTarget target ) => Target = target;

		public InteractionTarget Target { get; }
		public InteractionPolicy Policy { get; } = new()
		{
			SessionKind = InteractionSessionKind.Storage
		};
		public InteractionOffer Offer { get; set; } = new()
		{
			SessionKind = InteractionSessionKind.Storage
		};
		public int AuthorizeCount { get; private set; }

		public OperationResult<InteractionOffer> Authorize( ServerInteractionContext context )
		{
			AuthorizeCount++;
			return OperationResult<InteractionOffer>.Success( Offer );
		}
	}

	private sealed class MutableClock : IHexClock
	{
		public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
		public void Advance( TimeSpan elapsed ) => UtcNow += elapsed;
	}
}
