#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Application;

public sealed record ChatRateLimit( int Capacity, TimeSpan RefillPeriod )
{
	public static ChatRateLimit Default { get; } = new( 4, TimeSpan.FromSeconds( 5 ) );
}

public interface IChatAdmissionReservation : IDisposable
{
	void Commit( DateTimeOffset nowUtc );
}

public interface IChatAdmissionService
{
	OperationResult<IChatAdmissionReservation> Reserve(
		ConnectionId connectionId,
		string channelId,
		ChatRateLimit globalLimit,
		ChatRateLimit channelLimit,
		DateTimeOffset nowUtc );
	void Revoke( ConnectionId connectionId );
}

/// <summary>
/// Shared reservation-capable global/channel token buckets. Reservations hold
/// capacity immediately to close concurrent bypasses, but cancellation restores
/// both tokens so failed transactions do not consume chat admission.
/// </summary>
public sealed class ChatAdmissionService : IChatAdmissionService
{
	private readonly object _sync = new();
	private readonly Dictionary<ConnectionId, TokenBucket> _global = new();
	private readonly Dictionary<(ConnectionId Connection, string Channel), TokenBucket> _channels = new();

	public OperationResult<IChatAdmissionReservation> Reserve(
		ConnectionId connectionId,
		string channelId,
		ChatRateLimit globalLimit,
		ChatRateLimit channelLimit,
		DateTimeOffset nowUtc )
	{
		if ( string.IsNullOrWhiteSpace( channelId ) )
			return OperationResult<IChatAdmissionReservation>.Failure(
				ErrorCode.InvalidArgument, "Chat admission channel is required." );
		ValidateLimit( globalLimit );
		ValidateLimit( channelLimit );
		lock ( _sync )
		{
			var global = GetBucket( _global, connectionId, globalLimit, nowUtc );
			var channel = GetBucket( _channels, (connectionId, channelId), channelLimit, nowUtc );
			if ( !global.TryReserve( nowUtc ) )
				return OperationResult<IChatAdmissionReservation>.Failure(
					ErrorCode.Conflict, "Global chat rate limit exceeded." );
			if ( !channel.TryReserve( nowUtc ) )
			{
				global.Release();
				return OperationResult<IChatAdmissionReservation>.Failure(
					ErrorCode.Conflict, "Channel chat rate limit exceeded." );
			}
			return OperationResult<IChatAdmissionReservation>.Success(
				new Reservation( this, global, channel ) );
		}
	}

	public void Revoke( ConnectionId connectionId )
	{
		lock ( _sync )
		{
			_global.Remove( connectionId );
			foreach ( var key in _channels.Keys.Where( value => value.Connection == connectionId ).ToArray() )
				_channels.Remove( key );
		}
	}

	private void Finish(
		TokenBucket global,
		TokenBucket channel,
		bool committed,
		DateTimeOffset committedAtUtc )
	{
		lock ( _sync )
		{
			if ( committed )
			{
				global.Commit( committedAtUtc );
				channel.Commit( committedAtUtc );
			}
			else
			{
				global.Release();
				channel.Release();
			}
		}
	}

	private static TokenBucket GetBucket<TKey>(
		IDictionary<TKey, TokenBucket> buckets,
		TKey key,
		ChatRateLimit limit,
		DateTimeOffset nowUtc ) where TKey : notnull
	{
		if ( buckets.TryGetValue( key, out var existing ) )
		{
			if ( existing.Limit != limit )
				throw new InvalidOperationException( "Chat admission limit changed without revoking its connection." );
			return existing;
		}
		var created = new TokenBucket( limit, nowUtc );
		buckets.Add( key, created );
		return created;
	}

	private static void ValidateLimit( ChatRateLimit limit )
	{
		ArgumentNullException.ThrowIfNull( limit );
		if ( limit.Capacity <= 0 || limit.RefillPeriod <= TimeSpan.Zero )
			throw new ArgumentOutOfRangeException( nameof(limit), "Chat admission limit must be positive." );
	}

