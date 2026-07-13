#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

public enum InteractionSessionKind
{
	Door,
	Storage,
	Vendor,
	NestedBag,
	CharacterSearch,
	Scanner
}

public enum InteractionTargetKind
{
	SceneEntity,
	Inventory,
	Item,
	Character
}

public readonly record struct InteractionTarget
{
	public InteractionTargetKind Kind { get; }
	public Guid Id { get; }

	[JsonConstructor]
	public InteractionTarget( InteractionTargetKind kind, Guid id )
	{
		if ( !Enum.IsDefined( kind ) ) throw new ArgumentOutOfRangeException( nameof(kind) );
		if ( id == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(id) );
		Kind = kind;
		Id = id;
	}

	public static InteractionTarget SceneEntity( SceneEntityId id ) => new( InteractionTargetKind.SceneEntity, id.Value );
	public static InteractionTarget Inventory( InventoryId id ) => new( InteractionTargetKind.Inventory, id.Value );
	public static InteractionTarget Item( ItemId id ) => new( InteractionTargetKind.Item, id.Value );
	public static InteractionTarget Character( CharacterId id ) => new( InteractionTargetKind.Character, id.Value );
}

public sealed record InteractionPolicy
{
	public float MaxDistance { get; init; } = 130f;
	public bool RequireLineOfSight { get; init; } = true;
	public bool RequireCharacter { get; init; } = true;
	public bool RequireAlive { get; init; } = true;
	public bool RequireUnrestrained { get; init; } = true;
	public InteractionSessionKind? SessionKind { get; init; }
}

public sealed record InteractionSession
{
	public required InteractionSessionId Id { get; init; }
	public required InteractionSessionKind Kind { get; init; }
	public required ConnectionId ConnectionId { get; init; }
	public required CharacterId CharacterId { get; init; }
	public required InteractionTarget Target { get; init; }
	public required DateTimeOffset OpenedAt { get; init; }
	public required DateTimeOffset LastActivityAt { get; init; }
	public bool Revoked { get; init; }
	public string? RevokeReason { get; init; }
}

/// <summary>
/// Owns continuing interaction capabilities. At most one live session of each kind
/// is allowed per connection; opening another atomically revokes the previous one.
/// </summary>
public sealed class InteractionSessionService
{
	public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds( 60 );

	private readonly object _sync = new();
	private readonly IHexClock _clock;
	private readonly TimeSpan _idleTimeout;
	private readonly Func<InteractionSessionId> _createId;
	private readonly Dictionary<InteractionSessionId, InteractionSession> _sessions = new();

	public event Action<InteractionSession>? SessionRevoked;

	public InteractionSessionService(
		IHexClock clock,
		TimeSpan? idleTimeout = null,
		Func<InteractionSessionId>? createId = null )
	{
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_idleTimeout = idleTimeout ?? DefaultIdleTimeout;
		_createId = createId ?? InteractionSessionId.New;
		if ( _idleTimeout <= TimeSpan.Zero ) throw new ArgumentOutOfRangeException( nameof(idleTimeout) );
	}

	public InteractionSession Open(
		InteractionSessionKind kind,
		ConnectionId connectionId,
		CharacterId characterId,
		InteractionTarget target )
	{
		InteractionSession[] replaced;
		InteractionSession session;

		lock ( _sync )
		{
			var previous = _sessions.Values
				.Where( value => !value.Revoked && value.ConnectionId == connectionId && value.Kind == kind )
				.ToArray();
			replaced = new InteractionSession[previous.Length];
			for ( var i = 0; i < previous.Length; i++ )
				replaced[i] = RevokeUnsafe( previous[i], "replaced" );

			var now = _clock.UtcNow;
			session = new InteractionSession
			{
				Id = _createId(),
				Kind = kind,
				ConnectionId = connectionId,
				CharacterId = characterId,
				Target = target,
				OpenedAt = now,
				LastActivityAt = now
			};
			_sessions.Add( session.Id, session );
		}

		foreach ( var old in replaced ) SessionRevoked?.Invoke( old );
		return session;
	}

