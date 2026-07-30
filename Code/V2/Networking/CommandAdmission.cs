#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

public enum CommandAdmissionFailure
{
	None = 0,
	RateLimited = 1,
	Duplicate = 2,
	Disconnected = 3
}

public readonly record struct CommandAdmissionResult(
	bool Accepted,
	CommandAdmissionFailure Failure,
	TimeSpan RetryAfter )
{
	public static CommandAdmissionResult Success() => new( true, CommandAdmissionFailure.None, TimeSpan.Zero );
	public static CommandAdmissionResult Reject( CommandAdmissionFailure failure, TimeSpan retryAfter = default ) =>
		new( false, failure, retryAfter );
}

public static class SchemaCommandAdmissionCost
{
	public const int UnknownCommandUnits = 8;

	public static int Resolve( string? commandId, Func<string, int?> registeredCost )
	{
		ArgumentNullException.ThrowIfNull( registeredCost );
		if ( !ClientPayloadLimits.IsAdmissionSafeIdentifier( commandId ) ) return UnknownCommandUnits;
		return registeredCost( commandId! ) switch
		{
			1 => 1,
			2 => 2,
			4 => 4,
			_ => UnknownCommandUnits
		};
	}
}

public readonly record struct CommandIngressEvaluation<TActor>(
	bool Authenticated,
	CommandAdmissionResult Admission,
	OperationResult<TActor>? Session )
{
	public bool SessionEvaluated => Session.HasValue;
}

/// <summary>
/// Orders the authenticated command boundary so a known caller is charged before
/// its nonce/epoch scope is inspected. Unauthenticated traffic never reaches the
/// per-connection bucket, and a rejected scope closes an accepted request without
/// refunding its weighted cost.
/// </summary>
public static class CommandIngressAdmission
{
	public static CommandIngressEvaluation<TActor> Evaluate<TActor>(
		bool authenticated,
		Func<CommandAdmissionResult> tryBegin,
		Func<OperationResult<TActor>> resolveSession,
		Action finishRejectedSession )
	{
		ArgumentNullException.ThrowIfNull( tryBegin );
		ArgumentNullException.ThrowIfNull( resolveSession );
		ArgumentNullException.ThrowIfNull( finishRejectedSession );
		if ( !authenticated )
		{
			return new CommandIngressEvaluation<TActor>(
				false,
				CommandAdmissionResult.Reject( CommandAdmissionFailure.Disconnected ),
				null );
		}

		var admission = tryBegin();
		if ( !admission.Accepted )
			return new CommandIngressEvaluation<TActor>( true, admission, null );

		OperationResult<TActor> session;
		try
		{
			session = resolveSession();
		}
		catch
		{
			finishRejectedSession();
			throw;
		}
		if ( session.Failed ) finishRejectedSession();
		return new CommandIngressEvaluation<TActor>( true, admission, session );
	}
}

/// <summary>
/// Per-connection weighted token bucket plus active-request and replay bounds.
/// Tokens are charged before all other admission checks and are never refunded.
/// </summary>
public sealed class CommandAdmissionController
{
	public const int BurstUnits = 16;
	public const int RefillUnitsPerSecond = 8;
	public const int MaximumActiveRequests = 16;
	public const int RetainedRequestIds = 1024;

	private readonly object _sync = new();
	private readonly HashSet<CommandRequestId> _active = new();
	private readonly HashSet<CommandRequestId> _seen = new();
	private readonly Queue<CommandRequestId> _seenOrder = new();
	private readonly long _frequency;
	private double _tokens = BurstUnits;
	private long _lastTimestamp;
	private bool _started;
	private bool _disconnected;

	public CommandAdmissionController( long timestampFrequency )
	{
		if ( timestampFrequency <= 0 ) throw new ArgumentOutOfRangeException( nameof(timestampFrequency) );
		_frequency = timestampFrequency;
	}

	public int ActiveCount
	{
		get { lock ( _sync ) return _active.Count; }
	}
	public double AvailableUnits
	{
		get { lock ( _sync ) return _tokens; }
	}

	/// <summary>
	/// Raised the first time this controller charges anything, so a reader can confirm from a live
	/// log that admission metering actually runs rather than inferring it from a green suite. Audit
	/// round 2 (O3) has never been observed executing, and the one round-2 item that WAS watched
	/// immediately exposed a defect the suites could not reach.
	/// </summary>
	public static Action<int, int>? FirstChargeObserver { get; set; }

	private bool _observedFirstCharge;

	public CommandAdmissionResult TryBegin( CommandRequestId requestId, int cost, long timestamp )
	{
		if ( cost <= 0 || cost > BurstUnits ) throw new ArgumentOutOfRangeException( nameof(cost) );
		if ( !_observedFirstCharge )
		{
			_observedFirstCharge = true;
			FirstChargeObserver?.Invoke( cost, BurstUnits );
		}

		lock ( _sync )
		{
			Refill( timestamp );
			if ( _disconnected ) return CommandAdmissionResult.Reject( CommandAdmissionFailure.Disconnected );

			if ( _tokens < cost )
			{
				var missing = cost - _tokens;
				return CommandAdmissionResult.Reject(
					CommandAdmissionFailure.RateLimited,
					TimeSpan.FromSeconds( missing / RefillUnitsPerSecond ) );
			}
			_tokens -= cost;

			if ( _active.Count >= MaximumActiveRequests )
				return CommandAdmissionResult.Reject( CommandAdmissionFailure.RateLimited, TimeSpan.FromMilliseconds( 125 ) );
			if ( _seen.Contains( requestId ) || !_active.Add( requestId ) )
				return CommandAdmissionResult.Reject( CommandAdmissionFailure.Duplicate );
			return CommandAdmissionResult.Success();
		}
	}

	public bool Finish( CommandRequestId requestId )
	{
		lock ( _sync )
		{
			if ( !_active.Remove( requestId ) ) return false;
			_seen.Add( requestId );
			_seenOrder.Enqueue( requestId );
			while ( _seenOrder.Count > RetainedRequestIds ) _seen.Remove( _seenOrder.Dequeue() );
			return true;
		}
	}

	public void Disconnect()
	{
		lock ( _sync )
		{
			_disconnected = true;
			_active.Clear();
		}
	}

	private void Refill( long timestamp )
	{
		if ( !_started )
		{
			_started = true;
			_lastTimestamp = timestamp;
			return;
		}
		if ( timestamp <= _lastTimestamp ) return;
		var elapsed = (double)(timestamp - _lastTimestamp) / _frequency;
		_tokens = Math.Min( BurstUnits, _tokens + elapsed * RefillUnitsPerSecond );
		_lastTimestamp = timestamp;
	}
}
