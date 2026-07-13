#nullable enable

using System;
using System.Diagnostics;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Runtime;

internal sealed class RuntimePlayerSession : IDisposable
{
	private readonly object _sync = new();
	private readonly CommandAdmissionController _admission = new( Stopwatch.Frequency );
	private readonly ApplicationConnectionLatch _applicationConnection = new();
	private ClientSessionNonce? _clientNonce;
	private bool _disconnected;

	public RuntimePlayerSession( HexPlayerBody player, Action<Exception>? cancellationFailure = null )
	{
		Player = player ?? throw new ArgumentNullException( nameof(player) );
		Boundary = new ConnectionSessionBoundary( ConnectionEpoch.New(), cancellationFailure );
	}

	public HexPlayerBody Player { get; }
	public ConnectionSessionBoundary Boundary { get; }
	public bool IsConnected
	{
		get { lock ( _sync ) return !_disconnected && Boundary.IsConnected; }
	}
	public bool IsApplicationConnected
	{
		get
		{
			lock ( _sync )
				return !_disconnected && Boundary.IsConnected && _applicationConnection.IsConnected;
		}
	}
	public ClientSessionScope? Scope
	{
		get
		{
			lock ( _sync )
				return _clientNonce is null
					? null
					: new ClientSessionScope( _clientNonce.Value, Boundary.ConnectionEpoch );
		}
	}

	public bool TryBind(
		ClientSessionNonce nonce,
		out ClientSessionScope scope,
		out bool newlyBound )
	{
		lock ( _sync )
		{
			if ( _disconnected || !Boundary.IsConnected || (_clientNonce is not null && _clientNonce != nonce) )
			{
				scope = default;
				newlyBound = false;
				return false;
			}
			newlyBound = _clientNonce is null;
			_clientNonce ??= nonce;
			scope = new ClientSessionScope( nonce, Boundary.ConnectionEpoch );
			_ = _applicationConnection.ObserveNonceBound();
			return true;
		}
	}

	public bool IsCurrent( ClientSessionScope scope ) => Scope == scope;
	public bool ObserveHostReady() => _applicationConnection.ObserveHostReady();
	public bool ObserveHelloIssued() => _applicationConnection.ObserveHelloIssued();
	public bool CompleteApplicationConnection( bool succeeded ) =>
		_applicationConnection.CompleteNotification( succeeded );

	public CommandAdmissionResult TryBeginRequest( CommandRequestId requestId, int cost ) =>
		IsConnected
			? _admission.TryBegin( requestId, cost, Stopwatch.GetTimestamp() )
			: CommandAdmissionResult.Reject( CommandAdmissionFailure.Disconnected );

	public bool FinishRequest( CommandRequestId requestId ) => _admission.Finish( requestId );

	public void Disconnect()
	{
		lock ( _sync )
		{
			if ( _disconnected ) return;
			_disconnected = true;
			_applicationConnection.Disconnect();
		}
		Boundary.Disconnect();
		_admission.Disconnect();
	}

	public void Dispose()
	{
		Disconnect();
		Boundary.Dispose();
	}
}
