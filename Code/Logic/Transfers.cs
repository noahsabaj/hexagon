#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>Anything that can hold items: a character, a crate, a body, a thing lying on the ground.</summary>
public interface IHolder
{
	Guid HolderId { get; }
	/// <summary>How the journal names this holder.</summary>
	string HolderLabel { get; }
	InventoryData Inventory { get; }
}

/// <summary>Where new items may come from. A game adds its own names.</summary>
public static class Sources
{
	/// <summary>Made by staff: the one exception to nothing coming from nothing, and on the record under their name.</summary>
	public const string Operator = "operator";
	/// <summary>Placed by a scene's author: what the world as built holds, such as a stocked crate. It is placed once and never refills.</summary>
	public const string Scene = "scene";
	/// <summary>Made by someone, from something that was used up making it. See <see cref="Making"/>.</summary>
	public const string Made = "made";
	/// <summary>What a new character arrives wearing, chosen as it is made: the one thing a character has that nobody gave it.</summary>
	public const string Arrival = "arrival";
}

/// <summary>Where items may leave the world.</summary>
public static class Sinks
{
	/// <summary>Eaten, drunk, fired, used up.</summary>
	public const string Consumed = "consumed";
	/// <summary>Used up making something else: a blank struck into a coin.</summary>
	public const string Made = "made";
	/// <summary>Carried by a character deleted before it ever entered the city.</summary>
	public const string CharacterDeleted = "character.deleted";
	/// <summary>Carried by a character its player retired, which leaves for good taking what it carries.</summary>
	public const string CharacterRetired = "character.retired";
	/// <summary>Carried by a character that died in a game that leaves no body.</summary>
	public const string CharacterDied = "character.died";
	/// <summary>Taken out of the world by staff, as when settling a dispute.</summary>
	public const string Operator = "operator";
}

/// <summary>
/// The only way an item changes hands. Things move between holders; they enter the world from a
/// named source and leave it through a named sink, and every one of those is journaled. Money is
/// items like any other, so the supply of anything, money included, is a sum over the journal, and
/// an item that appears in two places has a recorded path to each.
/// An operation changes the documents in memory. The caller saves every holder it touched with one
/// <see cref="DocumentStore.SaveTogether"/>, so a crash keeps all of the change or none of it. No
/// order of separate saves is safe once each side of a change gives something.
/// </summary>
public sealed class Transfers
{
	private readonly Journal _journal;

	public Transfers( Journal journal ) => _journal = journal ?? throw new ArgumentNullException( nameof(journal) );

	/// <summary>
	/// Identical things pile up: <paramref name="max"/> to a slot, as the item's asset says. All of them
	/// arrive or none do. A key is issued already cut to its <paramref name="pattern"/>. One issued
	/// <paramref name="worn"/> arrives on its wearer, one at a time, and takes no room.
	/// </summary>
	public Result<ItemStack> Issue( string source, IHolder to, string definition, int width, int height, Actor by, int count = 1, int max = 1, string? pattern = null, bool worn = false )
	{
		var template = new ItemStack { Id = Guid.NewGuid(), Definition = definition, Width = width, Height = height, Max = Math.Max( 1, max ), Pattern = pattern };
		var placed = count < 1 ? Result<ItemStack>.Fail( ErrorCode.Invalid, "The count must be positive." )
			: worn && count > 1 ? Result<ItemStack>.Fail( ErrorCode.Invalid, "Only one of a thing is worn at a time." )
			: worn ? PutOnArriving( to.Inventory, template )
			: Add( to.Inventory, template, count );
		var data = new[] { ("source", source), ("item", placed.Ok ? placed.Value.Id.ToString( "N" ) : string.Empty), ("definition", definition), ("count", count.ToString()) };
		_journal.Record( JournalKinds.ItemIssue, by, to.HolderLabel, placed.Ok, data: pattern is null ? data : data.Append( ("pattern", pattern) ).ToArray() );
		return placed;
	}

