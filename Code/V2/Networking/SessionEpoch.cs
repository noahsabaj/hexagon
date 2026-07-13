#nullable enable

using System;
using System.Threading;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

public readonly record struct CommandRequestId
{
	public CommandRequestId( Guid value )
	{
		if ( value == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(value) );
		Value = value;
	}

	public Guid Value { get; }
	public static CommandRequestId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
}

/// <summary>
/// Unforgeable lifetime identifier for one authenticated connection binding.
/// A reconnect always receives a new value, even if s&amp;box reuses a connection ID.
/// </summary>
public readonly record struct ConnectionEpoch
{
	public ConnectionEpoch( Guid value )
	{
		if ( value == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(value) );
		Value = value;
	}

	public Guid Value { get; }
	public static ConnectionEpoch New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
}

/// <summary>Client-generated identity for one local connect attempt.</summary>
public readonly record struct ClientSessionNonce
{
	public ClientSessionNonce( Guid value )
	{
		if ( value == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(value) );
		Value = value;
	}

	public Guid Value { get; }
	public static ClientSessionNonce New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
}

/// <summary>
/// Exact bidirectional connection scope. A packet is authoritative only when
/// both the client's connect nonce and host's connection epoch match.
/// </summary>
public readonly record struct ClientSessionScope
{
	public ClientSessionScope( ClientSessionNonce nonce, ConnectionEpoch connection )
	{
		if ( nonce.Value == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(nonce) );
		if ( connection.Value == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(connection) );
		Nonce = nonce;
		Connection = connection;
	}

	public ClientSessionNonce Nonce { get; }
	public ConnectionEpoch Connection { get; }
}

public readonly record struct ClientSessionHello
{
	public ClientSessionHello( ClientSessionScope scope ) => Scope = scope;
	public ClientSessionScope Scope { get; }
}

public readonly record struct ClientCommandHeader
{
	public ClientCommandHeader( ClientSessionScope scope, CommandRequestId requestId )
	{
		Scope = scope;
		RequestId = requestId;
	}

	public ClientSessionScope Scope { get; }
	public CommandRequestId RequestId { get; }
}

/// <summary>
/// Monotonic, bounded retry schedule for the pre-authority session handshake.
/// Every attempt reuses the same client nonce; accepting the exact host hello or
/// disposing the transport permanently stops the schedule.
/// </summary>
public sealed class ClientSessionRetrySchedule
{
	private readonly object _sync = new();
	private readonly long _initialDelay;
	private readonly long _maximumDelay;
	private long _delay;
	private long _nextAttempt;
	private bool _started;
	private bool _complete;

	public ClientSessionRetrySchedule(
		long timestampFrequency,
		TimeSpan? initialDelay = null,
		TimeSpan? maximumDelay = null )
	{
		if ( timestampFrequency <= 0 ) throw new ArgumentOutOfRangeException( nameof(timestampFrequency) );
		var initial = initialDelay ?? TimeSpan.FromMilliseconds( 250 );
		var maximum = maximumDelay ?? TimeSpan.FromSeconds( 2 );
		if ( initial <= TimeSpan.Zero ) throw new ArgumentOutOfRangeException( nameof(initialDelay) );
		if ( maximum < initial ) throw new ArgumentOutOfRangeException( nameof(maximumDelay) );
		_initialDelay = ToTimestampTicks( initial, timestampFrequency );
		_maximumDelay = ToTimestampTicks( maximum, timestampFrequency );
		_delay = _initialDelay;
	}

	public bool IsComplete
	{
		get { lock ( _sync ) return _complete; }
	}

	public bool TryBeginAttempt( long timestamp )
	{
		lock ( _sync )
		{
			if ( _complete ) return false;
			if ( !_started )
			{
				_started = true;
				_nextAttempt = SaturatingAdd( timestamp, _delay );
				return true;
			}
			if ( timestamp < _nextAttempt ) return false;
			_delay = Math.Min( _maximumDelay, SaturatingDouble( _delay ) );
			_nextAttempt = SaturatingAdd( timestamp, _delay );
			return true;
		}
	}

	public bool Complete()
	{
		lock ( _sync )
		{
			if ( _complete ) return false;
			_complete = true;
			return true;
		}
	}

	private static long ToTimestampTicks( TimeSpan duration, long frequency )
	{
		var ticks = duration.TotalSeconds * frequency;
		if ( !double.IsFinite( ticks ) || ticks > long.MaxValue )
			throw new ArgumentOutOfRangeException( nameof(duration) );
		return Math.Max( 1, (long)Math.Ceiling( ticks ) );
	}

	private static long SaturatingAdd( long value, long increment ) =>
		value > long.MaxValue - increment ? long.MaxValue : value + increment;

	private static long SaturatingDouble( long value ) =>
		value > long.MaxValue / 2 ? long.MaxValue : value * 2;
}

public enum ApplicationConnectionState
{
	WaitingForConditions = 0,
	Notifying = 1,
	Connected = 2,
	Failed = 3,
	Disconnected = 4
}

/// <summary>
/// Idempotent state machine for application-level connection delivery. Binding
/// the nonce, issuing the exact hello, and completing host initialization only
/// grants one notification attempt; command authority is granted separately,
/// after that callback returns successfully. A failed or disconnected
/// notification is terminal.
/// </summary>
public sealed class ApplicationConnectionLatch
{
	private readonly object _sync = new();
	private bool _nonceBound;
	private bool _helloIssued;
	private bool _hostReady;
	private ApplicationConnectionState _state;

