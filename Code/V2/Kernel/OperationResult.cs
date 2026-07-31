#nullable enable

using System;
using System.Collections.Generic;

namespace Hexagon.V2.Kernel;

/// <summary>
/// Stable, transport-safe failure categories used by the v2 application surface.
/// </summary>
public enum ErrorCode
{
	None = 0,
	InvalidArgument = 1,
	InvalidIdentifier = 2,
	DuplicateRegistration = 3,
	MissingDependency = 4,
	DependencyCycle = 5,
	UnknownDefinition = 6,
	SchemaConfigurationFailed = 7,
	SchemaInvalid = 8,
	PolicyDenied = 9,
	PolicyFailed = 10,
	EventHandlerFailed = 11,
	ConfigurationInvalid = 12,
	ConfigurationTypeMismatch = 13,
	PersistedTypeInvalid = 14,
	Conflict = 15,
	NotFound = 16,
	Unauthorized = 17,
	InternalError = 18,
	RateLimited = 19,
	InvariantViolation = 20,
	LeaseUnavailable = 21,
	StorageLimitExceeded = 22,
	StaleTransaction = 23,
	ReconciliationPending = 24
}

public sealed record OperationError(
	ErrorCode Code,
	string Message,
	IReadOnlyDictionary<string, string>? Details = null
);

/// <summary>
/// Non-generic result for commands which have no success payload.
/// </summary>
public readonly struct OperationResult
{
	private OperationResult(bool succeeded, OperationError? error)
	{
		Succeeded = succeeded;
		Error = error;
	}

	public bool Succeeded { get; }
	public bool Failed => !Succeeded;
	public OperationError? Error { get; }

	public static OperationResult Success() => new(true, null);

	public static OperationResult Failure(ErrorCode code, string message,
		IReadOnlyDictionary<string, string>? details = null)
	{
		if (code == ErrorCode.None)
			throw new ArgumentException("A failure must have a non-zero error code.", nameof(code));

		if (string.IsNullOrWhiteSpace(message))
			throw new ArgumentException("A failure must have a message.", nameof(message));

		return new OperationResult(false, new OperationError(code, message, details));
	}

	public OperationResult<T> WithValue<T>(T value)
	{
		return Succeeded
			? OperationResult<T>.Success(value)
			: OperationResult<T>.Failure(Error!.Code, Error.Message, Error.Details);
	}
}

/// <summary>
/// A success value or one stable, user-safe failure. Access to Value is guarded.
/// </summary>
public readonly struct OperationResult<T>
{
	private readonly T? _value;

	private OperationResult(bool succeeded, T? value, OperationError? error)
	{
		Succeeded = succeeded;
		_value = value;
		Error = error;
	}

	public bool Succeeded { get; }
	public bool Failed => !Succeeded;
	public OperationError? Error { get; }

	public T Value => Succeeded
		? _value!
		: throw new InvalidOperationException($"A failed result has no value ({Error?.Code}).");

	public static OperationResult<T> Success(T value) => new(true, value, null);

	public static OperationResult<T> Failure(ErrorCode code, string message,
		IReadOnlyDictionary<string, string>? details = null)
	{
		if (code == ErrorCode.None)
			throw new ArgumentException("A failure must have a non-zero error code.", nameof(code));

		if (string.IsNullOrWhiteSpace(message))
			throw new ArgumentException("A failure must have a message.", nameof(message));

		return new OperationResult<T>(false, default, new OperationError(code, message, details));
	}

	public bool TryGetValue(out T? value)
	{
		value = _value;
		return Succeeded;
	}
}

internal static class KernelIdentifier
{
	public static bool IsValid(string? value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > 96)
			return false;

		if (!IsLowerAlphaNumeric(value[0]))
			return false;

		for (var i = 1; i < value.Length; i++)
		{
			var c = value[i];
			if (!IsLowerAlphaNumeric(c) && c != '.' && c != '_' && c != '-')
				return false;
		}

		return true;
	}

	private static bool IsLowerAlphaNumeric(char c)
		=> c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
