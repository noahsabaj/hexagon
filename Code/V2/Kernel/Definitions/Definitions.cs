#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hexagon.V2.Kernel.Definitions;

public interface IDefinition
{
	string Id { get; }
}

public enum CharacterFieldValueKind
{
	String = 0,
	Integer = 1,
	Boolean = 2,
	Choice = 3
}

public sealed record CharacterFieldDefinition(
	string Id,
	CharacterFieldValueKind ValueKind,
	bool ShowInCreation = false,
	bool Required = false,
	object? DefaultValue = null
) : IDefinition;

public sealed record FactionDefinition(
	string Id,
	bool IsDefault = false,
	string? DefaultClassId = null,
	string? DisplayName = null,
	string? Description = null
) : IDefinition;

public sealed record ClassDefinition(
	string Id,
	string FactionId,
	string? DisplayName = null,
	int? Capacity = null
) : IDefinition;

public sealed record ActionDefinition(
	string Id
) : IDefinition;

public sealed record ItemDefinition(
	string Id,
	IReadOnlyList<string> ActionIds,
	bool CanDrop = false,
	string? WorldModel = null,
	string? DisplayName = null,
	string? Description = null,
	string? Category = null,
	int Width = 1,
	int Height = 1
) : IDefinition
{
	public static ItemDefinition Create(string id, bool canDrop = false, string? worldModel = null,
		params string[] actionIds)
		=> new(id, new ReadOnlyCollection<string>(actionIds ?? Array.Empty<string>()), canDrop, worldModel);
}

public sealed record PermissionDefinition(
	string Id
) : IDefinition;

/// <summary>
/// Everything that is true of a chat channel, in one place.
/// <para>
/// This used to be only <c>(Id, PermissionId)</c>, with a game holding a PARALLEL table of ranges
/// keyed by the same ids — so "what is the yell channel" had two answers in two repositories and
/// nothing kept them agreeing. Range, the typed prefixes, the display name, the colour and whether
/// the dead may speak are all properties OF the channel, so they live on its definition.
/// </para>
/// <para>
/// <c>Prefixes</c> is what a player types to address the channel, without the leading slash.
/// Resolved BEFORE the command catalogue, so a prefix colliding with a command name would shadow
/// it — a guard test forbids that rather than leaving it to review.
/// </para>
/// <para>
/// <c>Range</c> is the audible radius in world units, or null for a channel that is not positional.
/// A ranged channel with no range would silently reach nobody, so the schema compiler catches it.
/// </para>
/// <para>
/// <c>AllowedWhileDead</c> defaults to false: death silences. It is per-channel because "OOC works
/// while dead" is a common house rule, and a game should not have to fork the framework to have it.
/// </para>
/// </summary>
public sealed record ChatChannelDefinition(
	string Id,
	string? PermissionId = null,
	string? DisplayName = null,
	IReadOnlyList<string>? Prefixes = null,
	float? Range = null,
	string? Colour = null,
	bool AllowedWhileDead = false
) : IDefinition;

public enum CommandCostClass
{
	Cheap = 1,
	Standard = 2,
	Expensive = 4
}

public sealed record CommandDefinition(
	string Id,
	string? PermissionId = null,
	CommandCostClass Cost = CommandCostClass.Standard
) : IDefinition;

public sealed record PanelDefinition(
	string Id,
	Type PanelType
) : IDefinition;

/// <summary>
/// Metadata registration for an application-layer initializer. The concrete
/// initializer contract belongs to the character application layer.
/// </summary>
public sealed record CharacterInitializerDefinition(
	string Id,
	Type InitializerType,
	int Order = 0
) : IDefinition;

/// <summary>
/// Immutable, fail-closed registry exposed by a compiled schema.
/// </summary>
public sealed class DefinitionRegistry<TDefinition> where TDefinition : class, IDefinition
{
	private readonly IReadOnlyDictionary<string, TDefinition> _definitions;
	private readonly IReadOnlyList<TDefinition> _all;

	internal DefinitionRegistry(IDictionary<string, TDefinition> definitions)
	{
		_definitions = new ReadOnlyDictionary<string, TDefinition>(
			new Dictionary<string, TDefinition>(definitions, StringComparer.Ordinal));
		_all = Array.AsReadOnly(_definitions.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
	}

	public int Count => _definitions.Count;
	public IEnumerable<TDefinition> All => _all;

	public bool Contains(string id) => id is not null && _definitions.ContainsKey(id);

	public bool TryGet(string id, out TDefinition? definition)
	{
		if (id is null)
		{
			definition = null;
			return false;
		}

		return _definitions.TryGetValue(id, out definition);
	}

	public OperationResult<TDefinition> Require(string id)
	{
		if (TryGet(id, out var definition))
			return OperationResult<TDefinition>.Success(definition!);

		return OperationResult<TDefinition>.Failure(
			ErrorCode.UnknownDefinition,
			$"Unknown {typeof(TDefinition).Name} '{id ?? "<null>"}'.");
	}
}
