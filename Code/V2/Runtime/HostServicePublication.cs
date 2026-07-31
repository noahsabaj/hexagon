#nullable enable

using System;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Testable publication transaction for required host network services.
/// Cleanup is attempted exactly once whenever publication does not succeed.
/// </summary>
internal static class HostServicePublication
{
	public static OperationResult<T> RequirePublished<T>(
		T value,
		Func<bool> publish,
		Action cleanup )
	{
		ArgumentNullException.ThrowIfNull( value );
		ArgumentNullException.ThrowIfNull( publish );
		ArgumentNullException.ThrowIfNull( cleanup );

		try
		{
			if ( publish() ) return OperationResult<T>.Success( value );
			return FailAfterCleanup<T>(
				cleanup,
				"Hexagon host services could not be network-published." );
		}
		catch ( Exception exception )
		{
			return FailAfterCleanup<T>(
				cleanup,
				$"Hexagon host-service publication threw: {exception.Message}" );
		}
	}

	private static OperationResult<T> FailAfterCleanup<T>( Action cleanup, string message )
	{
		try { cleanup(); }
		catch ( Exception cleanupException )
		{
			return OperationResult<T>.Failure(
				ErrorCode.InternalError,
				$"{message} Partial-service cleanup also failed: {cleanupException.Message}" );
		}
		return OperationResult<T>.Failure( ErrorCode.InternalError, message );
	}
}
