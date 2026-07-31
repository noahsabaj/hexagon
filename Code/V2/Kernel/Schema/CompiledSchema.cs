#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Policies;
using KernelClassDefinition = Hexagon.V2.Kernel.Definitions.ClassDefinition;
using KernelFactionDefinition = Hexagon.V2.Kernel.Definitions.FactionDefinition;
using KernelItemDefinition = Hexagon.V2.Kernel.Definitions.ItemDefinition;

namespace Hexagon.V2.Kernel.Schema;

public sealed record CompiledModule(string Id, IReadOnlyList<string> Dependencies);

public sealed class CompiledSchema
{
	private readonly IReadOnlyList<IPolicyRegistration> _policies;
	private readonly IReadOnlyList<IEventRegistration> _events;

	internal CompiledSchema(
		string id,
		IReadOnlyList<CompiledModule> modules,
		DefinitionRegistry<CharacterFieldDefinition> characterFields,
		DefinitionRegistry<KernelFactionDefinition> factions,
		DefinitionRegistry<KernelClassDefinition> classes,
		DefinitionRegistry<KernelItemDefinition> items,
		DefinitionRegistry<ActionDefinition> actions,
		DefinitionRegistry<ChatChannelDefinition> chatChannels,
		DefinitionRegistry<CommandDefinition> commands,
		DefinitionRegistry<PermissionDefinition> permissions,
		DefinitionRegistry<PanelDefinition> panels,
		DefinitionRegistry<CharacterInitializerDefinition> initializers,
		ConfigRegistry configs,
		DefinitionRegistry<PersistedTypeRegistration> persistedTypes,
		IReadOnlyList<IPolicyRegistration> policies,
		IReadOnlyList<IEventRegistration> events)
	{
		Id = id;
		Modules = modules;
		CharacterFields = characterFields;
		Factions = factions;
		Classes = classes;
		Items = items;
		Actions = actions;
		ChatChannels = chatChannels;
		Commands = commands;
		Permissions = permissions;
		Panels = panels;
		Initializers = initializers;
		Configs = configs;
		PersistedTypes = persistedTypes;
		_policies = policies;
		_events = events;
	}

	public string Id { get; }
	public IReadOnlyList<CompiledModule> Modules { get; }
	public DefinitionRegistry<CharacterFieldDefinition> CharacterFields { get; }
	public DefinitionRegistry<KernelFactionDefinition> Factions { get; }
	public DefinitionRegistry<KernelClassDefinition> Classes { get; }
	public DefinitionRegistry<KernelItemDefinition> Items { get; }
	public DefinitionRegistry<ActionDefinition> Actions { get; }
	public DefinitionRegistry<ChatChannelDefinition> ChatChannels { get; }
	public DefinitionRegistry<CommandDefinition> Commands { get; }
	public DefinitionRegistry<PermissionDefinition> Permissions { get; }
	public DefinitionRegistry<PanelDefinition> Panels { get; }
	public DefinitionRegistry<CharacterInitializerDefinition> Initializers { get; }
	public ConfigRegistry Configs { get; }
	public DefinitionRegistry<PersistedTypeRegistration> PersistedTypes { get; }

	public OperationResult<PolicyPipeline<TContext>> CreatePolicyPipeline<TContext>(
		Action<PolicyDiagnostic>? diagnostics = null)
		=> CreatePolicyPipeline(
			Array.Empty<PolicyHandler<TContext>>(),
			diagnostics);

	/// <summary>
	/// Composes schema policies with handlers supplied by the active runtime scope.
	/// The schema built-in remains mandatory and always runs first; every other
	/// handler is evaluated in deterministic order by <see cref="PolicyPipeline{TContext}"/>.
	/// </summary>
	public OperationResult<PolicyPipeline<TContext>> CreatePolicyPipeline<TContext>(
		IEnumerable<PolicyHandler<TContext>> additionalHandlers,
		Action<PolicyDiagnostic>? diagnostics = null)
	{
		var registrations = _policies
			.Where(x => x.ContextType == typeof(TContext))
			.Cast<PolicyRegistration<TContext>>()
			.ToArray();

		var builtIn = registrations.SingleOrDefault(x => x.IsBuiltIn);
		if (builtIn is null)
		{
			return OperationResult<PolicyPipeline<TContext>>.Failure(
				ErrorCode.UnknownDefinition,
				$"No built-in policy is registered for {typeof(TContext).FullName}.");
		}

		if (additionalHandlers is null)
		{
			return OperationResult<PolicyPipeline<TContext>>.Failure(
				ErrorCode.InvalidArgument,
				"Additional policy handlers cannot be null.");
		}

		PolicyHandler<TContext>[] runtimeHandlers;
		try
		{
			runtimeHandlers = additionalHandlers.ToArray();
		}
		catch (Exception)
		{
			return OperationResult<PolicyPipeline<TContext>>.Failure(
				ErrorCode.InvalidArgument,
				"Additional policy handlers could not be enumerated.");
		}

		var knownIds = new HashSet<string>(
			registrations.Select(x => x.Id),
			StringComparer.Ordinal);
		foreach (var runtimeHandler in runtimeHandlers)
		{
			if (runtimeHandler is null || runtimeHandler.Handler is null)
			{
				return OperationResult<PolicyPipeline<TContext>>.Failure(
					ErrorCode.InvalidArgument,
					"Every additional policy registration requires a handler.");
			}

			if (!KernelIdentifier.IsValid(runtimeHandler.Id))
			{
				return OperationResult<PolicyPipeline<TContext>>.Failure(
					ErrorCode.InvalidIdentifier,
					$"Additional policy identifier '{runtimeHandler.Id}' is invalid.");
			}

			if (!knownIds.Add(runtimeHandler.Id))
			{
				return OperationResult<PolicyPipeline<TContext>>.Failure(
					ErrorCode.DuplicateRegistration,
					$"Policy identifier '{runtimeHandler.Id}' is already registered for {typeof(TContext).FullName}.");
			}
		}

		var pipeline = new PolicyPipeline<TContext>(
			new PolicyHandler<TContext>(builtIn.Id, builtIn.TypedHandler, builtIn.Order),
			registrations
				.Where(x => !x.IsBuiltIn)
				.Select(x => new PolicyHandler<TContext>(x.Id, x.TypedHandler, x.Order))
				.Concat(runtimeHandlers),
			diagnostics);

		return OperationResult<PolicyPipeline<TContext>>.Success(pipeline);
	}

	public PostCommitEventBus<TEvent> CreateEventBus<TEvent>(
		Action<EventHandlerFailure>? diagnostics = null)
	{
		var handlers = _events
			.Where(x => x.EventType == typeof(TEvent))
			.Cast<EventRegistration<TEvent>>()
			.Select(x => new EventHandlerRegistration<TEvent>(x.Id, x.TypedHandler, x.Order));

		return new PostCommitEventBus<TEvent>(handlers, diagnostics);
	}
}

public sealed record ConformanceIssue(ErrorCode Code, string Path, string Message);

public sealed class ConformanceReport
{
	internal ConformanceReport(IReadOnlyList<ConformanceIssue> issues)
	{
		Issues = issues;
	}

	public IReadOnlyList<ConformanceIssue> Issues { get; }
	public bool IsValid => Issues.Count == 0;
}
