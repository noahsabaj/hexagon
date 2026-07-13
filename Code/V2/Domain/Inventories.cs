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

public readonly record struct InventoryGridPosition
{
	[JsonConstructor]
	public InventoryGridPosition( int x, int y )
	{
		if ( x < 0 ) throw new ArgumentOutOfRangeException( nameof(x) );
		if ( y < 0 ) throw new ArgumentOutOfRangeException( nameof(y) );
		X = x;
		Y = y;
	}

	public int X { get; }
	public int Y { get; }
}

public readonly record struct InventoryGridSize
{
	[JsonConstructor]
	public InventoryGridSize( int width, int height )
	{
		if ( width <= 0 ) throw new ArgumentOutOfRangeException( nameof(width) );
		if ( height <= 0 ) throw new ArgumentOutOfRangeException( nameof(height) );
		Width = width;
		Height = height;
	}

	public int Width { get; }
	public int Height { get; }
}

/// <summary>
/// Checked grid rectangle. Endpoints are calculated in 64-bit arithmetic so
/// hostile coordinates can never wrap back into a valid inventory region.
/// </summary>
public readonly record struct InventoryRectangle
{
	public InventoryRectangle( InventoryGridPosition position, InventoryGridSize size )
	{
		Position = position;
		Size = size;
		Right = (long)position.X + size.Width;
		Bottom = (long)position.Y + size.Height;
	}

	public InventoryGridPosition Position { get; }
	public InventoryGridSize Size { get; }
	public long Left => Position.X;
	public long Top => Position.Y;
	public long Right { get; }
	public long Bottom { get; }

	public bool FitsWithin( InventoryGridSize bounds ) =>
		Right <= bounds.Width && Bottom <= bounds.Height;

	public bool Overlaps( InventoryRectangle other ) =>
		Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

public static class InventoryGeometry
{
	public static bool TryCreateRectangle(
		int x,
		int y,
		int width,
		int height,
		out InventoryRectangle rectangle )
	{
		if ( x < 0 || y < 0 || width <= 0 || height <= 0 )
		{
			rectangle = default;
			return false;
		}

		rectangle = new InventoryRectangle(
			new InventoryGridPosition( x, y ),
			new InventoryGridSize( width, height ) );
		return true;
	}

	public static bool Fits(
		InventoryGridSize bounds,
		InventoryGridPosition position,
		InventoryGridSize size ) =>
		new InventoryRectangle( position, size ).FitsWithin( bounds );
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
	public InventoryGridPosition Position { get; }
	public int X => Position.X;
	public int Y => Position.Y;

	public InventoryPlacement( ItemId itemId, int x, int y )
		: this( itemId, new InventoryGridPosition( x, y ) )
	{
	}

	[JsonConstructor]
	public InventoryPlacement( ItemId itemId, InventoryGridPosition position )
	{
		ItemId = itemId;
		Position = position;
	}
}

public sealed record InventoryRecord
{
	public required InventoryId Id { get; init; }
	public required InventoryOwner Owner { get; init; }
	public required int Width { get; init; }
	public required int Height { get; init; }
	[JsonIgnore]
	public InventoryGridSize GridSize => new( Width, Height );
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
