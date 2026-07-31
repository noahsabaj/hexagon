#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;

namespace Hexagon.V2.Application;

public readonly record struct WorldPoint
{
	public WorldPoint( float x, float y, float z )
	{
		if ( !float.IsFinite( x ) ) throw new ArgumentOutOfRangeException( nameof(x), "Coordinate must be finite." );
		if ( !float.IsFinite( y ) ) throw new ArgumentOutOfRangeException( nameof(y), "Coordinate must be finite." );
		if ( !float.IsFinite( z ) ) throw new ArgumentOutOfRangeException( nameof(z), "Coordinate must be finite." );
		X = x;
		Y = y;
		Z = z;
	}

	public float X { get; }
	public float Y { get; }
	public float Z { get; }

	public double DistanceSquared( WorldPoint other )
	{
		var x = (double)X - other.X;
		var y = (double)Y - other.Y;
		var z = (double)Z - other.Z;
		return x * x + y * y + z * z;
	}
}

public readonly record struct InteractionRange
{
	public InteractionRange( double value )
	{
		if ( !double.IsFinite( value ) || value <= 0d )
			throw new ArgumentOutOfRangeException( nameof(value), "Interaction range must be finite and positive." );
		Value = value;
		Squared = value * value;
		if ( !double.IsFinite( Squared ) )
			throw new ArgumentOutOfRangeException( nameof(value), "Interaction range is too large." );
	}

	public double Value { get; }
	public double Squared { get; }
}

public sealed record ServerInteractionContext
{
	public required ConnectionId ConnectionId { get; init; }
	public required AccountId AccountId { get; init; }
	public required CharacterId CharacterId { get; init; }
	public required InteractionTarget Target { get; init; }
	public required WorldPoint ActorPosition { get; init; }
	public required WorldPoint TargetPosition { get; init; }
	public required bool IsAlive { get; init; }
	public required bool IsRestrained { get; init; }
}

public sealed record InteractionInventoryGrant(
	InventoryId InventoryId,
	InventoryCapability Capabilities,
	InventoryGrantKind Kind );

public sealed record InteractionOffer
{
	public InteractionSessionKind? SessionKind { get; init; }
	public IReadOnlyList<InteractionInventoryGrant> InventoryGrants { get; init; } =
		Array.Empty<InteractionInventoryGrant>();
}

public interface IHexInteractable
{
	InteractionTarget Target { get; }
	InteractionPolicy Policy { get; }
	OperationResult<InteractionOffer> Authorize( ServerInteractionContext context );
}

public interface IInteractionDirectory
{
	bool TryResolve( InteractionTarget target, out IHexInteractable interactable );
}

/// <summary>
/// Infrastructure reconstructs this context from the host-owned controller and
/// authoritative target. Client-provided transforms are never accepted.
/// </summary>
public interface IServerInteractionWorld
{
	bool TryBuildContext(
		ConnectionId connectionId,
		CharacterId characterId,
		InteractionTarget target,
		out ServerInteractionContext context );
	bool HasLineOfSight( ServerInteractionContext context );
}

public sealed record InteractionOpened(
	InteractionOffer Offer,
	InteractionSession? Session );

public sealed record TimedActionTicket
{
	public required InteractionSessionId Id { get; init; }
	public required ConnectionId ConnectionId { get; init; }
	public required AccountId AccountId { get; init; }
	public required CharacterId CharacterId { get; init; }
	public required InteractionTarget Target { get; init; }
	public required DateTimeOffset StartedAt { get; init; }
	public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Host-only interaction validator and session/capability coordinator. The same
/// validator is used for one-shot, continuing and timed-action completion paths.
/// </summary>
public sealed class InteractionAuthorityService
{
	public static readonly TimeSpan RevalidationInterval = TimeSpan.FromMilliseconds( 250 );
	public static readonly TimeSpan MaximumTimedActionDuration = TimeSpan.FromMinutes( 5 );
	public static readonly TimeSpan TimedActionCompletionGrace = TimeSpan.FromSeconds( 15 );

	private readonly IServerInteractionWorld _world;
	private readonly IInteractionDirectory _directory;
	private readonly InteractionSessionService _sessions;
	private readonly InventoryAccessService _inventoryAccess;
	private readonly PolicyPipeline<ServerInteractionContext> _policy;
	private readonly IHexClock _clock;
	private readonly Func<InteractionSessionId> _createId;
	private readonly Dictionary<InteractionSessionId, DateTimeOffset> _lastValidation = new();
	private readonly object _timedActionSync = new();
	private readonly Dictionary<InteractionSessionId, TimedActionTicket> _timedActions = new();
	private readonly Dictionary<TimedActionActor, InteractionSessionId> _timedActionActors = new();
	private readonly PriorityQueue<InteractionSessionId, long> _timedActionExpiry = new();

