#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>
/// Configures a schema, deterministically orders its modules, validates the
/// complete registration graph, and publishes immutable registries only when
/// every conformance rule passes.
/// </summary>
public static class SchemaCompiler
{
	public static ConformanceReport Validate(IHexSchema schema)
	{
		var build = Build(schema);
		return new ConformanceReport(new ReadOnlyCollection<ConformanceIssue>(build.Issues));
	}

	public static OperationResult<CompiledSchema> Compile(IHexSchema schema)
	{
		var build = Build(schema);
		if (build.Issues.Count == 0 && build.Schema is not null)
			return OperationResult<CompiledSchema>.Success(build.Schema);

		var first = build.Issues[0];
		var details = new Dictionary<string, string>
		{
			["path"] = first.Path,
			["issue_count"] = build.Issues.Count.ToString()
		};

		return OperationResult<CompiledSchema>.Failure(
			ErrorCode.SchemaInvalid,
			$"Schema conformance failed: {first.Message}",
			details);
	}

	private static BuildResult Build(IHexSchema schema)
	{
		var issues = new List<ConformanceIssue>();
		if (schema is null)
		{
			issues.Add(new ConformanceIssue(ErrorCode.InvalidArgument, "schema",
				"A schema instance is required."));
			return new BuildResult(null, issues);
		}

		var schemaId = schema.Id;
		ValidateId(schemaId, "schema.id", issues);

		var root = new SchemaContributions();
		var schemaBuilder = new SchemaBuilder(root);
		try
		{
			schema.Configure(schemaBuilder);
		}
		catch (Exception exception)
		{
			issues.Add(new ConformanceIssue(ErrorCode.SchemaConfigurationFailed,
				"schema.configure",
				$"Schema configuration threw {exception.GetType().Name}."));
			return new BuildResult(null, issues);
		}

		var configuredModules = ConfigureModules(schemaBuilder.Modules, issues);
		var orderedModules = OrderModules(configuredModules, issues);

		var all = new SchemaContributions();
		root.AppendTo(all);
		foreach (var module in orderedModules)
			module.Contributions.AppendTo(all);

		var characterFields = BuildRegistry(all.CharacterFields, "character_fields", issues);
		var factions = BuildRegistry(all.Factions, "factions", issues);
		var classes = BuildRegistry(all.Classes, "classes", issues);
		var items = BuildRegistry(all.Items, "items", issues);
		var actions = BuildRegistry(all.Actions, "actions", issues);
		var channels = BuildRegistry(all.ChatChannels, "chat_channels", issues);
		var commands = BuildRegistry(all.Commands, "commands", issues);
		var permissions = BuildRegistry(all.Permissions, "permissions", issues);
		var panels = BuildRegistry(all.Panels, "panels", issues);
		var initializers = BuildRegistry(all.Initializers, "initializers", issues);
		var persistedTypes = BuildRegistry(all.PersistedTypes, "persisted_types", issues);
		var configs = BuildConfigRegistry(all.Configs, issues);

		ValidateCharacterFields(characterFields, issues);
		ValidateFactions(factions, classes, issues);
		ValidateItems(items, actions, issues);
		ValidatePermissionReferences(channels, commands, permissions, issues);
		ValidateConcreteTypes(panels.Values.Select(x => (x.Id, x.PanelType)), "panels", issues);
		ValidateConcreteTypes(initializers.Values.Select(x => (x.Id, x.InitializerType)),
			"initializers", issues);
		ValidatePersistedTypes(persistedTypes, issues);
		ValidatePolicies(all.Policies, issues);
		ValidateEvents(all.Events, issues);

		if (issues.Count > 0)
			return new BuildResult(null, issues);

		var compiledModules = orderedModules
			.Select(x => new CompiledModule(x.Id,
				new ReadOnlyCollection<string>(x.Dependencies.OrderBy(y => y, StringComparer.Ordinal).ToArray())))
			.ToArray();

		var compiled = new CompiledSchema(
			schemaId,
			compiledModules,
			new DefinitionRegistry<CharacterFieldDefinition>(characterFields),
			new DefinitionRegistry<KernelFactionDefinition>(factions),
			new DefinitionRegistry<KernelClassDefinition>(classes),
			new DefinitionRegistry<KernelItemDefinition>(items),
			new DefinitionRegistry<ActionDefinition>(actions),
			new DefinitionRegistry<ChatChannelDefinition>(channels),
			new DefinitionRegistry<CommandDefinition>(commands),
			new DefinitionRegistry<PermissionDefinition>(permissions),
			new DefinitionRegistry<PanelDefinition>(panels),
			new DefinitionRegistry<CharacterInitializerDefinition>(initializers),
			new ConfigRegistry(configs),
			new DefinitionRegistry<PersistedTypeRegistration>(persistedTypes),
			all.Policies.OrderBy(x => x.ContextType.FullName, StringComparer.Ordinal)
				.ThenBy(x => x.IsBuiltIn ? 0 : 1)
				.ThenBy(x => x.Order)
				.ThenBy(x => x.Id, StringComparer.Ordinal)
				.ToArray(),
			all.Events.OrderBy(x => x.EventType.FullName, StringComparer.Ordinal)
				.ThenBy(x => x.Order)
				.ThenBy(x => x.Id, StringComparer.Ordinal)
				.ToArray());

		return new BuildResult(compiled, issues);
	}

