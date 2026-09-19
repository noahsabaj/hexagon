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
	// Copied on the way in. The list a game hands over is usually a List<string> it still
	// holds; without the copy, mutating it after Compile would silently rewrite the registry.
	private readonly IReadOnlyList<string> _actionIds = DefinitionLists.Freeze(ActionIds)!;

	public IReadOnlyList<string> ActionIds
	{
		get => _actionIds;
		init => _actionIds = DefinitionLists.Freeze(value)!;
	}

	public static ItemDefinition Create(string id, bool canDrop = false, string? worldModel = null,
		params string[] actionIds)
		=> new(id, actionIds ?? Array.Empty<string>(), canDrop, worldModel);
}

internal static class DefinitionLists
{
	/// <summary>
	/// An immutable snapshot of <paramref name="values"/>, or null for null so the compiler can
	/// still report a missing list as a conformance issue rather than a constructor exception.
	/// </summary>
	public static IReadOnlyList<string>? Freeze(IReadOnlyList<string>? values) =>
		values is null ? null : Array.AsReadOnly(values.ToArray());
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
/// The client composer resolves prefixes BEFORE the command catalogue and compares them
/// case-insensitively, so <see cref="Schema.SchemaCompiler"/> rejects a prefix that is empty,
/// contains whitespace, repeats another channel's prefix (ignoring case) or equals a registered
/// command id (ignoring case). A collision with a command not registered through the schema
/// cannot be seen here and stays the game's responsibility.
/// </para>
/// <para>
/// <c>Range</c> is the audible radius in world units, or null for a channel that is not positional.
/// Giving a channel a range is what makes it positional; the compiler rejects a range that is not
/// finite and positive, and <c>ChatService</c> hands recipient resolvers this value and no other.
/// </para>
/// <para>
/// <c>AllowedWhileDead</c> defaults to false: death silences. It is per-channel because "OOC works
/// while dead" is a common house rule, and a game should not have to fork the framework to have it.
/// <c>ChatService</c> enforces it on the host; the client composer only mirrors it for feedback.
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
) : IDefinition
{
	private readonly IReadOnlyList<string>? _prefixes = DefinitionLists.Freeze(Prefixes);

	public IReadOnlyList<string>? Prefixes
	{
		get => _prefixes;
		init => _prefixes = DefinitionLists.Freeze(value);
	}
}

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
