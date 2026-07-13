#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Policies;

namespace Hexagon.V2.Application;

public readonly record struct WorldPoint( float X, float Y, float Z )
{
	public float DistanceSquared( WorldPoint other )
	{
		var x = X - other.X;
		var y = Y - other.Y;
		var z = Z - other.Z;
		return x * x + y * y + z * z;
	}
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

	private readonly IServerInteractionWorld _world;
	private readonly IInteractionDirectory _directory;
	private readonly InteractionSessionService _sessions;
	private readonly InventoryAccessService _inventoryAccess;
	private readonly PolicyPipeline<ServerInteractionContext> _policy;
	private readonly IHexClock _clock;
	private readonly Func<InteractionSessionId> _createId;
	private readonly Dictionary<InteractionSessionId, DateTimeOffset> _lastValidation = new();
	private readonly Dictionary<InteractionSessionId, TimedActionTicket> _timedActions = new();

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
		var authorized = interactable.Authorize( context );
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
		var authorized = validated.Value.Interactable.Authorize( validated.Value.Context );
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
		var authorized = validated.Value.Interactable.Authorize( validated.Value.Context );
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
		if ( duration <= TimeSpan.Zero )
			return OperationResult<TimedActionTicket>.Failure( ErrorCode.InvalidArgument, "Timed action duration must be positive." );
		if ( target.Kind == InteractionTargetKind.Character && target.Id == characterId.Value )
			return OperationResult<TimedActionTicket>.Failure( ErrorCode.PolicyDenied, "Timed actions cannot target the acting character." );
		var validated = Validate( connectionId, accountId, characterId, target );
		if ( validated.Failed )
			return OperationResult<TimedActionTicket>.Failure( validated.Error!.Code, validated.Error.Message );
		var authorized = validated.Value.Interactable.Authorize( validated.Value.Context );
		if ( authorized.Failed )
			return OperationResult<TimedActionTicket>.Failure( authorized.Error!.Code, authorized.Error.Message );
		var ticket = new TimedActionTicket
		{
			Id = _createId(),
			ConnectionId = connectionId,
			AccountId = accountId,
			CharacterId = characterId,
			Target = target,
			StartedAt = _clock.UtcNow,
			Duration = duration
		};
		_timedActions[ticket.Id] = ticket;
		return OperationResult<TimedActionTicket>.Success( ticket );
	}

	public OperationResult<ServerInteractionContext> CompleteTimedAction(
		InteractionSessionId ticketId,
		ConnectionId connectionId,
		AccountId accountId,
		CharacterId characterId )
	{
		if ( !_timedActions.TryGetValue( ticketId, out var ticket ) ||
			ticket.ConnectionId != connectionId || ticket.AccountId != accountId || ticket.CharacterId != characterId )
			return OperationResult<ServerInteractionContext>.Failure( ErrorCode.Unauthorized, "Timed action ticket is stale or belongs to another actor." );
		if ( _clock.UtcNow - ticket.StartedAt < ticket.Duration )
			return OperationResult<ServerInteractionContext>.Failure( ErrorCode.Conflict, "Timed action has not completed." );
		_timedActions.Remove( ticketId );
		var validated = Validate( connectionId, accountId, characterId, ticket.Target );
		if ( validated.Failed )
			return OperationResult<ServerInteractionContext>.Failure( validated.Error!.Code, validated.Error.Message );
		var authorized = validated.Value.Interactable.Authorize( validated.Value.Context );
		return authorized.Succeeded
			? OperationResult<ServerInteractionContext>.Success( validated.Value.Context )
			: OperationResult<ServerInteractionContext>.Failure( authorized.Error!.Code, authorized.Error.Message );
	}

	public bool CancelTimedAction(
		InteractionSessionId ticketId,
		ConnectionId connectionId,
		CharacterId characterId )
	{
		if ( !_timedActions.TryGetValue( ticketId, out var ticket ) ||
			ticket.ConnectionId != connectionId || ticket.CharacterId != characterId )
			return false;
		_timedActions.Remove( ticketId );
		return true;
	}

	public int RevalidateActiveSessions()
	{
		var revoked = 0;
		_sessions.RevokeExpired();
		foreach ( var session in _sessions.ActiveSessions )
		{
			if ( _lastValidation.TryGetValue( session.Id, out var last ) &&
				_clock.UtcNow - last < RevalidationInterval ) continue;
			// Account identity is deliberately not recoverable from a session. The host
			// integration supplies the authenticated account during regular Continue calls;
			// scheduled validation uses the directory/world's connection binding.
			var hasTarget = _directory.TryResolve( session.Target, out var interactable ) &&
				interactable.Target == session.Target;
			if ( !hasTarget || !_world.TryBuildContext(
				session.ConnectionId, session.CharacterId, session.Target, out var context ) ||
				ValidateContext( context, interactable.Policy ).Failed ||
				interactable.Authorize( context ).Failed )
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

	private OperationResult ValidateContext( ServerInteractionContext context, InteractionPolicy? interactionPolicy )
	{
		if ( interactionPolicy is null )
			return OperationResult.Failure( ErrorCode.NotFound, "Interaction target policy is unavailable." );
		if ( interactionPolicy.RequireCharacter && context.CharacterId == default )
			return OperationResult.Failure( ErrorCode.Unauthorized, "An active character is required." );
		if ( interactionPolicy.RequireAlive && !context.IsAlive )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Dead characters cannot interact." );
		if ( interactionPolicy.RequireUnrestrained && context.IsRestrained )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Restrained characters cannot interact." );
		if ( interactionPolicy.MaxDistance <= 0f ||
			context.ActorPosition.DistanceSquared( context.TargetPosition ) >
			interactionPolicy.MaxDistance * interactionPolicy.MaxDistance )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Interaction target is out of range." );
		if ( interactionPolicy.RequireLineOfSight && !_world.HasLineOfSight( context ) )
			return OperationResult.Failure( ErrorCode.PolicyDenied, "Interaction target is not visible." );
		return _policy.Evaluate( context );
	}

	private void RemoveTimedActions( Func<TimedActionTicket, bool> predicate )
	{
		foreach ( var key in _timedActions.Where( pair => predicate( pair.Value ) ).Select( pair => pair.Key ).ToArray() )
			_timedActions.Remove( key );
	}

}