	private sealed class TokenBucket
	{
		private double _tokens;
		private int _pending;
		private DateTimeOffset _lastRefill;
		public TokenBucket( ChatRateLimit limit, DateTimeOffset nowUtc )
		{
			Limit = limit;
			_tokens = limit.Capacity;
			_lastRefill = nowUtc;
		}
		public ChatRateLimit Limit { get; }
		public bool TryReserve( DateTimeOffset nowUtc )
		{
			Refill( nowUtc );
			if ( _tokens - _pending < 1d ) return false;
			_pending++;
			return true;
		}
		public void Release()
		{
			if ( _pending <= 0 ) throw new InvalidOperationException( "Chat admission reservation is unbalanced." );
			_pending--;
		}
		public void Commit( DateTimeOffset nowUtc )
		{
			if ( _pending <= 0 ) throw new InvalidOperationException( "Chat admission reservation is unbalanced." );
			Refill( nowUtc );
			_pending--;
			_tokens = Math.Max( 0d, _tokens - 1d );
		}
		private void Refill( DateTimeOffset nowUtc )
		{
			if ( nowUtc <= _lastRefill ) return;
			var elapsed = (nowUtc - _lastRefill).TotalSeconds;
			_tokens = Math.Min(
				Limit.Capacity,
				_tokens + elapsed * Limit.Capacity / Limit.RefillPeriod.TotalSeconds );
			_lastRefill = nowUtc;
		}
	}

	private sealed class Reservation : IChatAdmissionReservation
	{
		private readonly ChatAdmissionService _owner;
		private readonly TokenBucket _global;
		private readonly TokenBucket _channel;
		private bool _committed;
		private DateTimeOffset _committedAtUtc;
		private bool _disposed;
		public Reservation( ChatAdmissionService owner, TokenBucket global, TokenBucket channel )
		{
			_owner = owner;
			_global = global;
			_channel = channel;
		}
		public void Commit( DateTimeOffset nowUtc )
		{
			if ( _disposed ) throw new ObjectDisposedException( nameof(Reservation) );
			_committed = true;
			_committedAtUtc = nowUtc;
		}
		public void Dispose()
		{
			if ( _disposed ) return;
			_disposed = true;
			_owner.Finish( _global, _channel, _committed, _committedAtUtc );
		}
	}
}

public sealed record ChatChannelRule
{
	public required string Id { get; init; }
	public ChatRateLimit RateLimit { get; init; } = ChatRateLimit.Default;
	public float? Range { get; init; }
}

public sealed record LiveInventoryItemView(
	ItemId ItemId,
	CharacterId? OwningCharacterId,
	DefinitionId Definition,
	IReadOnlyDictionary<string, TypedPayload> Traits );

public sealed record LiveInventorySnapshot(
	long Version,
	IReadOnlyList<LiveInventoryItemView> Items );

public interface ILiveInventoryView
{
	LiveInventorySnapshot Capture();
}

public sealed class CanonicalLiveInventoryView : ILiveInventoryView
{
	private readonly object _sync = new();
	private LiveInventorySnapshot _current = new( 0, Array.Empty<LiveInventoryItemView>() );

	public LiveInventorySnapshot Capture()
	{
		lock ( _sync ) return _current;
	}

	public void Publish( long version, IEnumerable<LiveInventoryItemView> items )
	{
		if ( version < 0 ) throw new ArgumentOutOfRangeException( nameof(version) );
		ArgumentNullException.ThrowIfNull( items );
		var materialized = items.OrderBy( item => item.ItemId.Value ).ToArray();
		if ( materialized.Select( item => item.ItemId ).Distinct().Count() != materialized.Length )
			throw new ArgumentException( "Live inventory items must be unique.", nameof(items) );
		lock ( _sync )
		{
			if ( version < _current.Version )
				throw new InvalidOperationException( "Live inventory view cannot move backwards." );
			_current = new LiveInventorySnapshot( version, Array.AsReadOnly( materialized ) );
		}
	}
}

public interface IPermissionAuthorizer
{
	bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId );
}

