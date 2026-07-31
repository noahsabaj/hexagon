#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Policies;
using KernelClassDefinition = Hexagon.V2.Kernel.Definitions.ClassDefinition;
using KernelFactionDefinition = Hexagon.V2.Kernel.Definitions.FactionDefinition;
using KernelItemDefinition = Hexagon.V2.Kernel.Definitions.ItemDefinition;

namespace Hexagon.V2.Kernel.Schema;

public interface IHexSchema
{
	string Id { get; }
	void Configure(SchemaBuilder builder);
}

public interface IHexModule
{
	string Id { get; }
	void Configure(ModuleBuilder builder);
}

public abstract class RegistrationBuilder
{
	private readonly SchemaContributions _contributions;

	internal RegistrationBuilder(SchemaContributions contributions)
	{
		_contributions = contributions;
	}

	public void RegisterCharacterField(CharacterFieldDefinition definition)
		=> _contributions.CharacterFields.Add(Required(definition));

	public void RegisterFaction(KernelFactionDefinition definition)
		=> _contributions.Factions.Add(Required(definition));

	public void RegisterClass(KernelClassDefinition definition)
		=> _contributions.Classes.Add(Required(definition));

	public void RegisterItem(KernelItemDefinition definition)
		=> _contributions.Items.Add(Required(definition));

	public void RegisterAction(ActionDefinition definition)
		=> _contributions.Actions.Add(Required(definition));

	public void RegisterChatChannel(ChatChannelDefinition definition)
		=> _contributions.ChatChannels.Add(Required(definition));

	public void RegisterCommand(CommandDefinition definition)
		=> _contributions.Commands.Add(Required(definition));

	public void RegisterPermission(PermissionDefinition definition)
		=> _contributions.Permissions.Add(Required(definition));

	public void RegisterPanel(PanelDefinition definition)
		=> _contributions.Panels.Add(Required(definition));

	public void RegisterInitializer(CharacterInitializerDefinition definition)
		=> _contributions.Initializers.Add(Required(definition));

	public void RegisterConfig(IConfigDefinition definition)
		=> _contributions.Configs.Add(Required(definition));

	public void RegisterPersistedType(PersistedTypeRegistration registration)
		=> _contributions.PersistedTypes.Add(Required(registration));

	public void RegisterBuiltInPolicy<TContext>(string id, IPolicy<TContext> policy, int order = 0)
		=> RegisterPolicyCore(id, policy, true, order);

	public void RegisterPolicy<TContext>(string id, IPolicy<TContext> policy, int order = 0)
		=> RegisterPolicyCore(id, policy, false, order);

	public void RegisterEventHandler<TEvent>(string id, IEventHandler<TEvent> handler, int order = 0)
	{
		if (handler is null)
			throw new ArgumentNullException(nameof(handler));

		_contributions.Events.Add(new EventRegistration<TEvent>(id, handler, order));
	}

	private void RegisterPolicyCore<TContext>(string id, IPolicy<TContext> policy,
		bool isBuiltIn, int order)
	{
		if (policy is null)
			throw new ArgumentNullException(nameof(policy));

		_contributions.Policies.Add(new PolicyRegistration<TContext>(id, policy, isBuiltIn, order));
	}

	private static T Required<T>(T? value) where T : class
		=> value ?? throw new ArgumentNullException(nameof(value));
}

public sealed class SchemaBuilder : RegistrationBuilder
{
	private readonly List<IHexModule> _modules = new();

	internal SchemaBuilder(SchemaContributions contributions) : base(contributions)
	{
	}

	internal IReadOnlyList<IHexModule> Modules => _modules;

	public void AddModule(IHexModule module)
		=> _modules.Add(module ?? throw new ArgumentNullException(nameof(module)));
}

public sealed class ModuleBuilder : RegistrationBuilder
{
	private readonly List<string> _dependencies = new();

	internal ModuleBuilder(string moduleId, SchemaContributions contributions) : base(contributions)
	{
		ModuleId = moduleId;
	}

	public string ModuleId { get; }
	internal IReadOnlyList<string> Dependencies => _dependencies;

	public void DependsOn(params string[] moduleIds)
	{
		if (moduleIds is null)
			throw new ArgumentNullException(nameof(moduleIds));

		foreach (var moduleId in moduleIds)
			_dependencies.Add(moduleId);
	}
}

internal sealed class SchemaContributions
{
	public List<CharacterFieldDefinition> CharacterFields { get; } = new();
	public List<KernelFactionDefinition> Factions { get; } = new();
	public List<KernelClassDefinition> Classes { get; } = new();
	public List<KernelItemDefinition> Items { get; } = new();
	public List<ActionDefinition> Actions { get; } = new();
	public List<ChatChannelDefinition> ChatChannels { get; } = new();
	public List<CommandDefinition> Commands { get; } = new();
	public List<PermissionDefinition> Permissions { get; } = new();
	public List<PanelDefinition> Panels { get; } = new();
	public List<CharacterInitializerDefinition> Initializers { get; } = new();
	public List<IConfigDefinition> Configs { get; } = new();
	public List<PersistedTypeRegistration> PersistedTypes { get; } = new();
	public List<IPolicyRegistration> Policies { get; } = new();
	public List<IEventRegistration> Events { get; } = new();

	public void AppendTo(SchemaContributions destination)
	{
		destination.CharacterFields.AddRange(CharacterFields);
		destination.Factions.AddRange(Factions);
		destination.Classes.AddRange(Classes);
		destination.Items.AddRange(Items);
		destination.Actions.AddRange(Actions);
		destination.ChatChannels.AddRange(ChatChannels);
		destination.Commands.AddRange(Commands);
		destination.Permissions.AddRange(Permissions);
		destination.Panels.AddRange(Panels);
		destination.Initializers.AddRange(Initializers);
		destination.Configs.AddRange(Configs);
		destination.PersistedTypes.AddRange(PersistedTypes);
		destination.Policies.AddRange(Policies);
		destination.Events.AddRange(Events);
	}
}

internal sealed record ConfiguredModule(
	string Id,
	IReadOnlyList<string> Dependencies,
	SchemaContributions Contributions
);
