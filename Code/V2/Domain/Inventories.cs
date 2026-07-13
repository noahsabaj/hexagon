#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hexagon.V2.Domain;

public enum InventoryOwnerKind
{
	Character,
	SceneEntity,
	ParentItem
}

/// <summary>
/// Closed owner union. OwnerId is the GUID value of the corresponding strong ID.
/// </summary>
public readonly record struct InventoryOwner
{
	public InventoryOwnerKind Kind { get; }
	public Guid OwnerId { get; }

	[JsonConstructor]
	public InventoryOwner( InventoryOwnerKind kind, Guid ownerId )
	{
		if ( ownerId == Guid.Empty ) throw new ArgumentOutOfRangeException( nameof(ownerId) );
		Kind = kind;
		OwnerId = ownerId;
	}

	public static InventoryOwner Character( CharacterId id ) => new( InventoryOwnerKind.Character, id.Value );
	public static InventoryOwner SceneEntity( SceneEntityId id ) => new( InventoryOwnerKind.SceneEntity, id.Value );
	public static InventoryOwner ParentItem( ItemId id ) => new( InventoryOwnerKind.ParentItem, id.Value );
}

public readonly record struct InventoryPlacement
{
	public ItemId ItemId { get; }
	public int X { get; }
	public int Y { get; }

	[JsonConstructor]
	public InventoryPlacement( ItemId itemId, int x, int y )
	{
		if ( x < 0 ) throw new ArgumentOutOfRangeException( nameof(x) );
		if ( y < 0 ) throw new ArgumentOutOfRangeException( nameof(y) );
		ItemId = itemId;
		X = x;
		Y = y;
	}
}

public sealed record InventoryRecord
{
	public required InventoryId Id { get; init; }
	public required InventoryOwner Owner { get; init; }
	public required int Width { get; init; }
	public required int Height { get; init; }
	public IReadOnlyList<InventoryPlacement> Placements { get; init; } = Array.Empty<InventoryPlacement>();
	public long Revision { get; init; }

	public InventoryRecord DeepCopy() => this with { Placements = Placements.ToArray() };

	public InventoryPlacement? Find( ItemId itemId )
	{
		foreach ( var placement in Placements )
		{
			if ( placement.ItemId == itemId ) return placement;
		}

		return null;
	}
}