public sealed record ChatSendContext(
	InventoryActor Actor,
	CharacterRecord Character,
	string ChannelId,
	string Text );

public sealed record ChatDelivery(
	Guid MessageId,
	string ChannelId,
	CharacterId AuthorCharacterId,
	string AuthorName,
	string Text,
	DateTimeOffset SentAtUtc,
	IReadOnlyList<ConnectionId> Recipients );

public interface IChatRecipientResolver
{
	IReadOnlyList<ConnectionId> Resolve(
		ChatSendContext context,
		ChatChannelRule rule,
		LiveInventorySnapshot inventory );
}

public sealed record ChatDeliveredEvent( ChatDelivery Delivery );

/// <summary>
/// Host chat boundary with fail-closed definitions/permissions, Unicode-scalar
/// limits and per-connection token buckets. Recipient resolution captures the
/// live inventory view exactly once and never queries persistence.
/// </summary>
public sealed class ChatService
{
	public const int MaximumUnicodeScalars = 512;

	private readonly CompiledSchema _schema;
	private readonly IReadOnlyDictionary<string, ChatChannelRule> _rules;
	private readonly IPermissionAuthorizer _permissions;
	private readonly IChatRecipientResolver _recipients;
	private readonly ILiveInventoryView _inventory;
	private readonly IHexClock _clock;
	private readonly PolicyPipeline<ChatSendContext> _policy;
	private readonly PostCommitEventBus<ChatDeliveredEvent> _events;
	private readonly ChatRateLimit _globalRateLimit;
	private readonly IChatAdmissionService _admission;