	private static IReadOnlyDictionary<string, ConfiguredModule> ConfigureModules(
		IReadOnlyList<IHexModule> modules, List<ConformanceIssue> issues)
	{
		var configured = new Dictionary<string, ConfiguredModule>(StringComparer.Ordinal);
		foreach (var module in modules)
		{
			var path = $"modules.{module.Id ?? "<null>"}";
			if (!ValidateId(module.Id, $"{path}.id", issues))
				continue;

			if (configured.ContainsKey(module.Id!))
			{
				issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
					$"Module '{module.Id}' is registered more than once."));
				continue;
			}

			var contributions = new SchemaContributions();
			var builder = new ModuleBuilder(module.Id!, contributions);
			try
			{
				module.Configure(builder);
			}
			catch (Exception exception)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaConfigurationFailed,
					$"{path}.configure",
					$"Module '{module.Id}' configuration threw {exception.GetType().Name}."));
			}

			configured.Add(module.Id!, new ConfiguredModule(module.Id!,
				new ReadOnlyCollection<string>(builder.Dependencies.ToArray()), contributions));
		}

		return new ReadOnlyDictionary<string, ConfiguredModule>(configured);
	}

	private static IReadOnlyList<ConfiguredModule> OrderModules(
		IReadOnlyDictionary<string, ConfiguredModule> modules, List<ConformanceIssue> issues)
	{
		var indegree = modules.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
		var dependents = modules.Keys.ToDictionary(x => x,
			_ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

		foreach (var module in modules.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
		{
			var uniqueDependencies = new HashSet<string>(StringComparer.Ordinal);
			foreach (var dependency in module.Dependencies)
			{
				var path = $"modules.{module.Id}.dependencies";
				if (!ValidateId(dependency, path, issues) || !uniqueDependencies.Add(dependency))
					continue;

				if (dependency == module.Id)
				{
					issues.Add(new ConformanceIssue(ErrorCode.DependencyCycle, path,
						$"Module '{module.Id}' cannot depend on itself."));
					continue;
				}

				if (!modules.ContainsKey(dependency))
				{
					issues.Add(new ConformanceIssue(ErrorCode.MissingDependency, path,
						$"Module '{module.Id}' requires missing module '{dependency}'."));
					continue;
				}

				indegree[module.Id]++;
				dependents[dependency].Add(module.Id);
			}
		}

		var ready = new SortedSet<string>(
			indegree.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
		var ordered = new List<ConfiguredModule>(modules.Count);

		while (ready.Count > 0)
		{
			var id = ready.Min!;
			ready.Remove(id);
			ordered.Add(modules[id]);

			foreach (var dependent in dependents[id])
			{
				indegree[dependent]--;
				if (indegree[dependent] == 0)
					ready.Add(dependent);
			}
		}

		if (ordered.Count != modules.Count)
		{
			var cycleMembers = indegree.Where(x => x.Value > 0).Select(x => x.Key)
				.OrderBy(x => x, StringComparer.Ordinal).ToArray();
			issues.Add(new ConformanceIssue(ErrorCode.DependencyCycle, "modules",
				$"Module dependency cycle detected among: {string.Join(", ", cycleMembers)}."));

			foreach (var id in modules.Keys.Except(ordered.Select(x => x.Id), StringComparer.Ordinal)
				.OrderBy(x => x, StringComparer.Ordinal))
				ordered.Add(modules[id]);
		}

		return ordered;
	}

	private static Dictionary<string, TDefinition> BuildRegistry<TDefinition>(
		IEnumerable<TDefinition> definitions, string category, List<ConformanceIssue> issues)
		where TDefinition : class, IDefinition
	{
		var result = new Dictionary<string, TDefinition>(StringComparer.Ordinal);
		foreach (var definition in definitions)
		{
			var path = $"{category}.{definition.Id ?? "<null>"}";
			if (!ValidateId(definition.Id, path, issues))
				continue;

			if (!result.TryAdd(definition.Id!, definition))
			{
				issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
					$"Duplicate {typeof(TDefinition).Name} '{definition.Id}'."));
			}
		}

		return result;
	}

	private static Dictionary<string, IConfigDefinition> BuildConfigRegistry(
		IEnumerable<IConfigDefinition> definitions, List<ConformanceIssue> issues)
	{
		var result = new Dictionary<string, IConfigDefinition>(StringComparer.Ordinal);
		foreach (var definition in definitions)
		{
			var path = $"configs.{definition.Id ?? "<null>"}";
			if (!ValidateId(definition.Id, path, issues))
				continue;

			if (!result.TryAdd(definition.Id!, definition))
			{
				issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
					$"Duplicate configuration '{definition.Id}'."));
				continue;
			}

			try
			{
				var validation = definition.ValidateDefault();
				if (validation.Failed)
				{
					issues.Add(new ConformanceIssue(ErrorCode.ConfigurationInvalid, path,
						validation.Error!.Message));
				}
			}
			catch (Exception exception)
			{
				issues.Add(new ConformanceIssue(ErrorCode.ConfigurationInvalid, path,
					$"Configuration validation threw {exception.GetType().Name}."));
			}
		}

		return result;
	}

	private static void ValidateCharacterFields(
		IReadOnlyDictionary<string, CharacterFieldDefinition> fields,
		List<ConformanceIssue> issues)
	{
		foreach (var field in fields.Values)
		{
			var path = $"character_fields.{field.Id}";
			if (!Enum.IsDefined(field.ValueKind))
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, path,
					$"Character field '{field.Id}' has unsupported value kind '{field.ValueKind}'."));
				continue;
			}

			if (field.DefaultValue is not null && !DefaultMatches(field.ValueKind, field.DefaultValue))
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, path,
					$"Default value for '{field.Id}' does not match {field.ValueKind}."));
			}
		}
	}

	private static bool DefaultMatches(CharacterFieldValueKind kind, object value)
	{
		return kind switch
		{
			CharacterFieldValueKind.String => value is string,
			CharacterFieldValueKind.Integer => value is int,
			CharacterFieldValueKind.Boolean => value is bool,
			CharacterFieldValueKind.Choice => value is string,
			_ => false
		};
	}

	private static void ValidateFactions(
		IReadOnlyDictionary<string, KernelFactionDefinition> factions,
		IReadOnlyDictionary<string, KernelClassDefinition> classes,
		List<ConformanceIssue> issues)
	{
		var defaultFactions = factions.Values.Where(x => x.IsDefault).Select(x => x.Id).ToArray();
		if (defaultFactions.Length > 1)
		{
			issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, "factions",
				$"Only one default faction is allowed; found {string.Join(", ", defaultFactions)}."));
		}

		foreach (var @class in classes.Values)
		{
			if (!ValidateId(@class.FactionId, $"classes.{@class.Id}.faction", issues))
			{
				// The invalid identifier is already a complete conformance issue.
			}
			else if (!factions.ContainsKey(@class.FactionId))
			{
				issues.Add(new ConformanceIssue(ErrorCode.UnknownDefinition, $"classes.{@class.Id}",
					$"Class '{@class.Id}' references unknown faction '{@class.FactionId}'."));
			}

			if (@class.Capacity is <= 0)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, $"classes.{@class.Id}",
					$"Class '{@class.Id}' capacity must be positive when specified."));
			}
		}

		foreach (var faction in factions.Values.Where(x => x.DefaultClassId is not null))
		{
			if (!ValidateId(faction.DefaultClassId, $"factions.{faction.Id}.default_class", issues))
			{
				continue;
			}

			if (!classes.TryGetValue(faction.DefaultClassId!, out var defaultClass))
			{
				issues.Add(new ConformanceIssue(ErrorCode.UnknownDefinition, $"factions.{faction.Id}",
					$"Faction '{faction.Id}' references unknown default class '{faction.DefaultClassId}'."));
			}
			else if (defaultClass.FactionId != faction.Id)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, $"factions.{faction.Id}",
					$"Default class '{defaultClass.Id}' belongs to faction '{defaultClass.FactionId}'."));
			}
		}
	}

	private static void ValidateItems(
		IReadOnlyDictionary<string, KernelItemDefinition> items,
		IReadOnlyDictionary<string, ActionDefinition> actions,
		List<ConformanceIssue> issues)
	{
		foreach (var item in items.Values)
		{
			var path = $"items.{item.Id}";
			if (item.ActionIds is null)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, path,
					$"Item '{item.Id}' has a null action list."));
				continue;
			}

			foreach (var actionId in item.ActionIds.Distinct(StringComparer.Ordinal))
			{
				if (!ValidateId(actionId, $"{path}.actions", issues))
					continue;

				if (!actions.ContainsKey(actionId))
				{
					issues.Add(new ConformanceIssue(ErrorCode.UnknownDefinition, path,
						$"Item '{item.Id}' references unknown action '{actionId}'."));
				}
			}

			if (item.CanDrop && string.IsNullOrWhiteSpace(item.WorldModel))
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, path,
					$"Droppable item '{item.Id}' must declare a world model."));
			}

			if (item.Width <= 0 || item.Height <= 0)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid, path,
					$"Item '{item.Id}' dimensions must be positive."));
			}
		}
	}

	private static void ValidatePermissionReferences(
		IReadOnlyDictionary<string, ChatChannelDefinition> channels,
		IReadOnlyDictionary<string, CommandDefinition> commands,
		IReadOnlyDictionary<string, PermissionDefinition> permissions,
		List<ConformanceIssue> issues)
	{
		foreach ( var command in commands.Values )
		{
			if ( !Enum.IsDefined( command.Cost ) )
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid,
					$"commands.{command.Id}.cost",
					$"Command '{command.Id}' declares an invalid admission cost."));
			}
		}

		foreach (var channel in channels.Values.Where(x => x.PermissionId is not null))
		{
			if (!ValidateId(channel.PermissionId, $"chat_channels.{channel.Id}.permission", issues))
				continue;

			if (!permissions.ContainsKey(channel.PermissionId!))
			{
				issues.Add(new ConformanceIssue(ErrorCode.UnknownDefinition,
					$"chat_channels.{channel.Id}",
					$"Chat channel '{channel.Id}' references unknown permission '{channel.PermissionId}'."));
			}
		}

		foreach (var command in commands.Values.Where(x => x.PermissionId is not null))
		{
			if (!ValidateId(command.PermissionId, $"commands.{command.Id}.permission", issues))
				continue;

			if (!permissions.ContainsKey(command.PermissionId!))
			{
				issues.Add(new ConformanceIssue(ErrorCode.UnknownDefinition,
					$"commands.{command.Id}",
					$"Command '{command.Id}' references unknown permission '{command.PermissionId}'."));
			}
		}
	}

	private static void ValidateConcreteTypes(IEnumerable<(string Id, Type Type)> registrations,
		string category, List<ConformanceIssue> issues)
	{
		foreach (var registration in registrations)
		{
			if (registration.Type is null || !registration.Type.IsClass || registration.Type.IsAbstract ||
				registration.Type.ContainsGenericParameters)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid,
					$"{category}.{registration.Id}",
					$"Registration '{registration.Id}' requires a closed concrete type."));
			}
		}
	}

	private static void ValidatePersistedTypes(
		IReadOnlyDictionary<string, PersistedTypeRegistration> registrations,
		List<ConformanceIssue> issues)
	{
		var byType = new Dictionary<Type, string>();
		foreach (var registration in registrations.Values)
		{
			var path = $"persisted_types.{registration.Id}";
			if (registration.Version < 1)
			{
				issues.Add(new ConformanceIssue(ErrorCode.PersistedTypeInvalid, path,
					$"Persisted type '{registration.Id}' must have a positive version."));
			}

			if (registration.ClrType is null || !registration.ClrType.IsClass ||
				registration.ClrType.IsAbstract || registration.ClrType.ContainsGenericParameters)
			{
				issues.Add(new ConformanceIssue(ErrorCode.PersistedTypeInvalid, path,
					$"Persisted type '{registration.Id}' requires a closed concrete CLR type."));
				continue;
			}

			if (!byType.TryAdd(registration.ClrType, registration.Id))
			{
				issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
					$"CLR type '{registration.ClrType.FullName}' is already registered as '{byType[registration.ClrType]}'."));
			}
		}
	}

	private static void ValidatePolicies(IReadOnlyList<IPolicyRegistration> registrations,
		List<ConformanceIssue> issues)
	{
		foreach (var group in registrations.GroupBy(x => x.ContextType)
			.OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var registration in group)
			{
				var path = $"policies.{group.Key.FullName}.{registration.Id ?? "<null>"}";
				if (!ValidateId(registration.Id, path, issues))
					continue;

				if (!ids.Add(registration.Id!))
				{
					issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
						$"Policy '{registration.Id}' is registered twice for {group.Key.FullName}."));
				}
			}

			var builtIns = group.Count(x => x.IsBuiltIn);
			if (builtIns != 1)
			{
				issues.Add(new ConformanceIssue(ErrorCode.SchemaInvalid,
					$"policies.{group.Key.FullName}",
					$"Exactly one built-in policy is required for {group.Key.FullName}; found {builtIns}."));
			}
		}
	}

	private static void ValidateEvents(IReadOnlyList<IEventRegistration> registrations,
		List<ConformanceIssue> issues)
	{
		foreach (var group in registrations.GroupBy(x => x.EventType)
			.OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var registration in group)
			{
				var path = $"events.{group.Key.FullName}.{registration.Id ?? "<null>"}";
				if (!ValidateId(registration.Id, path, issues))
					continue;

				if (!ids.Add(registration.Id!))
				{
					issues.Add(new ConformanceIssue(ErrorCode.DuplicateRegistration, path,
						$"Event handler '{registration.Id}' is registered twice for {group.Key.FullName}."));
				}
			}
		}
	}

	private static bool ValidateId(string? id, string path, List<ConformanceIssue> issues)
	{
		if (KernelIdentifier.IsValid(id))
			return true;

		issues.Add(new ConformanceIssue(ErrorCode.InvalidIdentifier, path,
			$"'{id ?? "<null>"}' is not a stable lowercase identifier."));
		return false;
	}

	private sealed record BuildResult(CompiledSchema? Schema, List<ConformanceIssue> Issues);
}
