#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

/// <summary>
/// Minimal, bounded transport contract for command results. The RPC surface stays
/// scalar-only while preserving the retry delay required by rate-limit failures.
/// </summary>
public static class OperationResultWireContract
{
	public const string RetryAfterMillisecondsDetail = "retry_after_ms";
	public const int DefaultRetryAfterMilliseconds = 125;
	public const int MaximumRetryAfterMilliseconds = 60_000;

	public static OperationResult RateLimited( string message, TimeSpan retryAfter )
	{
		var milliseconds = Normalize( retryAfter.TotalMilliseconds );
		return OperationResult.Failure(
			ErrorCode.RateLimited,
			message,
			new Dictionary<string, string>( StringComparer.Ordinal )
			{
				[RetryAfterMillisecondsDetail] = milliseconds.ToString( CultureInfo.InvariantCulture )
			} );
	}

	public static int EncodeRetryAfterMilliseconds( OperationResult result )
	{
		if ( result.Succeeded || result.Error?.Code != ErrorCode.RateLimited ) return 0;
		if ( result.Error.Details?.TryGetValue( RetryAfterMillisecondsDetail, out var encoded ) != true ||
			!long.TryParse( encoded, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed ) )
			return DefaultRetryAfterMilliseconds;
		return Normalize( parsed );
	}

	public static OperationResult Decode(
		bool succeeded,
		int rawErrorCode,
		string? message,
		int retryAfterMilliseconds )
	{
		if ( succeeded ) return OperationResult.Success();
		var code = Enum.IsDefined( typeof(ErrorCode), rawErrorCode ) && rawErrorCode != (int)ErrorCode.None
			? (ErrorCode)rawErrorCode
			: ErrorCode.InternalError;
		var safeMessage = string.IsNullOrWhiteSpace( message ) ? "The host denied the command." : message;
		if ( code != ErrorCode.RateLimited ) return OperationResult.Failure( code, safeMessage );
		var normalized = Normalize( retryAfterMilliseconds );
		return OperationResult.Failure(
			code,
			safeMessage,
			new Dictionary<string, string>( StringComparer.Ordinal )
			{
				[RetryAfterMillisecondsDetail] = normalized.ToString( CultureInfo.InvariantCulture )
			} );
	}

	private static int Normalize( double milliseconds )
	{
		if ( double.IsNaN( milliseconds ) || milliseconds <= 0d ) return DefaultRetryAfterMilliseconds;
		if ( double.IsPositiveInfinity( milliseconds ) || milliseconds >= MaximumRetryAfterMilliseconds )
			return MaximumRetryAfterMilliseconds;
		return Math.Clamp( (int)Math.Ceiling( milliseconds ), 1, MaximumRetryAfterMilliseconds );
	}

	private static int Normalize( long milliseconds ) => milliseconds switch
	{
		<= 0 => DefaultRetryAfterMilliseconds,
		>= MaximumRetryAfterMilliseconds => MaximumRetryAfterMilliseconds,
		_ => (int)milliseconds
	};
}