	public InteractionAuthorityService(
		IServerInteractionWorld world,
		IInteractionDirectory directory,
		InteractionSessionService sessions,
		InventoryAccessService inventoryAccess,
		PolicyPipeline<ServerInteractionContext> policy,
		IHexClock clock,
		Func<InteractionSessionId>? createId = null )
	{
		_world = world ?? throw new ArgumentNullException( nameof(world) );
		_directory = directory ?? throw new ArgumentNullException( nameof(directory) );
		_sessions = sessions ?? throw new ArgumentNullException( nameof(sessions) );
		_inventoryAccess = inventoryAccess ?? throw new ArgumentNullException( nameof(inventoryAccess) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_createId = createId ?? InteractionSessionId.New;
		_sessions.SessionRevoked += session =>
		{
			_inventoryAccess.RevokeSession( session.Id );
			_lastValidation.Remove( session.Id );
		};
	}

	public OperationResult<InteractionOpened> Begin(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		InteractionTarget target )
	{
		var validated = Validate( connectionId, accountId, characterId, target );
		if ( validated.Failed )
			return OperationResult<InteractionOpened>.Failure( validated.Error!.Code, validated.Error.Message );
		var interactable = validated.Value.Interactable;
		var context = validated.Value.Context;
		var authorized = AuthorizeFailClosed( interactable, context );
		if ( authorized.Failed )
			return OperationResult<InteractionOpened>.Failure( authorized.Error!.Code, authorized.Error.Message );

		var offer = authorized.Value;
		if ( offer.InventoryGrants.Count > 0 && offer.SessionKind is null )
			return OperationResult<InteractionOpened>.Failure(
				ErrorCode.InternalError, "Continuing inventory access requires a server session." );
		if ( offer.InventoryGrants.Any( grant =>
			grant.Kind is not InventoryGrantKind.InteractionSession and not InventoryGrantKind.NestedBag ) )
			return OperationResult<InteractionOpened>.Failure(
				ErrorCode.InternalError, "Interactable attempted to issue a non-session inventory grant." );
		InteractionSession? session = null;
		if ( offer.SessionKind is not null )
		{
			if ( interactable.Policy.SessionKind != offer.SessionKind )
				return OperationResult<InteractionOpened>.Failure(
					ErrorCode.InternalError, "Interactable returned a session kind that its policy did not declare." );
			session = _sessions.Open( offer.SessionKind.Value, connectionId, characterId, target );
			_lastValidation[session.Id] = _clock.UtcNow;
		}

		foreach ( var grant in offer.InventoryGrants )
		{
			_inventoryAccess.Grant( new InventoryGrant
			{
				ConnectionId = connectionId,
				CharacterId = characterId,
				InventoryId = grant.InventoryId,
				Capabilities = grant.Capabilities,
				Kind = grant.Kind,
				SessionId = session!.Id
			} );
		}

		return OperationResult<InteractionOpened>.Success( new InteractionOpened( offer, session ) );
	}

	/// <summary>
	/// Reconstructs and authorizes a single immediate interaction without opening
	/// a continuing session or issuing inventory grants. Mutation services use
	/// this at their commit boundary when an operation still requires live range,
	/// line-of-sight, actor, target, and policy proof.
	/// </summary>
	public OperationResult<ServerInteractionContext> AuthorizeOneShot(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		InteractionTarget target )
	{
		var validated = Validate( connectionId, accountId, characterId, target );
		if ( validated.Failed )
			return OperationResult<ServerInteractionContext>.Failure(
				validated.Error!.Code, validated.Error.Message );
		var authorized = AuthorizeFailClosed( validated.Value.Interactable, validated.Value.Context );
		return authorized.Succeeded
			? OperationResult<ServerInteractionContext>.Success( validated.Value.Context )
			: OperationResult<ServerInteractionContext>.Failure(
				authorized.Error!.Code, authorized.Error.Message );
	}

	public OperationResult<InteractionSession> Continue(
		InteractionSessionId sessionId,
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		InteractionTarget target )
	{
		if ( !_sessions.TryTouch( sessionId, connectionId, characterId, target, out var session ) )
			return OperationResult<InteractionSession>.Failure( ErrorCode.Unauthorized, "Interaction session is stale or not bound to the actor." );
		var validated = Validate( connectionId, accountId, characterId, target );
		if ( validated.Failed )
		{
			_sessions.Revoke( sessionId, "validation_failed" );
			return OperationResult<InteractionSession>.Failure( validated.Error!.Code, validated.Error.Message );
		}
		var authorized = AuthorizeFailClosed( validated.Value.Interactable, validated.Value.Context );
		if ( authorized.Failed )
		{
			_sessions.Revoke( sessionId, "authorization_changed" );
			return OperationResult<InteractionSession>.Failure( authorized.Error!.Code, authorized.Error.Message );
		}
		_lastValidation[sessionId] = _clock.UtcNow;
		return OperationResult<InteractionSession>.Success( session );
	}

	public OperationResult<TimedActionTicket> BeginTimedAction(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		InteractionTarget target,
		TimeSpan duration )
	{
		if ( duration <= TimeSpan.Zero || duration > MaximumTimedActionDuration )
			return OperationResult<TimedActionTicket>.Failure(
				ErrorCode.InvalidArgument,
				$"Timed action duration must be positive and no greater than {MaximumTimedActionDuration.TotalMinutes:0} minutes." );
		if ( target.Kind == InteractionTargetKind.Character && target.Id == characterId.Value )
			return OperationResult<TimedActionTicket>.Failure( ErrorCode.PolicyDenied, "Timed actions cannot target the acting character." );
		var actor = new TimedActionActor( connectionId, characterId );
		lock ( _timedActionSync )
		{
			SweepTimedActionsUnsafe( _clock.UtcNow );
			if ( _timedActionActors.ContainsKey( actor ) )
				return OperationResult<TimedActionTicket>.Failure( ErrorCode.Conflict, "The actor already has an active timed action." );
		}
		var validated = Validate( connectionId, accountId, characterId, target );
		if ( validated.Failed )
			return OperationResult<TimedActionTicket>.Failure( validated.Error!.Code, validated.Error.Message );
		var authorized = AuthorizeFailClosed( validated.Value.Interactable, validated.Value.Context );
		if ( authorized.Failed )
			return OperationResult<TimedActionTicket>.Failure( authorized.Error!.Code, authorized.Error.Message );
		var startedAt = _clock.UtcNow;
		var ticket = new TimedActionTicket
		{
			Id = _createId(),
			ConnectionId = connectionId,
			AccountId = accountId,
			CharacterId = characterId,
			Target = target,
			StartedAt = startedAt,
			Duration = duration
		};
		lock ( _timedActionSync )
		{
			SweepTimedActionsUnsafe( _clock.UtcNow );
			if ( _timedActionActors.ContainsKey( actor ) )
				return OperationResult<TimedActionTicket>.Failure( ErrorCode.Conflict, "The actor already has an active timed action." );
			_timedActions.Add( ticket.Id, ticket );
			_timedActionActors.Add( actor, ticket.Id );
			var expiresAt = startedAt + duration + TimedActionCompletionGrace;
			_timedActionExpiry.Enqueue( ticket.Id, expiresAt.UtcTicks );
		}
		return OperationResult<TimedActionTicket>.Success( ticket );
	}

	public OperationResult<ServerInteractionContext> CompleteTimedAction(
		InteractionSessionId ticketId,
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId )
	{
		TimedActionTicket ticket;
		lock ( _timedActionSync )
		{
			var now = _clock.UtcNow;
			SweepTimedActionsUnsafe( now );
			if ( !_timedActions.TryGetValue( ticketId, out ticket! ) ||
				ticket.ConnectionId != connectionId || ticket.AccountId != accountId || ticket.CharacterId != characterId )
				return OperationResult<ServerInteractionContext>.Failure( ErrorCode.Unauthorized, "Timed action ticket is stale or belongs to another actor." );
			if ( now - ticket.StartedAt < ticket.Duration )
				return OperationResult<ServerInteractionContext>.Failure( ErrorCode.Conflict, "Timed action has not completed." );
			RemoveTimedActionUnsafe( ticket );
		}
		var validated = Validate( connectionId, accountId, characterId, ticket.Target );
		if ( validated.Failed )
			return OperationResult<ServerInteractionContext>.Failure( validated.Error!.Code, validated.Error.Message );
		var authorized = AuthorizeFailClosed( validated.Value.Interactable, validated.Value.Context );
		return authorized.Succeeded
			? OperationResult<ServerInteractionContext>.Success( validated.Value.Context )
			: OperationResult<ServerInteractionContext>.Failure( authorized.Error!.Code, authorized.Error.Message );
	}

	public bool CancelTimedAction(
		InteractionSessionId ticketId,
		ConnectionId connectionId,
		CharacterId characterId )
	{
		lock ( _timedActionSync )
		{
			SweepTimedActionsUnsafe( _clock.UtcNow );
			if ( !_timedActions.TryGetValue( ticketId, out var ticket ) ||
				ticket.ConnectionId != connectionId || ticket.CharacterId != characterId )
				return false;
			RemoveTimedActionUnsafe( ticket );
			return true;
		}
	}

	public int RevalidateActiveSessions()
	{
		SweepTimedActions();
		var revoked = 0;
		_sessions.RevokeExpired();
		foreach ( var session in _sessions.ActiveSessions )
		{
			if ( _lastValidation.TryGetValue( session.Id, out var last ) &&
				_clock.UtcNow - last < RevalidationInterval ) continue;
			// Account identity is deliberately not recoverable from a session. The host
			// integration supplies the authenticated account during regular Continue calls;
			// scheduled validation uses the directory/world's connection binding.
			bool valid;
			try
			{
				valid = _directory.TryResolve( session.Target, out var interactable ) &&
					interactable.Target == session.Target &&
					_world.TryBuildContext(
						session.ConnectionId, session.CharacterId, session.Target, out var context ) &&
					ValidateContext( context, interactable.Policy ).Succeeded &&
					interactable.Authorize( context ) is { Succeeded: true };
			}
			catch ( Exception )
			{
				// A throwing interactable must not starve the whole maintenance loop, and a
				// session whose own validation cannot run is revoked fail-closed instead of
				// surviving unvalidated.
				if ( _sessions.Revoke( session.Id, "scheduled_validation_threw" ) ) revoked++;
				continue;
			}
			if ( !valid )
			{
				if ( _sessions.Revoke( session.Id, "scheduled_validation_failed" ) ) revoked++;
			}
			else
			{
				_lastValidation[session.Id] = _clock.UtcNow;
			}
		}
		return revoked;
	}

	public void Close( InteractionSessionId sessionId ) => _sessions.Revoke( sessionId, "closed" );

	public void CharacterChanged( ConnectionId connectionId, CharacterId previousCharacter )
	{
		_sessions.RevokeCharacter( connectionId, previousCharacter, "character_changed" );
		_inventoryAccess.RevokeCharacter( connectionId, previousCharacter );
		RemoveTimedActions( ticket =>
			ticket.ConnectionId == connectionId && ticket.CharacterId == previousCharacter );
	}

	public void Disconnected( ConnectionId connectionId )
	{
		_sessions.RevokeConnection( connectionId, "disconnected" );
		_inventoryAccess.RevokeConnection( connectionId );
		RemoveTimedActions( ticket => ticket.ConnectionId == connectionId );
	}

	public void TargetInvalidated( InteractionTarget target, string reason )
	{
		_sessions.RevokeTarget( target, reason );
		RemoveTimedActions( ticket => ticket.Target == target );
	}

	private OperationResult<(IHexInteractable Interactable, ServerInteractionContext Context)> Validate(
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId,
		InteractionTarget target )
	{
		// Directory, world, and interactable members are game-supplied; a throw from any of
		// them denies the interaction instead of escaping into the caller (mirrors
		// PolicyPipeline's fail-closed treatment of throwing policies).
		try
		{
			if ( !_directory.TryResolve( target, out var interactable ) || interactable.Target != target )
				return OperationResult<(IHexInteractable, ServerInteractionContext)>.Failure( ErrorCode.NotFound, "Interaction target is unavailable." );
			if ( !_world.TryBuildContext( connectionId, characterId, target, out var context ) ||
				context.AccountId != accountId )
				return OperationResult<(IHexInteractable, ServerInteractionContext)>.Failure( ErrorCode.Unauthorized, "Authoritative actor or target state is unavailable." );
			var contextValidation = ValidateContext( context, interactable.Policy );
			if ( contextValidation.Failed )
				return OperationResult<(IHexInteractable, ServerInteractionContext)>.Failure(
					contextValidation.Error!.Code, contextValidation.Error.Message );
			return OperationResult<(IHexInteractable, ServerInteractionContext)>.Success( (interactable, context) );
		}
		catch ( Exception exception )
		{
			return OperationResult<(IHexInteractable, ServerInteractionContext)>.Failure(
				ErrorCode.InternalError,
				$"Interaction validation failed closed: {exception.GetType().Name}: {exception.Message}" );
		}
	}

	private static OperationResult<InteractionOffer> AuthorizeFailClosed(
		IHexInteractable interactable,
		ServerInteractionContext context )
	{
		try
		{
			return interactable.Authorize( context );
		}
		catch ( Exception exception )
		{
			return OperationResult<InteractionOffer>.Failure(
				ErrorCode.InternalError,
				$"Interactable authorization failed closed: {exception.GetType().Name}: {exception.Message}" );
		}
	}

	private OperationResult ValidateContext( ServerInteractionContext context, InteractionPolicy? interactionPolicy )
	{
		if ( interactionPolicy is null )
			return OperationResult.Failure( ErrorCode.NotFound, "Interaction target policy is unavailable." );
		if ( context.ActorPosition.DistanceSquared( context.TargetPosition ) > interactionPolicy.Range.Squared )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Interaction target is out of range." );
		if ( interactionPolicy.RequireCharacter && context.CharacterId == default )
			return OperationResult.Failure( ErrorCode.Unauthorized, "An active character is required." );
		if ( interactionPolicy.RequireAlive && !context.IsAlive )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Dead characters cannot interact." );
		if ( interactionPolicy.RequireUnrestrained && context.IsRestrained )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Restrained characters cannot interact." );
		if ( interactionPolicy.RequireLineOfSight && !_world.HasLineOfSight( context ) )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Interaction target is not visible." );
		return _policy.Evaluate( context );
	}

	private void RemoveTimedActions( Func<TimedActionTicket, bool> predicate )
	{
		lock ( _timedActionSync )
		{
			SweepTimedActionsUnsafe( _clock.UtcNow );
			foreach ( var ticket in _timedActions.Values.Where( predicate ).ToArray() )
				RemoveTimedActionUnsafe( ticket );
		}
	}

	public int TrackedTimedActionCount
	{
		get
		{
			lock ( _timedActionSync )
			{
				SweepTimedActionsUnsafe( _clock.UtcNow );
				return _timedActions.Count;
			}
		}
	}

	public int QueuedTimedActionExpiryCount
	{
		get
		{
			lock ( _timedActionSync ) return _timedActionExpiry.Count;
		}
	}

	public int SweepTimedActions()
	{
		lock ( _timedActionSync ) return SweepTimedActionsUnsafe( _clock.UtcNow );
	}

	private int SweepTimedActionsUnsafe( DateTimeOffset now )
	{
		var removed = 0;
		while ( _timedActionExpiry.TryPeek( out var id, out var expiresAt ) && expiresAt < now.UtcTicks )
		{
			_timedActionExpiry.Dequeue();
			if ( !_timedActions.TryGetValue( id, out var ticket ) ) continue;
			var actualExpiry = ticket.StartedAt + ticket.Duration + TimedActionCompletionGrace;
			if ( actualExpiry > now )
			{
				_timedActionExpiry.Enqueue( id, actualExpiry.UtcTicks );
				continue;
			}
			RemoveTimedActionUnsafe( ticket );
			removed++;
		}
		return removed;
	}

	private void RemoveTimedActionUnsafe( TimedActionTicket ticket )
	{
		_timedActions.Remove( ticket.Id );
		_timedActionActors.Remove( new TimedActionActor( ticket.ConnectionId, ticket.CharacterId ) );
		CompactExpiryQueueUnsafe();
	}

	private void CompactExpiryQueueUnsafe()
	{
		var maximumQueueSize = Math.Max( 64L, (long)_timedActions.Count * 2 + 16 );
		if ( _timedActionExpiry.Count <= maximumQueueSize ) return;
		_timedActionExpiry.Clear();
		foreach ( var current in _timedActions.Values )
		{
			var expiresAt = current.StartedAt + current.Duration + TimedActionCompletionGrace;
			_timedActionExpiry.Enqueue( current.Id, expiresAt.UtcTicks );
		}
	}

	private readonly record struct TimedActionActor(
		ConnectionId ConnectionId,
		CharacterId CharacterId );

}
