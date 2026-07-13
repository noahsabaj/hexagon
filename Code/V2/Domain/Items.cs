#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Domain;

public sealed record ItemRecord
{
	public required ItemId Id { get; init; }
	public required DefinitionId Definition { get; init; }
	public IReadOnlyDictionary<string, TypedPayload> Traits { get; init; } = new Dictionary<string, TypedPayload>();
	public long Revision { get; init; }

	public ItemRecord DeepCopy() => this with
	{
		Traits = Traits.ToDictionary( pair => pair.Key, pair => pair.Value.DeepCopy(), StringComparer.Ordinal )
	};
}

public sealed record WorldTransformRecord
{
	public required float PositionX { get; init; }
	public required float PositionY { get; init; }
	public required float PositionZ { get; init; }
	public required float RotationX { get; init; }
	public required float RotationY { get; init; }
	public required float RotationZ { get; init; }
	public required float RotationW { get; init; }
}

public sealed record WorldItemRecord
{
	public required ItemId ItemId { get; init; }
	public required WorldTransformRecord Transform { get; init; }
	public long Revision { get; init; }
}
