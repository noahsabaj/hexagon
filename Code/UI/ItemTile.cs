#nullable enable

using System;
using Hexagon.Logic;
using Sandbox.UI;
using Sandbox.UI.Construct;

namespace Hexagon.UI;

/// <summary>
/// A pile in a grid: what it is, what tells two of it apart, and how many. The mouse can pick it up
/// and put it down elsewhere: in another cell, on a pile of the same thing, in the other grid, or
/// outside both. The engine's panel input does the dragging; the HUD decides what a drop means and
/// asks the host, as a click would.
/// </summary>
public sealed class ItemTile : Panel
{
	private ItemStack? _item;
	private (string?, int, string?, string?) _shown;

	/// <summary>The pile this tile shows.</summary>
	public ItemStack? Item
	{
		get => _item;
		set
		{
			_item = value;
			var shown = (value?.Definition, value?.Count ?? 0, value?.Frequency, value?.Pattern);
			if ( shown == _shown && ChildrenCount > 0 ) return;
			_shown = shown;
			Show();
		}
	}

	/// <summary>The pile's id, or empty for none.</summary>
	public Guid ItemId => _item?.Id ?? Guid.Empty;

	/// <summary>True for a pile in the open holder, false for one this character carries.</summary>
	public bool InOpenHolder { get; set; }

	public override bool WantsDrag => true;

	private void Show()
	{
		DeleteChildren( true );
		if ( _item is null ) return;
		var definition = ItemDefinition.Find( _item.Definition );
		Add.Label( definition?.Title ?? "Unknown item", "title" );
		if ( definition is { IsRadio: true } ) Add.Label( _item.Frequency ?? "untuned", "detail" );
		if ( definition is { IsKey: true } ) Add.Label( _item.Pattern ?? "blank", "detail" );
		if ( _item.Count > 1 ) Add.Label( $"×{_item.Count}", "count" );
	}
}
