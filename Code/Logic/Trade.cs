#nullable enable

using System;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// A sale is two transfers that happen together or not at all: the item one way, the tokens the
/// other. Nothing is created: a vendor's goods came from its declared restock source, and what a
/// vendor pays out it must first have taken in or been floated.
/// </summary>
public static class Trade
{
	public static long Price( long value, float factor ) => Math.Max( 0, (long)Math.Ceiling( value * (double)factor ) );

	public static Result Sell( Transfers transfers, IHolder seller, IHolder buyer, Guid itemId, long price, Actor by )
	{
		if ( price < 0 ) return Result.Fail( ErrorCode.Invalid, "That is not a price." );
		if ( seller.Inventory.Items.All( value => value.Id != itemId ) ) return Result.Fail( ErrorCode.NotFound, "That is not for sale." );
		if ( buyer.Tokens < price ) return Result.Fail( ErrorCode.Conflict, $"That costs {price} tokens." );
		// The item goes first because it is the half that can still fail, for want of room.
		var moved = transfers.Move( seller, buyer, itemId, by );
		if ( !moved.Ok ) return moved;
		return price == 0 ? moved : transfers.MoveTokens( buyer, seller, price, by );
	}
}
