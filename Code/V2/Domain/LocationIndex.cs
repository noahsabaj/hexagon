#nullable enable

using System;
using System.Collections.Generic;

namespace Hexagon.V2.Domain;

public enum ItemLocationKind
{
	Inventory,
	World
}

public readonly record struct ItemLocation( ItemLocationKind Kind, Guid ContainerId );

/// <summary>
/// Derived invariant index ensuring that an item has exactly one authoritative location.
/// </summary>
public sealed class ItemLocationIndex
{
	private readonly Dictionary<ItemId, ItemLocation> _locations = new();

	public IReadOnlyDictionary<ItemId, ItemLocation> Locations => _locations;

	public void Build( IEnumerable<InventoryRecord> inventories, IEnumerable<WorldItemRecord> worldItems )
	{
		_locations.Clear();

		foreach ( var inventory in inventories )
		{
			foreach ( var placement in inventory.Placements )
				AddUnique( placement.ItemId, new ItemLocation( ItemLocationKind.Inventory, inventory.Id.Value ) );
		}

		foreach ( var worldItem in worldItems )
			AddUnique( worldItem.ItemId, new ItemLocation( ItemLocationKind.World, worldItem.ItemId.Value ) );
	}

	public bool TryGet( ItemId itemId, out ItemLocation location ) => _locations.TryGetValue( itemId, out location );

	private void AddUnique( ItemId itemId, ItemLocation location )
	{
		if ( !_locations.TryAdd( itemId, location ) )
			throw new InvalidOperationException( $"Item '{itemId}' has more than one authoritative location." );
	}
}
