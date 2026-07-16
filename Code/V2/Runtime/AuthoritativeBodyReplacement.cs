#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Engine-neutral transaction coordinator for replacing a live authoritative body.
/// The previous body remains recoverable until the candidate is active and its
/// canonical pointer has been published. Every failed stage rolls back and discards
/// the partial candidate before failure is reported.
/// </summary>
internal static class AuthoritativeBodyReplacement
{
	public static OperationResult<T> RequireActivated<T>(
		T candidate,
		Func<bool> activateCandidate,
		Func<bool> quiescePrevious,
		Func<bool> publishCandidate,
		Action rollbackPublication,
		Action restorePrevious,
		Action discardCandidate,
		Action retirePrevious )
	{
		ArgumentNullException.ThrowIfNull( candidate );
		ArgumentNullException.ThrowIfNull( activateCandidate );
		ArgumentNullException.ThrowIfNull( quiescePrevious );
		ArgumentNullException.ThrowIfNull( publishCandidate );
		ArgumentNullException.ThrowIfNull( rollbackPublication );
		ArgumentNullException.ThrowIfNull( restorePrevious );
		ArgumentNullException.ThrowIfNull( discardCandidate );
		ArgumentNullException.ThrowIfNull( retirePrevious );

		var previousQuiesceStarted = false;
		var publicationStarted = false;
		try
		{
			if ( !activateCandidate() )
				return Fail<T>( "Candidate activation was rejected.", false, false,
					rollbackPublication, restorePrevious, discardCandidate );
			previousQuiesceStarted = true;
			if ( !quiescePrevious() )
				return Fail<T>( "Previous-body quiescence was rejected.", false, true,
					rollbackPublication, restorePrevious, discardCandidate );
			publicationStarted = true;
			if ( !publishCandidate() )
				return Fail<T>( "Canonical body publication was rejected.", true, true,
					rollbackPublication, restorePrevious, discardCandidate );

			// Retirement must be implemented as a no-throw destroy-or-retain operation:
			// publication is already complete and cannot truthfully be reported as failed.
			retirePrevious();
			return OperationResult<T>.Success( candidate );
		}
		catch ( Exception exception )
		{
			return Fail<T>(
				$"Authoritative body replacement threw: {exception.Message}",
				publicationStarted,
				previousQuiesceStarted,
				rollbackPublication,
				restorePrevious,
				discardCandidate );
		}
	}

	private static OperationResult<T> Fail<T>(
		string message,
		bool publicationStarted,
		bool previousQuiesceStarted,
		Action rollbackPublication,
		Action restorePrevious,
		Action discardCandidate )
	{
		var cleanupFailures = new List<string>();
		if ( publicationStarted ) TryCleanup( rollbackPublication, "publication rollback", cleanupFailures );
		if ( previousQuiesceStarted ) TryCleanup( restorePrevious, "previous-body restoration", cleanupFailures );
		TryCleanup( discardCandidate, "candidate cleanup", cleanupFailures );
		if ( cleanupFailures.Count > 0 )
			message += $" Recovery degraded: {string.Join( "; ", cleanupFailures )}";
		return OperationResult<T>.Failure( ErrorCode.InternalError, message );
	}

	private static void TryCleanup( Action action, string stage, ICollection<string> failures )
	{
		try { action(); }
		catch ( Exception exception ) { failures.Add( $"{stage}: {exception.Message}" ); }
	}
}
