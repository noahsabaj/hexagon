#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Kernel.Events;

public interface IEventHandler<in TEvent>
{
	void Handle(TEvent @event);
}

public sealed record EventHandlerRegistration<TEvent>(
	string Id,
	IEventHandler<TEvent> Handler,
	int Order = 0
);

public sealed record EventHandlerFailure(
	string HandlerId,
	Exception Exception
);

public sealed record EventDispatchReport(
	int InvokedCount,
	IReadOnlyList<EventHandlerFailure> Failures
)
{
	public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Synchronous post-commit fan-out. One broken listener never prevents later
/// listeners from observing the already committed event.
/// </summary>
public sealed class PostCommitEventBus<TEvent>
{
	private readonly IReadOnlyList<EventHandlerRegistration<TEvent>> _handlers;
	private readonly Action<EventHandlerFailure>? _diagnostics;

	public PostCommitEventBus(IEnumerable<EventHandlerRegistration<TEvent>>? handlers = null,
		Action<EventHandlerFailure>? diagnostics = null)
	{
		_handlers = (handlers ?? Array.Empty<EventHandlerRegistration<TEvent>>())
			.OrderBy(x => x.Order)
			.ThenBy(x => x.Id, StringComparer.Ordinal)
			.ToArray();
		_diagnostics = diagnostics;
	}

	public EventDispatchReport Publish(TEvent @event)
	{
		var failures = new List<EventHandlerFailure>();
		var invoked = 0;

		foreach (var registration in _handlers)
		{
			try
			{
				invoked++;
				registration.Handler.Handle(@event);
			}
			catch (Exception exception)
			{
				var failure = new EventHandlerFailure(registration.Id, exception);
				failures.Add(failure);
				TryReportDiagnostic(failure);
			}
		}

		return new EventDispatchReport(invoked, failures);
	}

	private void TryReportDiagnostic(EventHandlerFailure failure)
	{
		try
		{
			_diagnostics?.Invoke(failure);
		}
		catch
		{
			// A logging failure cannot interrupt post-commit fan-out.
		}
	}
}

internal interface IEventRegistration
{
	string Id { get; }
	Type EventType { get; }
	int Order { get; }
	object Handler { get; }
}

internal sealed record EventRegistration<TEvent>(
	string Id,
	IEventHandler<TEvent> TypedHandler,
	int Order
) : IEventRegistration
{
	public Type EventType => typeof(TEvent);
	public object Handler => TypedHandler;
}
