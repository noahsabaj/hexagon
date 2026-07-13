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

public sealed record ChatChannelDefinition(
	string Id,
	string? PermissionId = null
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
