#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Kernel.Policies;

public interface IPolicy<in TContext>
{
	PolicyDecision Evaluate(TContext context);
}

public readonly record struct PolicyDecision(
	bool Allowed,
	ErrorCode ErrorCode,
	string? Message
)
{
	public static PolicyDecision Allow() => new(true, ErrorCode.None, null);

	public static PolicyDecision Deny(string message, ErrorCode code = ErrorCode.PolicyDenied)
	{
		if (code == ErrorCode.None)
			throw new ArgumentException("A denial must have a non-zero error code.", nameof(code));

		return new PolicyDecision(false, code, message);
	}
}

public sealed record PolicyHandler<TContext>(
	string Id,
	IPolicy<TContext> Handler,
	int Order = 0
);

public sealed record PolicyDiagnostic(
	Type ContextType,
	string HandlerId,
	Exception Exception
);

/// <summary>
/// Fail-closed policy composition: the built-in rule runs first, then every
/// registered rule must explicitly allow. The first denial wins.
/// </summary>
public sealed class PolicyPipeline<TContext>
{
	private readonly PolicyHandler<TContext> _builtIn;
	private readonly IReadOnlyList<PolicyHandler<TContext>> _handlers;
	private readonly Action<PolicyDiagnostic>? _diagnostics;

	public PolicyPipeline(PolicyHandler<TContext> builtIn,
		IEnumerable<PolicyHandler<TContext>>? handlers = null,
		Action<PolicyDiagnostic>? diagnostics = null)
	{
		_builtIn = builtIn ?? throw new ArgumentNullException(nameof(builtIn));
		if (_builtIn.Handler is null)
			throw new ArgumentException("A built-in policy handler is required.", nameof(builtIn));

		_handlers = (handlers ?? Array.Empty<PolicyHandler<TContext>>())
			.OrderBy(x => x.Order)
			.ThenBy(x => x.Id, StringComparer.Ordinal)
			.ToArray();
		_diagnostics = diagnostics;
	}

	public OperationResult Evaluate(TContext context)
	{
		var result = EvaluateOne(_builtIn, context);
		if (result.Failed)
			return result;

		foreach (var handler in _handlers)
		{
			result = EvaluateOne(handler, context);
			if (result.Failed)
				return result;
		}

		return OperationResult.Success();
	}

	private OperationResult EvaluateOne(PolicyHandler<TContext> registration, TContext context)
	{
		try
		{
			var decision = registration.Handler.Evaluate(context);
			if (decision.Allowed)
				return OperationResult.Success();

			var code = decision.ErrorCode == ErrorCode.None
				? ErrorCode.PolicyDenied
				: decision.ErrorCode;
			var message = string.IsNullOrWhiteSpace(decision.Message)
				? $"Policy '{registration.Id}' denied the operation."
				: decision.Message!;

			return OperationResult.Failure(code, message,
				new Dictionary<string, string> { ["policy"] = registration.Id });
		}
		catch (Exception exception)
		{
			TryReportDiagnostic(new PolicyDiagnostic(typeof(TContext), registration.Id, exception));
			return OperationResult.Failure(
				ErrorCode.PolicyFailed,
				$"Policy '{registration.Id}' failed closed.",
				new Dictionary<string, string> { ["policy"] = registration.Id });
		}
	}

	private void TryReportDiagnostic(PolicyDiagnostic diagnostic)
	{
		try
		{
			_diagnostics?.Invoke(diagnostic);
		}
		catch
		{
			// Diagnostics must never turn a fail-closed decision into an exception.
		}
	}
}

internal interface IPolicyRegistration
{
	string Id { get; }
	Type ContextType { get; }
	bool IsBuiltIn { get; }
	int Order { get; }
	object Handler { get; }
}

internal sealed record PolicyRegistration<TContext>(
	string Id,
	IPolicy<TContext> TypedHandler,
	bool IsBuiltIn,
	int Order
) : IPolicyRegistration
{
	public Type ContextType => typeof(TContext);
	public object Handler => TypedHandler;
}
