#nullable enable

using System;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>Anything that can hold items and tokens: a character now; a crate, a vendor or a corpse later.</summary>
public interface IHolder
{
	Guid HolderId { get; }
	/// <summary>How the journal names this holder.</summary>
	string HolderLabel { get; }
	InventoryData Inventory { get; }
	long Tokens { get; set; }
}

/// <summary>Where new items and tokens may come from. A game adds its own names.</summary>
public static class Sources
{
	public const string CharacterStart = "character.start";
	public const string Operator = "operator";
}

/// <summary>Where items and tokens may leave the world.</summary>
public static class Sinks
{
	public const string Discard = "discard";
	/// <summary>Eaten, drunk, fired, used up.</summary>
	public const string Consumed = "consumed";
	public const string CharacterDeleted = "character.deleted";
}

/// <summary>
/// The only way an item or a token changes hands. Things move between holders; they enter the
/// world from a named source and leave it through a named sink, and every one of those is
/// journaled. So the money supply is a sum over the journal, and an item that appears in two
/// places has a recorded path to each.
/// An operation changes the documents in memory. The caller saves them: the giver first, so a
/// crash between the two saves loses a thing the journal can restore rather than duplicating it.
/// </summary>
public sealed class Transfers
{
	private readonly Journal _journal;

	public Transfers( Journal journal ) => _journal = journal ?? throw new ArgumentNullException( nameof(journal) );

	public Result<ItemStack> Issue( string source, IHolder to, string definition, int width, int height, Actor by )
	{
		var placed = Place( to.Inventory, new ItemStack { Id = Guid.NewGuid(), Definition = definition, Width = width, Height = height } );
		_journal.Record( "item.issue", by, to.HolderLabel, placed.Ok,
			data: new[] { ("source", source), ("item", placed.Ok ? placed.Value.Id.ToString( "N" ) : string.Empty), ("definition", definition) } );
		return placed;
	}

	/// <summary>The item keeps its id, so the journal can follow one object through every hand.</summary>
	public Result Move( IHolder from, IHolder to, Guid itemId, Actor by )
	{
		if ( ReferenceEquals( from, to ) ) return Result.Fail( ErrorCode.Invalid, "That is already there." );
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		// Place a copy first: if it does not fit, nothing has changed.
		var placed = Place( to.Inventory, new ItemStack { Id = item.Id, Definition = item.Definition, Width = item.Width, Height = item.Height } );
		if ( placed.Ok ) from.Inventory.Items.Remove( item );
		_journal.Record( "item.move", by, to.HolderLabel, placed.Ok,
			data: new[] { ("from", from.HolderLabel), ("item", itemId.ToString( "N" )), ("definition", item.Definition) } );
		return placed.ToResult();
	}

	public Result Destroy( string sink, IHolder from, Guid itemId, Actor by )
	{
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		from.Inventory.Items.Remove( item );
		_journal.Record( "item.destroy", by, from.HolderLabel,
			data: new[] { ("sink", sink), ("item", itemId.ToString( "N" )), ("definition", item.Definition) } );
		return Result.Success();
	}

	public Result IssueTokens( string source, IHolder to, long amount, Actor by )
	{
		if ( amount <= 0 ) return Result.Fail( ErrorCode.Invalid, "The amount must be positive." );
		to.Tokens = checked(to.Tokens + amount);
		_journal.Record( "tokens.issue", by, to.HolderLabel, data: new[] { ("source", source), ("amount", amount.ToString()) } );
		return Result.Success();
	}

	public Result MoveTokens( IHolder from, IHolder to, long amount, Actor by )
	{
		if ( amount <= 0 || ReferenceEquals( from, to ) ) return Result.Fail( ErrorCode.Invalid, "The amount must be positive." );
		if ( from.Tokens < amount ) return Result.Fail( ErrorCode.Conflict, "There are not enough tokens." );
		from.Tokens -= amount;
		to.Tokens = checked(to.Tokens + amount);
		_journal.Record( "tokens.move", by, to.HolderLabel, data: new[] { ("from", from.HolderLabel), ("amount", amount.ToString()) } );
		return Result.Success();
	}

	public Result DestroyTokens( string sink, IHolder from, long amount, Actor by )
	{
		if ( amount <= 0 ) return Result.Fail( ErrorCode.Invalid, "The amount must be positive." );
		if ( from.Tokens < amount ) return Result.Fail( ErrorCode.Conflict, "There are not enough tokens." );
		from.Tokens -= amount;
		_journal.Record( "tokens.destroy", by, from.HolderLabel, data: new[] { ("sink", sink), ("amount", amount.ToString()) } );
		return Result.Success();
	}

	/// <summary>Everything a holder has leaves the world at once, as when a character is deleted.</summary>
	public void DestroyAll( string sink, IHolder from, Actor by )
	{
		foreach ( var item in from.Inventory.Items.ToArray() ) Destroy( sink, from, item.Id, by );
		if ( from.Tokens > 0 ) DestroyTokens( sink, from, from.Tokens, by );
	}

	/// <summary>Puts the item in the first free cell, scanning rows then columns.</summary>
	private static Result<ItemStack> Place( InventoryData inventory, ItemStack item )
	{
		for ( var y = 0; y < inventory.Height; y++ )
		for ( var x = 0; x < inventory.Width; x++ )
		{
			if ( !InventoryGrid.Fits( inventory, x, y, item.Width, item.Height ) ) continue;
			item.X = x;
			item.Y = y;
			inventory.Items.Add( item );
			return Result<ItemStack>.Success( item );
		}
		return Result<ItemStack>.Fail( ErrorCode.Conflict, "There is no room for that." );
	}
}
