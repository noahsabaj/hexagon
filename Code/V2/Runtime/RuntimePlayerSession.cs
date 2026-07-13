#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Runtime;

internal sealed class RuntimePlayerSession : IDisposable
{
	private const int MaximumActiveRequests = 64;
	private const int RetainedRequestIds = 1024;
	private readonly HashSet<CommandRequestId> _activeRequests = new();
	private readonly HashSet<CommandRequestId> _seenRequests = new();
	private readonly Queue<CommandRequestId> _seenOrder = new();
	private bool _disconnected;

	public RuntimePlayerSession( HexPlayerBody player, Action<Exception>? cancellationFailure = null )
	{
		Player = player ?? throw new ArgumentNullException( nameof(player) );
		Boundary = new ConnectionSessionBoundary( ConnectionEpoch.New(), cancellationFailure );
	}

	public HexPlayerBody Player { get; }
	public ConnectionSessionBoundary Boundary { get; }
	public bool IsConnected => !_disconnected && Boundary.IsConnected;

	public bool TryBeginRequest( CommandRequestId requestId )
	{
		if ( !IsConnected || _activeRequests.Count >= MaximumActiveRequests ) return false;
		if ( _seenRequests.Contains( requestId ) || !_activeRequests.Add( requestId ) ) return false;
		return true;
	}

	public bool FinishRequest( CommandRequestId requestId )
	{
		if ( !_activeRequests.Remove( requestId ) ) return false;
		_seenRequests.Add( requestId );
		_seenOrder.Enqueue( requestId );
		while ( _seenOrder.Count > RetainedRequestIds )
			_seenRequests.Remove( _seenOrder.Dequeue() );
		return true;
	}

	public void Disconnect()
	{
		if ( _disconnected ) return;
		_disconnected = true;
		Boundary.Disconnect();
		_activeRequests.Clear();
	}

	public void Dispose()
	{
		Disconnect();
		Boundary.Dispose();
	}
}