	/// <summary>
	/// A whole pile that lands in a slot of its own keeps its id, so the journal can follow one
	/// object through every hand. Part of a pile, or one that joins another, is followed by count.
	/// Something worn that is taken off its wearer by someone else arrives carried.
	/// </summary>
	public Result Move( IHolder from, IHolder to, Guid itemId, Actor by, int count = 0 )
	{
		if ( ReferenceEquals( from, to ) ) return Result.Fail( ErrorCode.Invalid, "That is already there." );
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count == 0 ) count = item.Count;
		if ( count < 1 || count > item.Count ) return Result.Fail( ErrorCode.Invalid, "There are not that many." );
		// Place first: if it does not all fit, nothing has changed. What goes is the top of the pile.
		var moving = Like( item, count == item.Count ? item.Id : Guid.NewGuid() );
		moving.Made = Making.Top( item, count );
		var placed = Add( to.Inventory, moving, count );
		if ( placed.Ok ) Take( from, item, count );
		_journal.Record( JournalKinds.ItemMove, by, to.HolderLabel, placed.Ok,
			data: new[] { ("from", from.HolderLabel), ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()),
				("into", placed.Ok ? placed.Value.Id.ToString( "N" ) : string.Empty) } );
		return placed.ToResult();
	}

	/// <summary>
	/// Everything one holder has goes to another, as when a body is left where a character fell. Each
	/// pile keeps its place where that place is free, so a holder at least as large as the giver takes
	/// all of it; packing them one at a time into the first free cell does not always fit what fitted
	/// before. A pile whose place is taken is moved like any other. What is worn stays worn: the body
	/// still has it on. Fails, having moved what it could, if anything is left behind.
	/// </summary>
	public Result MoveAll( IHolder from, IHolder to, Actor by )
	{
		if ( ReferenceEquals( from, to ) ) return Result.Fail( ErrorCode.Invalid, "That is already there." );
		var left = 0;
		foreach ( var item in from.Inventory.Items.ToArray() )
		{
			if ( !item.Worn && !InventoryGrid.Fits( to.Inventory, item.X, item.Y, item.Width, item.Height ) )
			{
				if ( !Move( from, to, item.Id, by ).Ok ) left++;
				continue;
			}
			var kept = Like( item, item.Id );
			kept.Count = item.Count;
			kept.Made = item.Made?.Select( mark => mark.Copy() ).ToList();
			kept.X = item.X;
			kept.Y = item.Y;
			kept.Worn = item.Worn;
			to.Inventory.Items.Add( kept );
			from.Inventory.Items.Remove( item );
			_journal.Record( JournalKinds.ItemMove, by, to.HolderLabel,
				data: new[] { ("from", from.HolderLabel), ("item", item.Id.ToString( "N" )), ("definition", item.Definition), ("count", item.Count.ToString()),
					("into", kept.Id.ToString( "N" )) } );
		}
		LetGo( from );
		return left == 0 ? Result.Success() : Result.Fail( ErrorCode.Conflict, $"{left} would not fit." );
	}

	public Result Destroy( string sink, IHolder from, Guid itemId, Actor by, int count = 0 )
	{
		var item = from.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count == 0 ) count = item.Count;
		if ( count < 1 || count > item.Count ) return Result.Fail( ErrorCode.Invalid, "There are not that many." );
		Take( from, item, count );
		_journal.Record( JournalKinds.ItemDestroy, by, from.HolderLabel,
			data: new[] { ("sink", sink), ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()) } );
		return Result.Success();
	}

	/// <summary>Part of a pile becomes a pile of its own, in the same hands. Nothing enters or leaves, but it is on record.</summary>
	public Result<ItemStack> Split( IHolder holder, Guid itemId, int count, Actor by )
	{
		var item = holder.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result<ItemStack>.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( count < 1 || count >= item.Count ) return Result<ItemStack>.Fail( ErrorCode.Invalid, "That is not part of it." );
		if ( item.Worn ) return Result<ItemStack>.Fail( ErrorCode.Conflict, Wearing.TakeOffFirst );
		var part = Like( item, Guid.NewGuid() );
		part.Count = count;
		part.Made = Making.Top( item, count );
		var placed = Place( holder.Inventory, part );
		if ( placed.Ok )
		{
			Making.TakeTop( item, count );
			item.Count -= count;
		}
		_journal.Record( JournalKinds.ItemSplit, by, holder.HolderLabel, placed.Ok,
			data: new[] { ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()), ("into", part.Id.ToString( "N" )) } );
		return placed;
	}

	/// <summary>One pile is put on another of the same thing, as far as it will go.</summary>
	public Result Merge( IHolder holder, Guid itemId, Guid intoId, Actor by )
	{
		var item = holder.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		var into = holder.Inventory.Items.FirstOrDefault( value => value.Id == intoId );
		if ( item is null || into is null || item == into ) return Result.Fail( ErrorCode.NotFound, "That item is not there." );
		if ( item.Worn || into.Worn ) return Result.Fail( ErrorCode.Conflict, Wearing.TakeOffFirst );
		var count = Alike( item, into ) ? Math.Min( item.Count, into.Max - into.Count ) : 0;
		if ( count < 1 ) return Result.Fail( ErrorCode.Conflict, "Those do not go together." );
		Making.PutOnTop( into, Making.Top( item, count ), count );
		into.Count += count;
		Take( holder, item, count );
		_journal.Record( JournalKinds.ItemMerge, by, holder.HolderLabel,
			data: new[] { ("item", itemId.ToString( "N" )), ("definition", item.Definition), ("count", count.ToString()), ("into", intoId.ToString( "N" )) } );
		return Result.Success();
	}

	/// <summary>Everything a holder has leaves the world at once, as when a character is retired.</summary>
	public void DestroyAll( string sink, IHolder from, Actor by )
	{
		foreach ( var item in from.Inventory.Items.ToArray() ) Destroy( sink, from, item.Id, by );
	}

	/// <summary>
	/// Something is made. What it is made from is used up, out of <paramref name="from"/>, top of each
	/// pile first; what it makes enters the world in <paramref name="to"/>, each with its mark, from the
	/// made source; <paramref name="maker"/> names what made it, for the record. All of it happens or
	/// none does: there must be enough to make it from, and room for what it makes.
	/// </summary>
	public Result<ItemStack> Make( IHolder from, IHolder to, IReadOnlyList<(string Definition, int Count)> inputs, ItemStack made, int count,
		IReadOnlyList<MadeMark> marks, string maker, Actor by )
	{
		if ( inputs.Any( input => Making.Has( from.Inventory, input.Definition ) < input.Count ) )
			return Result<ItemStack>.Fail( ErrorCode.NotFound, Making.NothingToMakeFrom );
		var fromWas = Copy( from.Inventory );
		var heldWas = (from as CharacterData)?.Equipped;
		var toWas = ReferenceEquals( from, to ) ? null : Copy( to.Inventory );
		var used = new List<(ItemStack Pile, int Count)>();
		foreach ( var (definition, wanted) in inputs )
		{
			var left = wanted;
			// The last pile of a thing first, as a hand takes from what is on top.
			foreach ( var pile in from.Inventory.Items.Where( item => !item.Worn && item.Definition == definition ).Reverse().ToArray() )
			{
				if ( left == 0 ) break;
				var taken = Math.Min( left, pile.Count );
				used.Add( (pile, taken) );
				Take( from, pile, taken );
				left -= taken;
			}
		}
		var template = Like( made, Guid.NewGuid() );
		template.Made = marks.Select( mark => mark.Copy() ).ToList();
		var placed = Add( to.Inventory, template, count );
		if ( !placed.Ok )
		{
			from.Inventory.Items.Clear();
			from.Inventory.Items.AddRange( fromWas );
			if ( from is CharacterData character ) character.Equipped = heldWas;
			if ( toWas is not null )
			{
				to.Inventory.Items.Clear();
				to.Inventory.Items.AddRange( toWas );
			}
			return Result<ItemStack>.Fail( ErrorCode.Conflict, Making.NoRoom );
		}
		foreach ( var (pile, taken) in used )
			_journal.Record( JournalKinds.ItemDestroy, by, from.HolderLabel,
				data: new[] { ("sink", Sinks.Made), ("item", pile.Id.ToString( "N" )), ("definition", pile.Definition), ("count", taken.ToString()), ("maker", maker) } );
		var serials = marks.Where( mark => mark.Serial.Length > 0 ).Select( mark => mark.Serial ).ToArray();
		var data = new List<(string, string)> { ("source", Sources.Made), ("item", placed.Value.Id.ToString( "N" )), ("definition", made.Definition), ("count", count.ToString()), ("maker", maker) };
		if ( serials.Length > 0 ) data.Add( ("serials", serials.Length == 1 ? serials[0] : $"{serials[0]}-{serials[^1]}") );
		_journal.Record( JournalKinds.ItemIssue, by, to.HolderLabel, data: data.ToArray() );
		return placed;
	}

	// What an inventory held, as it was, to put back if a change cannot be finished.
	private static List<ItemStack> Copy( InventoryData inventory ) =>
		inventory.Items.Select( item =>
		{
			var copy = Like( item, item.Id );
			copy.Count = item.Count;
			copy.X = item.X;
			copy.Y = item.Y;
			copy.Worn = item.Worn;
			copy.Made = item.Made?.Select( mark => mark.Copy() ).ToList();
			return copy;
		} ).ToList();

	private static ItemStack Like( ItemStack item, Guid id ) =>
		new() { Id = id, Definition = item.Definition, Width = item.Width, Height = item.Height, Max = item.Max, Frequency = item.Frequency, Pattern = item.Pattern, Paper = item.Paper?.Copy(), Map = item.Map?.Copy() };

	/// <summary>Only the same thing piles up: two keys cut differently, or two radios tuned apart, are two things, and a sheet or a map with anything on it is one of a kind.</summary>
	private static bool Alike( ItemStack a, ItemStack b ) =>
		a.Definition == b.Definition && a.Frequency == b.Frequency && a.Pattern == b.Pattern && a.Paper is null && b.Paper is null && a.Map is null && b.Map is null;

	private static void Take( IHolder holder, ItemStack item, int count )
	{
		Making.TakeTop( item, count );
		item.Count -= count;
		if ( item.Count <= 0 ) holder.Inventory.Items.Remove( item );
		LetGo( holder );
	}

	/// <summary>What has left a character's hands is no longer in them.</summary>
	private static void LetGo( IHolder holder )
	{
		if ( holder is CharacterData { Equipped: { } held } character && character.Inventory.Items.All( item => item.Id != held ) ) character.Equipped = null;
	}

	/// <summary>
	/// Tops up piles of the same thing, then starts new ones, the first of which takes the
	/// template's id. Returns the last pile that received any. If it does not all fit, nothing changes.
	/// </summary>
	private static Result<ItemStack> Add( InventoryData inventory, ItemStack template, int count )
	{
		var topped = new System.Collections.Generic.List<(ItemStack Pile, int Was, System.Collections.Generic.List<MadeMark>? Marks)>();
		var started = new System.Collections.Generic.List<ItemStack>();
		ItemStack? last = null;
		// The marks of what arrives, in order, each going with one of them.
		var marks = template.Made;
		var used = 0;
		System.Collections.Generic.List<MadeMark>? Next( int how ) => marks?.Skip( used ).Take( how ).ToList();
		foreach ( var pile in inventory.Items.Where( value => !value.Worn && Alike( value, template ) && value.Count < value.Max ) )
		{
			if ( count == 0 ) break;
			var room = Math.Min( count, pile.Max - pile.Count );
			topped.Add( (pile, pile.Count, pile.Made) );
			Making.PutOnTop( pile, Next( room ), room );
			used += room;
			pile.Count += room;
			count -= room;
			last = pile;
		}
		while ( count > 0 )
		{
			var pile = Like( template, started.Count == 0 ? template.Id : Guid.NewGuid() );
			pile.Count = Math.Min( count, pile.Max );
			pile.Made = Next( pile.Count );
			used += pile.Count;
			if ( !Place( inventory, pile ).Ok )
			{
				foreach ( var (was, amount, wasMarked) in topped )
				{
					was.Count = amount;
					was.Made = wasMarked;
				}
				foreach ( var added in started ) inventory.Items.Remove( added );
				return Result<ItemStack>.Fail( ErrorCode.Conflict, "There is no room for that." );
			}
			started.Add( pile );
			count -= pile.Count;
			last = pile;
		}
		return Result<ItemStack>.Success( last! );
	}

	/// <summary>Something arrives already worn, so it needs no cell.</summary>
	private static Result<ItemStack> PutOnArriving( InventoryData inventory, ItemStack item )
	{
		item.Worn = true;
		inventory.Items.Add( item );
		return Result<ItemStack>.Success( item );
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
