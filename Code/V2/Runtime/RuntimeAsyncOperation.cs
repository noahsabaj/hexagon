#nullable enable

using System;
using System.Threading.Tasks;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Converts synchronous and asynchronous exceptions into values before an async
/// runtime state machine observes them. This avoids compiler-generated
/// exception rethrow references rejected by the s&amp;box whitelist.
/// ContinueWith only constructs an immutable outcome; callers await that task
/// without ConfigureAwait(false), so s&amp;box's ExpirableSynchronizationContext
/// posts all scene, RPC, and session work back to its main-thread queue.
/// </summary>
internal static class RuntimeAsyncOperation
{
	public static Task<RuntimeOperationOutcome> Capture( Func<ValueTask> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask pending;
		try
		{
			pending = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( RuntimeOperationOutcome.Failure( exception ) );
		}
		if ( pending.IsCompletedSuccessfully )
		{
			pending.GetAwaiter().GetResult();
			return Task.FromResult( RuntimeOperationOutcome.Success() );
		}

		return pending.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
				return RuntimeOperationOutcome.Failure( new OperationCanceledException( "Runtime operation was canceled." ) );
			if ( task.IsFaulted ) return RuntimeOperationOutcome.Failure( Unwrap( task.Exception ) );
			return RuntimeOperationOutcome.Success();
		} );
	}

	public static Task<RuntimeOperationOutcome<T>> Capture<T>( Func<ValueTask<T>> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask<T> pending;
		try
		{
			pending = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( RuntimeOperationOutcome<T>.Failure( exception ) );
		}
		if ( pending.IsCompletedSuccessfully )
			return Task.FromResult( RuntimeOperationOutcome<T>.Success( pending.Result ) );

		return pending.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
				return RuntimeOperationOutcome<T>.Failure( new OperationCanceledException( "Runtime operation was canceled." ) );
			if ( task.IsFaulted ) return RuntimeOperationOutcome<T>.Failure( Unwrap( task.Exception ) );
			return RuntimeOperationOutcome<T>.Success( task.Result );
		} );
	}

	private static Exception Unwrap( AggregateException? exception ) =>
		exception?.InnerException ?? (Exception?)exception
			?? new InvalidOperationException( "Runtime operation faulted without an exception." );
}

internal readonly record struct RuntimeOperationOutcome( Exception? Exception )
{
	public bool Succeeded => Exception is null;
	public static RuntimeOperationOutcome Success() => new( null );
	public static RuntimeOperationOutcome Failure( Exception exception ) => new( exception );
}

internal readonly record struct RuntimeOperationOutcome<T>( T? Value, Exception? Exception )
{
	public bool Succeeded => Exception is null;
	public static RuntimeOperationOutcome<T> Success( T value ) => new( value, null );
	public static RuntimeOperationOutcome<T> Failure( Exception exception ) => new( default, exception );
}
