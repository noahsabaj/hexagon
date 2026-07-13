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
		if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
		if (y < 0) throw new ArgumentOutOfRangeException(nameof(y));
		if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
		if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

		ItemId = itemId;
		DefinitionId = definitionId;
		DisplayName = displayName ?? string.Empty;
		Description = description ?? string.Empty;
		Category = category ?? string.Empty;
		X = x;
		Y = y;
		Width = width;
		Height = height;
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
	public int X { get; }
	public int Y { get; }
	public int Width { get; }
	public int Height { get; }
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
		if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

		InventoryId = inventoryId;
		Revision = revision;
		Kind = kind;
		Title = title ?? string.Empty;
		Width = width;
		Height = height;
		Items = Array.AsReadOnly((items ?? throw new ArgumentNullException(nameof(items)))
			.OrderBy(item => item.Y)
			.ThenBy(item => item.X)
			.ThenBy(item => item.ItemId.Value)
			.ToArray());
	}

	public InventoryId InventoryId { get; }
	public long Revision { get; }
	public InventoryViewKind Kind { get; }
	public string Title { get; }
	public int Width { get; }
	public int Height { get; }
	public IReadOnlyList<InventoryItemSnapshot> Items { get; }
}
