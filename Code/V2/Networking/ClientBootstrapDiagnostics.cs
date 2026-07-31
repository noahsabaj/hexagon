#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

/// <summary>
/// Stable bootstrap stages which may report one bounded failure to the host.
/// This is intentionally not a general-purpose client logging channel.
/// </summary>
public enum ClientBootstrapDiagnosticPhase
{
	Bootstrap = 1,
	SchemaDiscovery = 2,
	ClientConfiguration = 3
}

/// <summary>Allowlisted client bootstrap failure categories.</summary>
public enum ClientBootstrapDiagnosticCode
{
	BootstrapUnavailable = 1,
	SchemaRuntimeUnavailable = 2,
	ClientConfigurationFailed = 3
}

public readonly record struct ClientBootstrapDiagnostic(
	ClientBootstrapDiagnosticPhase Phase,
	ClientBootstrapDiagnosticCode Code,
	string Detail );

/// <summary>
/// Validates the fixed bootstrap diagnostic vocabulary and converts detail into
/// a bounded, single-line value safe to include in a structured host log entry.
/// </summary>
public static class ClientBootstrapDiagnosticContract
{
	public const int MaximumDetailCharacters = 256;
	public const int MaximumDetailUtf8Bytes = 512;

	public static string PrepareDetail( ClientBootstrapDiagnosticCode code, string? detail )
	{
		var normalized = NormalizeBounded( detail ?? string.Empty );
		return normalized.Length == 0 ? DefaultDetail( code ) : normalized;
	}

	public static OperationResult<ClientBootstrapDiagnostic> Validate(
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code,
		string? detail )
	{
		if ( !IsAllowedPair( phase, code ) )
			return Failure( "The bootstrap diagnostic phase and code are not allowlisted." );
		if ( detail is null )
			return Failure( "The bootstrap diagnostic detail is null." );
		if ( detail.Length > MaximumDetailCharacters )
			return Failure( $"The bootstrap diagnostic detail exceeds {MaximumDetailCharacters} characters." );
		if ( !HasValidUnicode( detail ) )
			return Failure( "The bootstrap diagnostic detail contains invalid Unicode." );
		if ( Encoding.UTF8.GetByteCount( detail ) > MaximumDetailUtf8Bytes )
			return Failure( $"The bootstrap diagnostic detail exceeds {MaximumDetailUtf8Bytes} UTF-8 bytes." );

		var normalized = NormalizeBounded( detail );
		if ( normalized.Length == 0 ) normalized = DefaultDetail( code );
		return OperationResult<ClientBootstrapDiagnostic>.Success(
			new ClientBootstrapDiagnostic( phase, code, normalized ) );
	}

	public static string EscapeLogValue( string detail )
	{
		ArgumentNullException.ThrowIfNull( detail );
		var escaped = new StringBuilder( detail.Length );
		foreach ( var character in detail )
		{
			switch ( character )
			{
				case '\\':
					escaped.Append( "\\\\" );
					break;
				case '"':
					escaped.Append( "\\\"" );
					break;
				default:
					escaped.Append( char.IsControl( character ) || char.IsWhiteSpace( character )
						? ' '
						: character );
					break;
			}
		}
		return escaped.ToString();
	}

	private static bool IsAllowedPair(
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code ) => (phase, code) switch
	{
		(ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable) => true,
		(ClientBootstrapDiagnosticPhase.SchemaDiscovery,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable) => true,
		(ClientBootstrapDiagnosticPhase.ClientConfiguration,
			ClientBootstrapDiagnosticCode.ClientConfigurationFailed) => true,
		_ => false
	};

	private static string DefaultDetail( ClientBootstrapDiagnosticCode code ) => code switch
	{
		ClientBootstrapDiagnosticCode.BootstrapUnavailable => "Hexagon bootstrap is unavailable.",
		ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable => "Schema runtime is unavailable.",
		ClientBootstrapDiagnosticCode.ClientConfigurationFailed => "Schema client configuration failed.",
		_ => "Client bootstrap failed."
	};

