#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Client;

public readonly record struct PendingClientCommand(
	CommandRequestId RequestId,
	ValueTask<OperationResult> Completion );

/// <summary>
/// Correlates one-way RPC messages into truthful client command completions.
/// The registry owns timeout and cancellation races; exactly one terminal result
/// wins and every pending task is completed when the transport is disposed.
/// </summary>
public sealed class PendingCommandRegistry : IDisposable
{
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds( 15 );
	public const int DefaultMaximumPending = 64;

	private readonly object _sync = new();
	private readonly Dictionary<CommandRequestId, PendingState> _pending = new();
	private readonly TimeSpan _timeout;
	private readonly int _maximumPending;
	private bool _disposed;

	public PendingCommandRegistry(
		TimeSpan? timeout = null,
		int maximumPending = DefaultMaximumPending )
	{
		_timeout = timeout ?? DefaultTimeout;
		if ( _timeout <= TimeSpan.Zero ) throw new ArgumentOutOfRangeException( nameof(timeout) );
		if ( maximumPending <= 0 ) throw new ArgumentOutOfRangeException( nameof(maximumPending) );
		_maximumPending = maximumPending;
	}

	public int Count
	{
		get { lock ( _sync ) return _pending.Count; }
	}

	public OperationResult<PendingClientCommand> Register( CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		PendingState state;
		lock ( _sync )
		{
			if ( _disposed )
				return OperationResult<PendingClientCommand>.Failure( ErrorCode.InternalError, "Command transport is disposed." );
			if ( _pending.Count >= _maximumPending )
				return OperationResult<PendingClientCommand>.Failure( ErrorCode.Conflict, "Too many commands are awaiting a host response." );

			CommandRequestId requestId;
			do requestId = CommandRequestId.New(); while ( _pending.ContainsKey( requestId ) );
			state = new PendingState( this, requestId, cancellationToken, _timeout );
			_pending.Add( requestId, state );
			state.Start();
		}
		return OperationResult<PendingClientCommand>.Success(
			new PendingClientCommand( state.RequestId, new ValueTask<OperationResult>( state.Task ) ) );
	}

	public bool IsPending( CommandRequestId requestId )
	{
		lock ( _sync ) return _pending.ContainsKey( requestId );
	}

	public bool Complete( CommandRequestId requestId, OperationResult result )
	{
		var state = Remove( requestId );
		if ( state is null ) return false;
		state.Complete( result );
		return true;
	}

	public void Dispose()
	{
		PendingState[] pending;
		lock ( _sync )
		{
			if ( _disposed ) return;
			_disposed = true;
			pending = _pending.Values.ToArray();
			_pending.Clear();
		}

		var failure = OperationResult.Failure( ErrorCode.InternalError, "Command transport closed before the host responded." );
		foreach ( var state in pending ) state.Complete( failure );
	}

	private PendingState? Remove( CommandRequestId requestId )
	{
		lock ( _sync )
		{
			if ( !_pending.Remove( requestId, out var state ) ) return null;
			return state;
		}
	}

	private void Cancel( PendingState candidate )
	{
		var state = Remove( candidate.RequestId );
		if ( !ReferenceEquals( state, candidate ) ) return;
		state.Cancel();
	}

	private void Timeout( PendingState candidate )
	{
		var state = Remove( candidate.RequestId );
		if ( !ReferenceEquals( state, candidate ) ) return;
		state.Complete( OperationResult.Failure( ErrorCode.InternalError, "Host command response timed out." ) );
	}

	private sealed class PendingState
	{
		private readonly PendingCommandRegistry _owner;
		private readonly CancellationToken _externalToken;
		private readonly TimeSpan _timeout;
		private readonly TaskCompletionSource<OperationResult> _completion =
			new( TaskCreationOptions.RunContinuationsAsynchronously );
		private readonly CancellationTokenSource _timeoutCancellation = new();
		private CancellationTokenRegistration _externalRegistration;
		private CancellationTokenRegistration _timeoutRegistration;

		public PendingState(
			PendingCommandRegistry owner,
			CommandRequestId requestId,
			CancellationToken externalToken,
			TimeSpan timeout )
		{
			_owner = owner;
			RequestId = requestId;
			_externalToken = externalToken;
			_timeout = timeout;
		}

		public CommandRequestId RequestId { get; }
		public Task<OperationResult> Task => _completion.Task;

		public void Start()
		{
			_timeoutRegistration = _timeoutCancellation.Token.Register(
				static value => ((PendingState)value!)._owner.Timeout( (PendingState)value ), this );
			_timeoutCancellation.CancelAfter( _timeout );
			var externalRegistration = _externalToken.Register(
				static value => ((PendingState)value!)._owner.Cancel( (PendingState)value ), this );
			_externalRegistration = externalRegistration;
			if ( !_owner.IsPending( RequestId ) ) externalRegistration.Dispose();
		}

		public void Complete( OperationResult result )
		{
			Cleanup();
			_completion.TrySetResult( result );
		}

		public void Cancel()
		{
			Cleanup();
			_completion.TrySetCanceled( _externalToken );
		}

		private void Cleanup()
		{
			_timeoutCancellation.Dispose();
			_timeoutRegistration.Dispose();
			_externalRegistration.Dispose();
		}
	}
}
