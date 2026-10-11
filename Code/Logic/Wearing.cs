#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// Clothes are items. What a character wears stays in its inventory, marked worn, and takes no room
/// in the grid; it is what others see of it. Putting something on or taking it off changes no hands,
/// so it is not a transfer. Whether two things can be worn together is the engine's to say, so the
/// caller asks it before putting anything on. Something worn leaves its wearer only by being taken
/// off them by someone else, as from the down, the bound or the dead, and then it arrives carried.
/// </summary>
public static class Wearing
{
	public const string TakeOffFirst = "Take it off first.";

	/// <summary>What is worn, in the order it was put on.</summary>
	public static IEnumerable<ItemStack> Worn( InventoryData inventory ) => inventory.Items.Where( value => value.Worn );

	/// <summary>What is carried: everything in the grid.</summary>
	public static IEnumerable<ItemStack> Carried( InventoryData inventory ) => inventory.Items.Where( value => !value.Worn );

	/// <summary>Puts on something carried. Its cells are free from then on.</summary>
	public static Result PutOn( InventoryData inventory, Guid itemId )
	{
		var item = inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( item.Worn ) return Result.Fail( ErrorCode.Conflict, "You are already wearing it." );
		if ( item.Count > 1 ) return Result.Fail( ErrorCode.Conflict, "Only one of a thing is worn at a time." );
		// Put on last, so it is the outermost: the order is kept, and the look follows it.
		inventory.Items.Remove( item );
		item.Worn = true;
		inventory.Items.Add( item );
		return Result.Success();
	}

	/// <summary>Takes off something worn, into the first free cell. With nowhere to put it, it stays on.</summary>
	public static Result TakeOff( InventoryData inventory, Guid itemId )
	{
		var item = inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( !item.Worn ) return Result.Fail( ErrorCode.Conflict, "You are not wearing it." );
		for ( var y = 0; y < inventory.Height; y++ )
		for ( var x = 0; x < inventory.Width; x++ )
		{
			if ( !InventoryGrid.Fits( inventory, x, y, item.Width, item.Height, ignore: item.Id ) ) continue;
			item.Worn = false;
			item.X = x;
			item.Y = y;
			return Result.Success();
		}
		return Result.Fail( ErrorCode.Conflict, "You have no room to take it off." );
	}
}