	public ChatService(
		CompiledSchema schema,
		IEnumerable<ChatChannelRule> rules,
		IPermissionAuthorizer permissions,
		IChatRecipientResolver recipients,
		ILiveInventoryView inventory,
		IHexClock clock,
		PolicyPipeline<ChatSendContext> policy,
		PostCommitEventBus<ChatDeliveredEvent>? events = null,
		ChatRateLimit? globalRateLimit = null,
		IChatAdmissionService? admission = null )
	{
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_permissions = permissions ?? throw new ArgumentNullException( nameof(permissions) );
		_recipients = recipients ?? throw new ArgumentNullException( nameof(recipients) );
		_inventory = inventory ?? throw new ArgumentNullException( nameof(inventory) );
		_clock = clock ?? throw new ArgumentNullException( nameof(clock) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_events = events ?? new PostCommitEventBus<ChatDeliveredEvent>();
		_globalRateLimit = globalRateLimit ?? ChatRateLimit.Default;
		_admission = admission ?? new ChatAdmissionService();
		ValidateRateLimit( _globalRateLimit );
		var map = new Dictionary<string, ChatChannelRule>( StringComparer.Ordinal );
		foreach ( var rule in rules ?? throw new ArgumentNullException( nameof(rules) ) )
		{
			ArgumentNullException.ThrowIfNull( rule );
			if ( !_schema.ChatChannels.Contains( rule.Id ) )
				throw new InvalidOperationException( $"Chat rule '{rule.Id}' has no schema definition." );
			ValidateRateLimit( rule.RateLimit );
			if ( rule.Range is <= 0f ) throw new ArgumentOutOfRangeException( nameof(rules), "Chat range must be positive." );
			if ( !map.TryAdd( rule.Id, rule ) ) throw new InvalidOperationException( $"Chat rule '{rule.Id}' is duplicated." );
		}
		foreach ( var definition in _schema.ChatChannels.All )
			if ( !map.ContainsKey( definition.Id ) )
				throw new InvalidOperationException( $"Chat channel '{definition.Id}' has no runtime rule." );
		_rules = map;
	}

	public OperationResult<ChatDelivery> Send(
		InventoryActor actor,
		CharacterRecord character,
		string channelId,
		string rawText )
	{
		if ( character.Id != actor.CharacterId || character.AccountId != actor.AccountId )
			return OperationResult<ChatDelivery>.Failure( ErrorCode.Unauthorized, "Chat actor binding is invalid." );
		if ( !_schema.ChatChannels.TryGet( channelId, out var definition ) || !_rules.TryGetValue( channelId, out var rule ) )
			return OperationResult<ChatDelivery>.Failure( ErrorCode.UnknownDefinition, "Chat channel is not registered." );
		if ( definition!.PermissionId is not null )
		{
			if ( !_schema.Permissions.Contains( definition.PermissionId ) )
				return OperationResult<ChatDelivery>.Failure( ErrorCode.UnknownDefinition, "Chat permission is not registered." );
			if ( !_permissions.HasPermission( actor.AccountId, actor.CharacterId, definition.PermissionId ) )
				return OperationResult<ChatDelivery>.Failure( ErrorCode.Unauthorized, "Chat permission is denied." );
		}

		var normalized = Normalize( rawText );
		if ( normalized.Failed )
			return OperationResult<ChatDelivery>.Failure( normalized.Error!.Code, normalized.Error.Message );
		var context = new ChatSendContext( actor, character, channelId, normalized.Value );
		var policy = _policy.Evaluate( context );
		if ( policy.Failed )
			return OperationResult<ChatDelivery>.Failure( policy.Error!.Code, policy.Error.Message );

		var now = _clock.UtcNow;
		var reserved = _admission.Reserve(
			actor.ConnectionId, channelId, _globalRateLimit, rule.RateLimit, now );
		if ( reserved.Failed )
			return OperationResult<ChatDelivery>.Failure( reserved.Error!.Code, reserved.Error.Message );
		using var admission = reserved.Value;

		var inventory = _inventory.Capture();
		var recipients = _recipients.Resolve( context, rule, inventory )
			.Distinct()
			.ToArray();
		var delivery = new ChatDelivery(
			Guid.NewGuid(), channelId, character.Id, character.Name, normalized.Value, now, recipients );
		admission.Commit( now );
		_events.Publish( new ChatDeliveredEvent( delivery ) );
		return OperationResult<ChatDelivery>.Success( delivery );
	}

	public void RevokeConnection( ConnectionId connectionId )
	{
		_admission.Revoke( connectionId );
	}

	public static OperationResult<string> Normalize( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message is empty." );
		for ( var index = 0; index < text.Length; index++ )
		{
			if ( char.IsHighSurrogate( text[index] ) )
			{
				if ( index + 1 >= text.Length || !char.IsLowSurrogate( text[index + 1] ) )
					return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( text[index] ) )
			{
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message contains invalid Unicode." );
			}
		}
		var normalized = text.Normalize( NormalizationForm.FormC ).Trim();
		var scalarCount = 0;
		for ( var index = 0; index < normalized.Length; index++ )
		{
			var character = normalized[index];
			if ( char.IsControl( character ) && character is not '\n' and not '\t' )
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message contains control characters." );
			if ( char.IsHighSurrogate( character ) )
			{
				if ( index + 1 >= normalized.Length || !char.IsLowSurrogate( normalized[index + 1] ) )
					return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( character ) )
			{
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message contains invalid Unicode." );
			}
			scalarCount++;
			if ( scalarCount > MaximumUnicodeScalars )
				return OperationResult<string>.Failure( ErrorCode.InvalidArgument, "Chat message exceeds 512 Unicode scalars." );
		}
		return OperationResult<string>.Success( normalized );
	}

	private static void ValidateRateLimit( ChatRateLimit limit )
	{
		if ( limit.Capacity <= 0 || limit.RefillPeriod <= TimeSpan.Zero )
			throw new ArgumentOutOfRangeException( nameof(limit), "Chat rate limit must have positive capacity and period." );
		var refillRate = limit.Capacity / limit.RefillPeriod.TotalSeconds;
		var defaultRefillRate = ChatRateLimit.Default.Capacity / ChatRateLimit.Default.RefillPeriod.TotalSeconds;
		if ( limit.Capacity > ChatRateLimit.Default.Capacity || refillRate > defaultRefillRate )
			throw new ArgumentOutOfRangeException( nameof(limit), "Channel overrides may only be stricter than the default." );
	}

}
