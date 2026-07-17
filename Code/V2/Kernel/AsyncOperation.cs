#nullable enable

using System;
using System.Threading.Tasks;

namespace Hexagon.V2.Kernel;

/// <summary>
/// Converts synchronous and asynchronous exceptions into values before an async state machine
/// observes them. This keeps gate release explicit and avoids both exception regions spanning
/// await points and compiler-generated exception rethrow references rejected by the s&amp;box
/// whitelist. ContinueWith only constructs an immutable outcome; callers await that task
/// without ConfigureAwait(false), so s&amp;box's ExpirableSynchronizationContext posts all scene,
/// RPC, and session work back to its main-thread queue.
/// </summary>
internal static class AsyncOperation
{
	public static Task<OperationOutcome> Capture( Func<ValueTask> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask pending;
		try
		{
			pending = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( OperationOutcome.Failure( exception ) );
		}
		if ( pending.IsCompletedSuccessfully )
		{
			pending.GetAwaiter().GetResult();
			return Task.FromResult( OperationOutcome.Success() );
		}

		return pending.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
				return OperationOutcome.Failure( new OperationCanceledException( "Operation was canceled." ) );
			if ( task.IsFaulted ) return OperationOutcome.Failure( Unwrap( task.Exception ) );
			return OperationOutcome.Success();
		} );
	}

	public static Task<OperationOutcome<T>> Capture<T>( Func<ValueTask<T>> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask<T> pending;
		try
		{
			pending = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( OperationOutcome<T>.Failure( exception ) );
		}
		if ( pending.IsCompletedSuccessfully )
			return Task.FromResult( OperationOutcome<T>.Success( pending.Result ) );

		return pending.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
				return OperationOutcome<T>.Failure( new OperationCanceledException( "Operation was canceled." ) );
			if ( task.IsFaulted ) return OperationOutcome<T>.Failure( Unwrap( task.Exception ) );
			return OperationOutcome<T>.Success( task.Result );
		} );
	}

	public static OperationOutcome CaptureSynchronous( Action operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		try
		{
			operation();
			return OperationOutcome.Success();
		}
		catch ( Exception exception )
		{
			return OperationOutcome.Failure( exception );
		}
	}

	public static OperationOutcome<T> CaptureSynchronous<T>( Func<T> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		try
		{
			return OperationOutcome<T>.Success( operation() );
		}
		catch ( Exception exception )
		{
			return OperationOutcome<T>.Failure( exception );
		}
	}

	private static Exception Unwrap( AggregateException? exception ) =>
		exception?.InnerException ?? (Exception?)exception
			?? new InvalidOperationException( "Operation faulted without an exception." );
}

internal readonly record struct OperationOutcome( Exception? Exception )
{
	public bool Succeeded => Exception is null;

	public static OperationOutcome Success() => new( null );
	public static OperationOutcome Failure( Exception exception ) => new( exception );
}

internal readonly record struct OperationOutcome<T>( T? Value, Exception? Exception )
{
	public bool Succeeded => Exception is null;

	public static OperationOutcome<T> Success( T value ) => new( value, null );
	public static OperationOutcome<T> Failure( Exception exception ) => new( default, exception );
}
