#nullable enable

using System;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Converts asynchronous failures into values before an async state machine observes them. This keeps
/// gate release explicit and avoids exception regions spanning await points in the s&amp;box sandbox.
/// </summary>
internal static class AsyncOperation
{
	public static Task<OperationOutcome> Capture( Func<ValueTask> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask valueTask;
		try
		{
			valueTask = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( OperationOutcome.Failure( exception ) );
		}

		return valueTask.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
			{
				return OperationOutcome.Failure( new OperationCanceledException( "Persistence operation was canceled." ) );
			}

			if ( task.IsFaulted )
			{
				return OperationOutcome.Failure( Unwrap( task.Exception ) );
			}

			return OperationOutcome.Success();
		} );
	}

	public static Task<OperationOutcome<T>> Capture<T>( Func<ValueTask<T>> operation )
	{
		ArgumentNullException.ThrowIfNull( operation );
		ValueTask<T> valueTask;
		try
		{
			valueTask = operation();
		}
		catch ( Exception exception )
		{
			return Task.FromResult( OperationOutcome<T>.Failure( exception ) );
		}

		return valueTask.AsTask().ContinueWith( static task =>
		{
			if ( task.IsCanceled )
			{
				return OperationOutcome<T>.Failure( new OperationCanceledException( "Persistence operation was canceled." ) );
			}

			if ( task.IsFaulted )
			{
				return OperationOutcome<T>.Failure( Unwrap( task.Exception ) );
			}

			return OperationOutcome<T>.Success( task.Result );
		} );
	}

	private static Exception Unwrap( AggregateException? exception ) =>
		exception?.InnerException ?? (Exception?)exception
			?? new InvalidOperationException( "Persistence operation faulted without an exception." );
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
