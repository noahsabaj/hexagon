#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Application;

public readonly struct ItemShape : IEquatable<ItemShape>
{
	public int Width { get; }
	public int Height { get; }
	public InventoryGridSize GridSize => new( Width, Height );

	public ItemShape( int width, int height )
	{
		if ( width <= 0 ) throw new ArgumentOutOfRangeException( nameof(width) );
		if ( height <= 0 ) throw new ArgumentOutOfRangeException( nameof(height) );
		Width = width;
		Height = height;
	}

	public bool Equals( ItemShape other ) => Width == other.Width && Height == other.Height;
	public override bool Equals( object? obj ) => obj is ItemShape other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Width, Height );
	public static bool operator ==( ItemShape left, ItemShape right ) => left.Equals( right );
	public static bool operator !=( ItemShape left, ItemShape right ) => !left.Equals( right );
}

public interface IItemShapeCatalog
{
	bool TryGetShape( DefinitionId definitionId, out ItemShape shape );
	bool TryGetShape( ItemId itemId, out ItemShape shape );
}

public readonly record struct InventoryTransferResult( InventoryRecord Source, InventoryRecord Target );

/// <summary>
/// Pure inventory aggregate operations. No method resolves items globally: callers
/// must supply the item record already proven to belong to the source inventory.
/// </summary>
public sealed class InventoryLayoutService
{
	private readonly IItemShapeCatalog _catalog;

	public InventoryLayoutService( IItemShapeCatalog catalog ) =>
		_catalog = catalog ?? throw new ArgumentNullException( nameof(catalog) );

	public OperationResult<InventoryRecord> AddAt(
		InventoryRecord inventory,
		ItemRecord item,
		int x,
		int y,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
		=> AddAt( inventory, item, new InventoryGridPosition( x, y ), stagedDefinitions );

	public OperationResult<InventoryRecord> AddAt(
		InventoryRecord inventory,
		ItemRecord item,
		InventoryGridPosition position,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
	{
		if ( inventory.Find( item.Id ) is not null )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.Conflict, "Item is already in the inventory." );

		if ( !_catalog.TryGetShape( item.Definition, out var shape ) )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.UnknownDefinition, $"Unknown item definition '{item.Definition}'." );

		if ( !CanFit( inventory, position, shape, null, stagedDefinitions ) )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.Conflict, "Item does not fit at the requested position." );

		return OperationResult<InventoryRecord>.Success( inventory with
		{
			Placements = inventory.Placements.Append( new InventoryPlacement( item.Id, position ) ).ToArray()
		} );
	}

	public OperationResult<InventoryRecord> Move(
		InventoryRecord inventory,
		ItemRecord item,
		int x,
		int y,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
		=> Move( inventory, item, new InventoryGridPosition( x, y ), stagedDefinitions );

	public OperationResult<InventoryRecord> Move(
		InventoryRecord inventory,
		ItemRecord item,
		InventoryGridPosition position,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
	{
		if ( inventory.Find( item.Id ) is null )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.NotFound, "Item is not a member of the inventory." );

		if ( !_catalog.TryGetShape( item.Definition, out var shape ) )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.UnknownDefinition, $"Unknown item definition '{item.Definition}'." );

		if ( !CanFit( inventory, position, shape, item.Id, stagedDefinitions ) )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.Conflict, "Item does not fit at the requested position." );

		return OperationResult<InventoryRecord>.Success( inventory with
		{
			Placements = inventory.Placements
				.Select( placement => placement.ItemId == item.Id ? new InventoryPlacement( item.Id, position ) : placement )
				.ToArray()
		} );
	}

	public OperationResult<InventoryRecord> Remove( InventoryRecord inventory, ItemId itemId )
	{
		if ( inventory.Find( itemId ) is null )
			return OperationResult<InventoryRecord>.Failure( ErrorCode.NotFound, "Item is not a member of the inventory." );

		return OperationResult<InventoryRecord>.Success( inventory with
		{
			Placements = inventory.Placements.Where( placement => placement.ItemId != itemId ).ToArray()
		} );
	}

	public OperationResult<InventoryTransferResult> Transfer(
		InventoryRecord source,
		InventoryRecord target,
		ItemRecord item,
		int x,
		int y,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null ) =>
		Transfer( source, target, item, new InventoryGridPosition( x, y ), stagedDefinitions );

	public OperationResult<InventoryTransferResult> Transfer(
		InventoryRecord source,
		InventoryRecord target,
		ItemRecord item,
		InventoryGridPosition position,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
	{
		if ( source.Id == target.Id )
		{
			var moved = Move( source, item, position, stagedDefinitions );
			return moved.Succeeded
				? OperationResult<InventoryTransferResult>.Success( new InventoryTransferResult( moved.Value, moved.Value ) )
				: OperationResult<InventoryTransferResult>.Failure( moved.Error!.Code, moved.Error.Message );
		}

		if ( source.Find( item.Id ) is null )
			return OperationResult<InventoryTransferResult>.Failure( ErrorCode.NotFound, "Item is not a member of the source inventory." );

		var added = AddAt( target, item, position, stagedDefinitions );
		if ( added.Failed )
			return OperationResult<InventoryTransferResult>.Failure( added.Error!.Code, added.Error.Message );

		var removed = Remove( source, item.Id );
		if ( removed.Failed )
			return OperationResult<InventoryTransferResult>.Failure( removed.Error!.Code, removed.Error.Message );

		return OperationResult<InventoryTransferResult>.Success( new InventoryTransferResult( removed.Value, added.Value ) );
	}

	public OperationResult<(int X, int Y)> FindFirstFit(
		InventoryRecord inventory,
		ItemRecord item,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions = null )
	{
		if ( !_catalog.TryGetShape( item.Definition, out var shape ) )
			return OperationResult<(int X, int Y)>.Failure( ErrorCode.UnknownDefinition, $"Unknown item definition '{item.Definition}'." );

		for ( var y = 0; y <= inventory.Height - shape.Height; y++ )
		{
			for ( var x = 0; x <= inventory.Width - shape.Width; x++ )
			{
				if ( CanFit( inventory, new InventoryGridPosition( x, y ), shape, null, stagedDefinitions ) )
					return OperationResult<(int X, int Y)>.Success( (x, y) );
			}
		}

		return OperationResult<(int X, int Y)>.Failure( ErrorCode.Conflict, "Inventory has no space for the item." );
	}

	private bool CanFit(
		InventoryRecord inventory,
		InventoryGridPosition position,
		ItemShape shape,
		ItemId? excludedItem,
		IReadOnlyDictionary<ItemId, DefinitionId>? stagedDefinitions )
	{
		if ( !InventoryGeometry.Fits( inventory.GridSize, position, shape.GridSize ) )
			return false;
		var requested = new InventoryRectangle( position, shape.GridSize );

		foreach ( var placement in inventory.Placements )
		{
			if ( excludedItem is not null && placement.ItemId == excludedItem.Value ) continue;
			// A committed inventory cannot contain an item with an unknown definition. The
			// aggregate validator catches this before persistence; fail closed here as well.
			ItemShape placedShape;
			if ( stagedDefinitions is not null && stagedDefinitions.TryGetValue( placement.ItemId, out var stagedDefinition ) )
			{
				if ( !_catalog.TryGetShape( stagedDefinition, out placedShape ) ) return false;
			}
			else if ( !_catalog.TryGetShape( placement.ItemId, out placedShape ) )
			{
				return false;
			}
			var occupied = new InventoryRectangle( placement.Position, placedShape.GridSize );
			if ( requested.Overlaps( occupied ) ) return false;
		}

		return true;
	}

}