	public ApplicationConnectionState State
	{
		get { lock ( _sync ) return _state; }
	}

	public bool IsConnected => State == ApplicationConnectionState.Connected;

	public bool ObserveNonceBound()
	{
		lock ( _sync )
		{
			_nonceBound = true;
			return TryNotify();
		}
	}

	public bool ObserveHostReady()
	{
		lock ( _sync )
		{
			_hostReady = true;
			return TryNotify();
		}
	}

	public bool ObserveHelloIssued()
	{
		lock ( _sync )
		{
			_helloIssued = true;
			return TryNotify();
		}
	}

	public bool CompleteNotification( bool succeeded )
	{
		lock ( _sync )
		{
			if ( _state != ApplicationConnectionState.Notifying ) return false;
			_state = succeeded
				? ApplicationConnectionState.Connected
				: ApplicationConnectionState.Failed;
			return succeeded;
		}
	}

	public bool Disconnect()
	{
		lock ( _sync )
		{
			var wasConnected = _state == ApplicationConnectionState.Connected;
			_state = ApplicationConnectionState.Disconnected;
			return wasConnected;
		}
	}

	private bool TryNotify()
	{
		if ( _state != ApplicationConnectionState.WaitingForConditions ||
			!_nonceBound || !_helloIssued || !_hostReady ) return false;
		_state = ApplicationConnectionState.Notifying;
		return true;
	}
}

/// <summary>
/// Total ordering for full client-state snapshots within one connection lifetime.
/// Character increments whenever the active character changes; Revision increments
/// for every complete state publication.
/// </summary>
public readonly record struct ClientStateEpoch
{
	public ClientStateEpoch( ConnectionEpoch connection, long character, long revision )
	{
		if ( character < 0 ) throw new ArgumentOutOfRangeException( nameof(character) );
		if ( revision <= 0 ) throw new ArgumentOutOfRangeException( nameof(revision) );
		Connection = connection;
		Character = character;
		Revision = revision;
	}

	public ConnectionEpoch Connection { get; }
	public long Character { get; }
	public long Revision { get; }
}

/// <summary>
/// Host-only command lease. Character-stable commands receive the character token;
/// account and character-transition commands receive the connection token.
/// </summary>
public readonly record struct CommandSessionLease(
	ConnectionEpoch Connection,
	long Character,
	CancellationToken CancellationToken,
	bool RequiresStableCharacter );

/// <summary>
/// Pure host-side connection/character boundary. The runtime owns one instance per
/// authenticated connection and never persists it.
/// </summary>
public sealed class ConnectionSessionBoundary : IDisposable
{
	private readonly CancellationTokenSource _connectionCancellation = new();
	private CancellationTokenSource _characterCancellation = new();
	private CharacterId? _characterId;
	private long _characterEpoch;
	private long _stateRevision;
	private bool _disconnected;
	private readonly Action<Exception>? _cancellationFailure;

	public ConnectionSessionBoundary(
		ConnectionEpoch epoch,
		Action<Exception>? cancellationFailure = null )
	{
		ConnectionEpoch = epoch;
		_cancellationFailure = cancellationFailure;
	}

	public ConnectionEpoch ConnectionEpoch { get; }
	public CharacterId? CharacterId => _characterId;
	public long CharacterEpoch => _characterEpoch;
	public bool IsConnected => !_disconnected;

	public CommandSessionLease Capture( CharacterId? characterId, bool requiresStableCharacter )
	{
		ObserveCharacter( characterId );
		return new CommandSessionLease(
			ConnectionEpoch,
			_characterEpoch,
			requiresStableCharacter ? _characterCancellation.Token : _connectionCancellation.Token,
			requiresStableCharacter );
	}

	public ClientStateEpoch Publish( CharacterId? characterId )
	{
		if ( _disconnected ) throw new InvalidOperationException( "The connection session has ended." );
		ObserveCharacter( characterId );
		_stateRevision = checked( _stateRevision + 1 );
		return new ClientStateEpoch( ConnectionEpoch, _characterEpoch, _stateRevision );
	}

	public bool IsCurrent( CommandSessionLease lease )
	{
		if ( _disconnected || lease.Connection != ConnectionEpoch ) return false;
		if ( !lease.RequiresStableCharacter ) return !_connectionCancellation.IsCancellationRequested;
		return lease.Character == _characterEpoch && !lease.CancellationToken.IsCancellationRequested;
	}

	public void ObserveCharacter( CharacterId? characterId )
	{
		if ( _disconnected ) return;
		if ( _characterId == characterId ) return;

		var previous = _characterCancellation;
		_characterCancellation = new CancellationTokenSource();
		_characterId = characterId;
		_characterEpoch = checked( _characterEpoch + 1 );
		CancelSafely( previous );
		previous.Dispose();
	}

	public void Disconnect()
	{
		if ( _disconnected ) return;
		_disconnected = true;
		CancelSafely( _connectionCancellation );
		CancelSafely( _characterCancellation );
	}

	public void Dispose()
	{
		Disconnect();
		_connectionCancellation.Dispose();
		_characterCancellation.Dispose();
	}

	private void CancelSafely( CancellationTokenSource source )
	{
		try
		{
			source.Cancel();
		}
		catch ( Exception exception )
		{
			try
			{
				_cancellationFailure?.Invoke( exception );
			}
			catch
			{
				// Diagnostics must never restore authority to a failed cancellation callback.
			}
		}
	}
}
