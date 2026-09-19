#nullable enable

using System;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Placement rules for a grid inventory. Items enter and leave an inventory only through
/// <see cref="Transfers"/>, so there is no add or remove here.
/// </summary>
public static class InventoryGrid
{
	public static bool Fits( InventoryData inventory, int x, int y, int width, int height, Guid? ignore = null )
	{
		if ( width < 1 || height < 1 || x < 0 || y < 0 ) return false;
		if ( x + width > inventory.Width || y + height > inventory.Height ) return false;
		return !inventory.Items.Any( other => other.Id != ignore &&
			x < other.X + other.Width && other.X < x + width &&
			y < other.Y + other.Height && other.Y < y + height );
	}

	/// <summary>Rearranges within one inventory. Nothing changes hands, so nothing is journaled.</summary>
	public static Result Move( InventoryData inventory, Guid itemId, int x, int y )
	{
		var item = inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not in this inventory." );
		if ( !Fits( inventory, x, y, item.Width, item.Height, ignore: itemId ) )
			return Result.Fail( ErrorCode.Conflict, "That space is taken." );
		item.X = x;
		item.Y = y;
		return Result.Success();
	}
}