	public InteractionSession Open(
		InteractionSessionKind kind,
		ConnectionId connectionId,
		CharacterId characterId,
		SceneEntityId targetId ) =>
		Open( kind, connectionId, characterId, InteractionTarget.SceneEntity( targetId ) );

	public bool TryTouch(
		InteractionSessionId sessionId,
		ConnectionId connectionId,
		CharacterId characterId,
		InteractionTarget target,
		out InteractionSession session )
	{
		InteractionSession? expired = null;
		lock ( _sync )
		{
			if ( !_sessions.TryGetValue( sessionId, out var current )
				|| current.Revoked
				|| current.ConnectionId != connectionId
				|| current.CharacterId != characterId
				|| current.Target != target )
			{
				session = default!;
				return false;
			}

			if ( _clock.UtcNow - current.LastActivityAt >= _idleTimeout )
			{
				expired = RevokeUnsafe( current, "idle_timeout" );
				session = default!;
			}
			else
			{
				session = current with { LastActivityAt = _clock.UtcNow };
				_sessions[sessionId] = session;
				return true;
			}
		}

		if ( expired is not null ) SessionRevoked?.Invoke( expired );
		return false;
	}

	public bool TryTouch(
		InteractionSessionId sessionId,
		ConnectionId connectionId,
		CharacterId characterId,
		SceneEntityId targetId,
		out InteractionSession session ) =>
		TryTouch( sessionId, connectionId, characterId, InteractionTarget.SceneEntity( targetId ), out session );

	public IReadOnlyList<InteractionSession> ActiveSessions
	{
		get
		{
			lock ( _sync ) return _sessions.Values.ToArray();
		}
	}

	/// <summary>
	/// Number of live capability records retained by the service. Revoked sessions
	/// are removed before callbacks run, so this count cannot grow with history.
	/// </summary>
	public int TrackedSessionCount
	{
		get
		{
			lock ( _sync ) return _sessions.Count;
		}
	}

	public IReadOnlyList<InteractionSession> RevokeConnection( ConnectionId connectionId, string reason ) =>
		RevokeWhere( value => value.ConnectionId == connectionId, reason );

	public IReadOnlyList<InteractionSession> RevokeCharacter( ConnectionId connectionId, CharacterId characterId, string reason ) =>
		RevokeWhere( value => value.ConnectionId == connectionId && value.CharacterId == characterId, reason );

	public IReadOnlyList<InteractionSession> RevokeTarget( InteractionTarget target, string reason ) =>
		RevokeWhere( value => value.Target == target, reason );

	public bool Revoke( InteractionSessionId sessionId, string reason )
	{
		InteractionSession? revoked = null;
		lock ( _sync )
		{
			if ( _sessions.TryGetValue( sessionId, out var current ) && !current.Revoked )
				revoked = RevokeUnsafe( current, reason );
		}

		if ( revoked is null ) return false;
		SessionRevoked?.Invoke( revoked );
		return true;
	}

	public IReadOnlyList<InteractionSession> RevokeExpired()
	{
		var now = _clock.UtcNow;
		return RevokeWhere( value => now - value.LastActivityAt >= _idleTimeout, "idle_timeout" );
	}

	private IReadOnlyList<InteractionSession> RevokeWhere( Func<InteractionSession, bool> predicate, string reason )
	{
		InteractionSession[] revoked;
		lock ( _sync )
		{
			var matching = _sessions.Values
				.Where( value => !value.Revoked && predicate( value ) )
				.ToArray();
			revoked = new InteractionSession[matching.Length];
			for ( var i = 0; i < matching.Length; i++ )
				revoked[i] = RevokeUnsafe( matching[i], reason );
		}

		foreach ( var session in revoked ) SessionRevoked?.Invoke( session );
		return revoked;
	}

	private InteractionSession RevokeUnsafe( InteractionSession session, string reason )
	{
		var revoked = session with { Revoked = true, RevokeReason = reason };
		_sessions.Remove( session.Id );
		return revoked;
	}
}