	private static string NormalizeBounded( string value )
	{
		var normalized = new StringBuilder( Math.Min( value.Length, MaximumDetailCharacters ) );
		var utf8Bytes = 0;
		var pendingSpace = false;
		for ( var index = 0; index < value.Length; index++ )
		{
			var current = value[index];
			if ( char.IsControl( current ) || char.IsWhiteSpace( current ) )
			{
				pendingSpace = normalized.Length > 0;
				continue;
			}

			var characterCount = 1;
			var encodedBytes = current <= 0x7f ? 1 : current <= 0x7ff ? 2 : 3;
			if ( char.IsHighSurrogate( current ) )
			{
				if ( index + 1 < value.Length && char.IsLowSurrogate( value[index + 1] ) )
				{
					characterCount = 2;
					encodedBytes = 4;
				}
				else
				{
					current = '?';
					encodedBytes = 1;
				}
			}
			else if ( char.IsLowSurrogate( current ) )
			{
				current = '?';
				encodedBytes = 1;
			}

			var spaceBytes = pendingSpace ? 1 : 0;
			if ( normalized.Length + spaceBytes + characterCount > MaximumDetailCharacters ||
				utf8Bytes + spaceBytes + encodedBytes > MaximumDetailUtf8Bytes )
				break;
			if ( pendingSpace )
			{
				normalized.Append( ' ' );
				utf8Bytes++;
				pendingSpace = false;
			}
			normalized.Append( current );
			if ( characterCount == 2 ) normalized.Append( value[++index] );
			utf8Bytes += encodedBytes;
		}
		return normalized.ToString();
	}

	private static bool HasValidUnicode( string value )
	{
		for ( var index = 0; index < value.Length; index++ )
		{
			if ( char.IsHighSurrogate( value[index] ) )
			{
				if ( index + 1 >= value.Length || !char.IsLowSurrogate( value[++index] ) ) return false;
			}
			else if ( char.IsLowSurrogate( value[index] ) ) return false;
		}
		return true;
	}

	private static OperationResult<ClientBootstrapDiagnostic> Failure( string message ) =>
		OperationResult<ClientBootstrapDiagnostic>.Failure( ErrorCode.InvalidArgument, message );
}

public enum ClientBootstrapDiagnosticAdmissionFailure
{
	None = 0,
	Malformed = 1,
	Duplicate = 2,
	LimitReached = 3,
	Disconnected = 4
}

public readonly record struct ClientBootstrapDiagnosticAdmissionResult(
	bool Accepted,
	ClientBootstrapDiagnosticAdmissionFailure Failure,
	ClientBootstrapDiagnostic Diagnostic );

/// <summary>
/// Lifetime, per-connection admission for pre-session bootstrap diagnostics.
/// Every connected caller attempt is charged before payload validation; accepted
/// phase/code pairs can be published at most once.
/// </summary>
public sealed class ClientBootstrapDiagnosticAdmissionController
{
	public const int MaximumAttempts = 4;

	private readonly object _sync = new();
	private readonly HashSet<(ClientBootstrapDiagnosticPhase Phase, ClientBootstrapDiagnosticCode Code)> _published = new();
	private int _attempts;
	private bool _disconnected;

	public int AttemptCount
	{
		get { lock ( _sync ) return _attempts; }
	}

	public int PublishedCount
	{
		get { lock ( _sync ) return _published.Count; }
	}

	public ClientBootstrapDiagnosticAdmissionResult TryAccept(
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code,
		string? detail )
	{
		lock ( _sync )
		{
			if ( _disconnected ) return Reject( ClientBootstrapDiagnosticAdmissionFailure.Disconnected );
			if ( _attempts >= MaximumAttempts )
				return Reject( ClientBootstrapDiagnosticAdmissionFailure.LimitReached );
			_attempts++;
		}

		var validated = ClientBootstrapDiagnosticContract.Validate( phase, code, detail );
		if ( validated.Failed ) return Reject( ClientBootstrapDiagnosticAdmissionFailure.Malformed );

		lock ( _sync )
		{
			if ( _disconnected ) return Reject( ClientBootstrapDiagnosticAdmissionFailure.Disconnected );
			var diagnostic = validated.Value;
			if ( !_published.Add( (diagnostic.Phase, diagnostic.Code) ) )
				return Reject( ClientBootstrapDiagnosticAdmissionFailure.Duplicate );
			return new ClientBootstrapDiagnosticAdmissionResult(
				true,
				ClientBootstrapDiagnosticAdmissionFailure.None,
				diagnostic );
		}
	}

	public void Disconnect()
	{
		lock ( _sync ) _disconnected = true;
	}

	private static ClientBootstrapDiagnosticAdmissionResult Reject(
		ClientBootstrapDiagnosticAdmissionFailure failure ) => new( false, failure, default );
}
