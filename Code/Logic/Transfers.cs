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
	public const string Wage = "wage";
}

/// <summary>Where items and tokens may leave the world.</summary>
public static class Sinks
{
	public const string Discard = "discard";
	/// <summary>Eaten, drunk, fired, used up.</summary>
	public const string Consumed = "consumed";
	public const string CharacterDeleted = "character.deleted";
	/// <summary>Paid to the city for property, not to anyone in it.</summary>
	public const string Property = "property";
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

	/// <summary>Identical things pile up: <paramref name="max"/> to a slot, as the item's asset says. All of them arrive or none do.</summary>
	public Result<ItemStack> Issue( string source, IHolder to, string definition, int width, int height, Actor by, int count = 1, int max = 1 )
	{
		var placed = count < 1
			? Result<ItemStack>.Fail( ErrorCode.Invalid, "The count must be positive." )
			: Add( to.Inventory, new ItemStack { Id = Guid.NewGuid(), Definition = definition, Width = width, Height = height, Max = Math.Max( 1, max ) }, count );
		_journal.Record( "item.issue", by, to.HolderLabel, placed.Ok,
			data: new[] { ("source", source), ("item", placed.Ok ? placed.Value.Id.ToString( "N" ) : string.Empty), ("definition", definition), ("count", count.ToString()) } );
		return placed;
	}

	/// <summary>
	/// A whole pile that lands in a slot of its own keeps its id, so the journal can follow one
	/// object through every hand. Part of a pile, or one that joins another, is followed by count.
	/// </summary>
	public Result Move( IHolder from, IHolder to, Guid itemId, Actor by, int count = 0 )
	{
		if ( ReferenceEquals( from, to ) ) return Result.Fail( ErrorCode.Invalid, "That is already there." );
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count == 0 ) count = item.Count;
		if ( count < 1 || count > item.Count ) return Result.Fail( ErrorCode.Invalid, "There are not that many." );
		// Place first: if it does not all fit, nothing has changed.
		var placed = Add( to.Inventory, Like( item, count == item.Count ? item.Id : Guid.NewGuid() ), count );
		if ( placed.Ok ) Take( from.Inventory, item, count );
		_journal.Record( "item.move", by, to.HolderLabel, placed.Ok,
			data: new[] { ("from", from.HolderLabel), ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()),
				("into", placed.Ok ? placed.Value.Id.ToString( "N" ) : string.Empty) } );
		return placed.ToResult();
	}

	public Result Destroy( string sink, IHolder from, Guid itemId, Actor by, int count = 0 )
	{
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count == 0 ) count = item.Count;
		if ( count < 1 || count > item.Count ) return Result.Fail( ErrorCode.Invalid, "There are not that many." );
		Take( from.Inventory, item, count );
		_journal.Record( "item.destroy", by, from.HolderLabel,
			data: new[] { ("sink", sink), ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()) } );
		return Result.Success();
	}

	/// <summary>Part of a pile becomes a pile of its own, in the same hands. Nothing enters or leaves, but it is on record.</summary>
	public Result<ItemStack> Split( IHolder holder, Guid itemId, int count, Actor by )
	{
		var item = holder.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result<ItemStack>.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count < 1 || count >= item.Count ) return Result<ItemStack>.Fail( ErrorCode.Invalid, "That is not part of it." );
		var part = Like( item, Guid.NewGuid() );
		part.Count = count;
		var placed = Place( holder.Inventory, part );
		if ( placed.Ok ) item.Count -= count;
		_journal.Record( "item.split", by, holder.HolderLabel, placed.Ok,
			data: new[] { ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()), ("into", part.Id.ToString( "N" )) } );
		return placed;
	}

	/// <summary>One pile is put on another of the same thing, as far as it will go.</summary>
	public Result Merge( IHolder holder, Guid itemId, Guid intoId, Actor by )
	{
		var item = holder.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		var into = holder.Inventory.Items.FirstOrDefault( value => value.Id == intoId );
		if ( item is null || into is null || item == into ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		var count = item.Definition == into.Definition ? Math.Min( item.Count, into.Max - into.Count ) : 0;
		if ( count < 1 ) return Result.Fail( ErrorCode.Conflict, "Those do not go together." );
		into.Count += count;
		Take( holder.Inventory, item, count );
		_journal.Record( "item.merge", by, holder.HolderLabel,
			data: new[] { ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()), ("into", intoId.ToString( "N" )) } );
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

	private static ItemStack Like( ItemStack item, Guid id ) =>
		new() { Id = id, Definition = item.Definition, Width = item.Width, Height = item.Height, Max = item.Max, Frequency = item.Frequency };

	private static void Take( InventoryData inventory, ItemStack item, int count )
	{
		item.Count -= count;
		if ( item.Count <= 0 ) inventory.Items.Remove( item );
	}

	/// <summary>
	/// Tops up piles of the same thing, then starts new ones, the first of which takes the
	/// template's id. Returns the last pile that received any. If it does not all fit, nothing changes.
	/// </summary>
	private static Result<ItemStack> Add( InventoryData inventory, ItemStack template, int count )
	{
		var topped = new System.Collections.Generic.List<(ItemStack Pile, int Was)>();
		var started = new System.Collections.Generic.List<ItemStack>();
		ItemStack? last = null;
		foreach ( var pile in inventory.Items.Where( value => value.Definition == template.Definition && value.Count < value.Max ) )
		{
			if ( count == 0 ) break;
			var room = Math.Min( count, pile.Max - pile.Count );
			topped.Add( (pile, pile.Count) );
			pile.Count += room;
			count -= room;
			last = pile;
		}
		while ( count > 0 )
		{
			var pile = Like( template, started.Count == 0 ? template.Id : Guid.NewGuid() );
			pile.Count = Math.Min( count, pile.Max );
			if ( !Place( inventory, pile ).Ok )
			{
				foreach ( var (was, amount) in topped ) was.Count = amount;
				foreach ( var added in started ) inventory.Items.Remove( added );
				return Result<ItemStack>.Fail( ErrorCode.Conflict, "There is no room for that." );
			}
			started.Add( pile );
			count -= pile.Count;
			last = pile;
		}
		return Result<ItemStack>.Success( last! );
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
