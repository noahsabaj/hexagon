#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

public enum InventoryViewKind
{
	Main = 0,
	Bag = 1,
	Storage = 2,
	Vendor = 3,
	Search = 4
}

public enum ItemActionInvocationKind
{
	ContextMenu = 0,
	DedicatedPanel = 1
}

public sealed record ItemActionSnapshot(
	ActionId ActionId,
	string Label,
	bool Enabled,
	string? DisabledReason = null,
	ItemActionInvocationKind Invocation = ItemActionInvocationKind.ContextMenu
);

public sealed record InventoryItemSnapshot
{
	public InventoryItemSnapshot(
		ItemId itemId,
		DefinitionId definitionId,
		string displayName,
		string description,
		string category,
		int x,
		int y,
		int width,
		int height,
		long quantity,
		IEnumerable<ItemActionSnapshot>? actions = null,
		IReadOnlyDictionary<string, SnapshotValue>? state = null,
		bool canDrop = false,
		string? dropDisabledReason = null)
	{
		if ( !InventoryGeometry.TryCreateRectangle( x, y, width, height, out var rectangle ) )
			throw new ArgumentOutOfRangeException( nameof(x), "Inventory item geometry is invalid." );
		if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

		ItemId = itemId;
		DefinitionId = definitionId;
		DisplayName = displayName ?? string.Empty;
		Description = description ?? string.Empty;
		Category = category ?? string.Empty;
		Position = rectangle.Position;
		Size = rectangle.Size;
		Quantity = quantity;
		Actions = Array.AsReadOnly((actions ?? Array.Empty<ItemActionSnapshot>()).ToArray());
		State = PresentationSnapshotMap.Copy(state);
		CanDrop = canDrop;
		DropDisabledReason = canDrop ? null : dropDisabledReason;
	}

	public ItemId ItemId { get; }
	public DefinitionId DefinitionId { get; }
	public string DisplayName { get; }
	public string Description { get; }
	public string Category { get; }
	public InventoryGridPosition Position { get; }
	public InventoryGridSize Size { get; }
	public int X => Position.X;
	public int Y => Position.Y;
	public int Width => Size.Width;
	public int Height => Size.Height;
	public long Quantity { get; }
	public IReadOnlyList<ItemActionSnapshot> Actions { get; }
	public IReadOnlyDictionary<string, SnapshotValue> State { get; }
	public bool CanDrop { get; }
	public string? DropDisabledReason { get; }
}

public sealed record InventorySnapshot
{
	public InventorySnapshot(
		InventoryId inventoryId,
		long revision,
		InventoryViewKind kind,
		string title,
		int width,
		int height,
		IEnumerable<InventoryItemSnapshot> items)
	{
		if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
		var gridSize = new InventoryGridSize( width, height );
		var copiedItems = (items ?? throw new ArgumentNullException(nameof(items))).ToArray();
		for ( var index = 0; index < copiedItems.Length; index++ )
		{
			var item = copiedItems[index];
			if ( !new InventoryRectangle( item.Position, item.Size ).FitsWithin( gridSize ) )
				throw new ArgumentException( "Inventory item lies outside the inventory bounds.", nameof(items) );
			var rectangle = new InventoryRectangle( item.Position, item.Size );
			for ( var otherIndex = 0; otherIndex < index; otherIndex++ )
			{
				var other = copiedItems[otherIndex];
				if ( rectangle.Overlaps( new InventoryRectangle( other.Position, other.Size ) ) )
					throw new ArgumentException( "Inventory items overlap.", nameof(items) );
			}
		}

		InventoryId = inventoryId;
		Revision = revision;
		Kind = kind;
		Title = title ?? string.Empty;
		Size = gridSize;
		Items = Array.AsReadOnly(copiedItems
			.OrderBy(item => item.Y)
			.ThenBy(item => item.X)
			.ThenBy(item => item.ItemId.Value)
			.ToArray());
	}

	public InventoryId InventoryId { get; }
	public long Revision { get; }
	public InventoryViewKind Kind { get; }
	public string Title { get; }
	public InventoryGridSize Size { get; }
	public int Width => Size.Width;
	public int Height => Size.Height;
	public IReadOnlyList<InventoryItemSnapshot> Items { get; }
}
