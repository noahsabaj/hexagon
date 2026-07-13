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
