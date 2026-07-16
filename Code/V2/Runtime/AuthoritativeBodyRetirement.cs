#nullable enable

using System;
using System.Collections.Generic;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Engine-neutral cleanup coordinator for a canonical body that is being
/// stripped. A body that survives destruction is retained for a later retry,
/// and every stage is attempted even when an earlier engine call throws.
/// </summary>
internal static class AuthoritativeBodyRetirement
{
	public static AuthoritativeBodyRetirementResult Retire(
		Func<bool> isValid,
		Action quiesce,
		Action destroy,
		Action retain )
	{
		ArgumentNullException.ThrowIfNull( isValid );
		ArgumentNullException.ThrowIfNull( quiesce );
		ArgumentNullException.ThrowIfNull( destroy );
		ArgumentNullException.ThrowIfNull( retain );

		var failures = new List<Exception>();
		if ( !ObserveValidity( isValid, failures, assumeValidOnFailure: true ) )
			return new AuthoritativeBodyRetirementResult( true, false, failures.AsReadOnly() );

		Try( quiesce, failures );
		Try( destroy, failures );
		var remainsValid = ObserveValidity( isValid, failures, assumeValidOnFailure: true );
		var retained = false;
		if ( remainsValid ) retained = Try( retain, failures );
		return new AuthoritativeBodyRetirementResult(
			!remainsValid,
			retained,
			failures.AsReadOnly() );
	}

	private static bool ObserveValidity(
		Func<bool> isValid,
		ICollection<Exception> failures,
		bool assumeValidOnFailure )
	{
		try { return isValid(); }
		catch ( Exception exception )
		{
			failures.Add( exception );
			return assumeValidOnFailure;
		}
	}

	private static bool Try( Action action, ICollection<Exception> failures )
	{
		try
		{
			action();
			return true;
		}
		catch ( Exception exception )
		{
			failures.Add( exception );
			return false;
		}
	}
}

internal sealed record AuthoritativeBodyRetirementResult(
	bool IsClean,
	bool Retained,
	IReadOnlyList<Exception> Failures );
