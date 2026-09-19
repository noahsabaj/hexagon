#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hexagon.V2.Application;

/// <summary>
/// One fault observed while draining an owned asynchronous operation.
/// </summary>
public sealed record AsyncOperationFailure(
	long OperationId,
	string Name,
	Exception Exception)
{
	public bool WasCanceled => Exception is OperationCanceledException;
}

/// <summary>
/// Immutable terminal result for a one-way operation registry drain. <see cref="Failures"/>
/// holds at most <see cref="AsyncOperationRegistry.RetainedFailureLimit"/> of the MOST RECENT
/// faults; <see cref="DroppedFailureCount"/> says how many older ones were let go, so a
/// long-lived host that faulted thousands of times still reports that it did.
/// </summary>
public sealed record AsyncDrainResult(
	int AcceptedOperationCount,
	int CompletedOperationCount,
	IReadOnlyList<AsyncOperationFailure> Failures,
	int DroppedFailureCount = 0)
{
	public bool Succeeded => Failures.Count == 0 && DroppedFailureCount == 0;
	public int TotalFailureCount => Failures.Count + DroppedFailureCount;
}

/// <summary>
/// Owns fire-and-forget work so shutdown can close admission, observe every
/// exception, and wait for quiescence without retaining completed tasks.
/// A registry is intentionally one-shot: admission cannot reopen after it is
/// stopped.
/// </summary>
public sealed class AsyncOperationRegistry
{
	/// <summary>
	/// Failures retained for the drain report. A registry lives as long as the host and every
	/// failure holds its exception (and so its stack and captured state), so an unbounded list
	/// would grow with uptime. The oldest are dropped first and counted rather than forgotten.
	/// </summary>
	public const int RetainedFailureLimit = 64;

	private readonly object _sync = new();
	private readonly Dictionary<long, OperationEntry> _active = new();
	private readonly Queue<AsyncOperationFailure> _failures = new();
	private TaskCompletionSource<AsyncDrainResult>? _drainCompletion;
	private long _nextOperationId;
	private int _acceptedOperationCount;
	private int _completedOperationCount;
	private int _droppedFailureCount;
	private bool _accepting = true;

	public bool IsAccepting
	{
		get { lock ( _sync ) return _accepting; }
	}

	public int ActiveOperationCount
	{
		get { lock ( _sync ) return _active.Count; }
	}

	/// <summary>
	/// Starts and owns a ValueTask-producing operation if admission is open.
	/// Synchronous factory exceptions are observed as operation failures.
	/// </summary>
	public bool TryStart( string name, Func<ValueTask> operation )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( operation );
		var entry = Reserve( name );
		if ( entry is null ) return false;

		ValueTask pending;
		try
		{
			pending = operation();
		}
		catch ( Exception exception )
		{
			Complete( entry, exception );
			return true;
		}

		if ( pending.IsCompletedSuccessfully )
		{
			pending.GetAwaiter().GetResult();
			Complete( entry, null );
			return true;
		}

		_ = ObserveAsync( entry, pending );
		return true;
	}

	/// <summary>
	/// Starts and owns a Task-producing operation if admission is open.
	/// </summary>
	public bool TryStartTask( string name, Func<Task> operation )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( name );
		ArgumentNullException.ThrowIfNull( operation );
		var entry = Reserve( name );
		if ( entry is null ) return false;

		Task pending;
		try
		{
			pending = operation()
				?? throw new InvalidOperationException( "Asynchronous operation factory returned null." );
		}
		catch ( Exception exception )
		{
			Complete( entry, exception );
			return true;
		}

		_ = ObserveTaskAsync( entry, pending );
		return true;
	}

	/// <summary>
	/// Permanently closes admission. Returns true only for the caller that
	/// performed the transition.
	/// </summary>
	public bool StopAdmission()
	{
		lock ( _sync )
		{
			if ( !_accepting ) return false;
			_accepting = false;
			EnsureDrainCompletionUnsafe();
			CompleteDrainIfQuiescedUnsafe();
			return true;
		}
	}

	/// <summary>
	/// Closes admission and returns the same terminal drain for every caller.
	/// Faults and cancellations are returned in the result rather than thrown.
	/// </summary>
	public ValueTask<AsyncDrainResult> DrainAsync()
	{
		StopAdmission();
		lock ( _sync )
		{
			EnsureDrainCompletionUnsafe();
			return new ValueTask<AsyncDrainResult>( _drainCompletion!.Task );
		}
	}

	private OperationEntry? Reserve( string name )
	{
		lock ( _sync )
		{
			if ( !_accepting ) return null;
			var entry = new OperationEntry( checked( ++_nextOperationId ), name );
			_active.Add( entry.Id, entry );
			_acceptedOperationCount++;
			return entry;
		}
	}

	private async Task ObserveAsync( OperationEntry entry, ValueTask pending )
	{
		Exception? failure = null;
		try
		{
			await pending;
		}
		catch ( Exception exception )
		{
			failure = exception;
		}
		Complete( entry, failure );
	}

	private async Task ObserveTaskAsync( OperationEntry entry, Task pending )
	{
		Exception? failure = null;
		try
		{
			await pending;
		}
		catch ( Exception exception )
		{
			failure = exception;
		}
		Complete( entry, failure );
	}

	private void Complete( OperationEntry entry, Exception? failure )
	{
		lock ( _sync )
		{
			if ( !_active.Remove( entry.Id ) ) return;
			_completedOperationCount++;
			if ( failure is not null )
			{
				_failures.Enqueue( new AsyncOperationFailure( entry.Id, entry.Name, failure ) );
				while ( _failures.Count > RetainedFailureLimit )
				{
					_failures.Dequeue();
					_droppedFailureCount++;
				}
			}
			CompleteDrainIfQuiescedUnsafe();
		}
	}

	private void EnsureDrainCompletionUnsafe() =>
		_drainCompletion ??= new TaskCompletionSource<AsyncDrainResult>(
			TaskCreationOptions.RunContinuationsAsynchronously );

	private void CompleteDrainIfQuiescedUnsafe()
	{
		if ( _accepting || _active.Count != 0 || _drainCompletion is null ) return;
		_drainCompletion.TrySetResult( new AsyncDrainResult(
			_acceptedOperationCount,
			_completedOperationCount,
			Array.AsReadOnly( _failures.ToArray() ),
			_droppedFailureCount ) );
	}

	private sealed record OperationEntry( long Id, string Name );
}
