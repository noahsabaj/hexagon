using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Maps: grease pencil goes on and wipes off, counters move, and what is on a map goes with it.</summary>
[TestClass]
public sealed class MapsTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 12, 12, 0, 0, TimeSpan.Zero );
	private const string Blue = "#1F2A6E";
	private const string Red = "#B3261E";

	private static ItemStack Map() => new() { Id = Guid.NewGuid(), Definition = "items/city_map.item", Width = 2, Height = 1, Count = 1, Max = 5 };

	private static CharacterData Holder() => new() { Id = Guid.NewGuid(), Inventory = new InventoryData { Width = 4, Height = 1 } };

	[TestMethod]
	public void ALineIsDrawnThroughPlacesOnTheMap()
	{
		var map = Map();
		Assert.IsTrue( Maps.Draw( map, new[] { 100, 200, 300, 400, 500, 450 }, Blue ).Ok );
		var line = map.Map!.Marks.Single();
		CollectionAssert.AreEqual( new[] { 100, 200, 300, 400, 500, 450 }, line.Points );
		Assert.AreEqual( Blue, line.Ink, "in the colour of what drew it" );
		Assert.AreEqual( string.Empty, line.Text, "a line is not a word" );

		Assert.AreEqual( ErrorCode.Invalid, Maps.Draw( map, new[] { 100, 200 }, Blue ).Code, "one place is not a line" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Draw( map, new[] { 100, 200, 300 }, Blue ).Code, "every place has two numbers" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Draw( map, new[] { 100, 200, 1001, 400 }, Blue ).Code, "off the map" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Draw( map, new[] { -1, 200, 300, 400 }, Blue ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Draw( map, Enumerable.Repeat( 500, Maps.MaximumPoints * 2 + 2 ).ToArray(), Blue ).Code, "a line keeps only so many points" );
		Assert.IsTrue( Maps.Draw( map, Enumerable.Repeat( 500, Maps.MaximumPoints * 2 ).ToArray(), Blue ).Ok );
		Assert.HasCount( 2, map.Map.Marks );
	}

	[TestMethod]
	public void AWordIsWrittenWhereItStarts()
	{
		var map = Map();
		Assert.IsTrue( Maps.Write( map, 400, 600, "  Rally   point ", Red ).Ok );
		var word = map.Map!.Marks.Single();
		Assert.AreEqual( "Rally point", word.Text, "its spacing tidied" );
		CollectionAssert.AreEqual( new[] { 400, 600 }, word.Points );
		Assert.AreEqual( Red, word.Ink );

		Assert.AreEqual( ErrorCode.Invalid, Maps.Write( map, 400, 600, " \t ", Red ).Code, "nothing to write" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Write( map, 400, 600, new string( 'a', Maps.WordLength + 1 ), Red ).Code );
		Assert.IsTrue( Maps.Write( map, 400, 600, new string( 'a', Maps.WordLength ), Red ).Ok );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Write( map, 1200, 600, "Here", Red ).Code, "off the map" );
	}

	[TestMethod]
	public void GreasePencilWipesOffAndLeavesNothingBehind()
	{
		var map = Map();
		Assert.IsTrue( Maps.Draw( map, new[] { 0, 0, 1000, 1000 }, Blue ).Ok );
		Assert.IsTrue( Maps.Write( map, 500, 500, "Here", Blue ).Ok );
		var (line, word) = (map.Map!.Marks[0].Id, map.Map.Marks[1].Id);
		Assert.AreNotEqual( line, word );

		Assert.IsTrue( Maps.Wipe( map, line ).Ok );
		Assert.AreEqual( word, map.Map.Marks.Single().Id, "only what was wiped is gone" );
		Assert.AreEqual( ErrorCode.NotFound, Maps.Wipe( map, line ).Code, "it cannot be wiped twice" );
		Assert.AreEqual( ErrorCode.NotFound, Maps.Wipe( Map(), 1 ).Code, "nothing on a clean map" );

		Assert.IsTrue( Maps.Draw( map, new[] { 10, 10, 20, 20 }, Blue ).Ok );
		Assert.IsFalse( map.Map.Marks.Any( value => value.Id == line ), "a mark put down later is never taken for one wiped off" );
	}

	[TestMethod]
	public void AFullMapTakesNothingMoreUntilSomethingIsWipedOff()
	{
		var map = Map();
		for ( var mark = 0; mark < Maps.MaximumMarks; mark++ ) Assert.IsTrue( Maps.Draw( map, new[] { 0, 0, 10, 10 }, Blue ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, Maps.Draw( map, new[] { 0, 0, 10, 10 }, Blue ).Code );
		Assert.AreEqual( ErrorCode.Conflict, Maps.Write( map, 0, 0, "More", Blue ).Code );
		Assert.IsTrue( Maps.Wipe( map, map.Map!.Marks[0].Id ).Ok );
		Assert.IsTrue( Maps.Write( map, 0, 0, "More", Blue ).Ok );
	}

	[TestMethod]
	public void CountersStandOnTheMapOrWaitInTheirBox()
	{
		var map = Map();
		Assert.IsTrue( Maps.Move( map, 4, 2, (250, 750) ).Ok );
		var standing = map.Map!.Counters.Single();
		Assert.AreEqual( (2, 250, 750), (standing.Number, standing.X, standing.Y) );
		Assert.AreEqual( 1, map.Max, "a map with anything on it is one of a kind" );

		Assert.IsTrue( Maps.Move( map, 4, 2, (600, 100) ).Ok, "moved again" );
		Assert.AreEqual( (600, 100), (map.Map.Counters.Single().X, map.Map.Counters.Single().Y), "a counter stands in one place" );
		Assert.IsTrue( Maps.Move( map, 4, 0, (10, 10) ).Ok );
		Assert.IsTrue( Maps.Move( map, 4, 2, null ).Ok, "back in its box" );
		Assert.AreEqual( 0, map.Map.Counters.Single().Number );
		Assert.IsTrue( Maps.Move( map, 4, 2, null ).Ok, "one already in its box stays there" );

		Assert.AreEqual( ErrorCode.Invalid, Maps.Move( map, 4, 4, (10, 10) ).Code, "the map came with four" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Move( map, 4, -1, (10, 10) ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Move( map, 4, 1, (10, 1001) ).Code, "off the map is the box, not a place" );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Move( Map(), 0, 0, (10, 10) ).Code, "a map with no counters" );
	}

	[TestMethod]
	public void PlacesAreReadWholeOrNotAtAll()
	{
		var read = Maps.Points( "100,200;300,400; 500 , 600" );
		Assert.IsTrue( read.Ok );
		CollectionAssert.AreEqual( new List<int> { 100, 200, 300, 400, 500, 600 }, read.Value );

		foreach ( var bad in new[] { null, "", "100,200", "100,200;300", "100,200;x,400", "100,200;-3,400", "100,200;+3,400", "100,200;1001,400", "100,200;3.5,400" } )
			Assert.AreEqual( ErrorCode.Invalid, Maps.Points( bad ).Code, $"'{bad}'" );
		var many = string.Join( ";", Enumerable.Repeat( "1,1", Maps.MaximumPoints + 1 ) );
		Assert.AreEqual( ErrorCode.Invalid, Maps.Points( many ).Code );
	}

	[TestMethod]
	public void WhatIsOnAMapGoesWithItAndIsSeenOnlyLaidOut()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder();
		var bob = Holder();
		var map = transfers.Issue( Sources.Operator, alice, "items/city_map.item", 2, 1, Actor.Console ).Value;
		Assert.IsTrue( Maps.Write( map, 500, 500, "Here", Blue ).Ok );
		Assert.IsTrue( Maps.Move( map, 4, 1, (100, 100) ).Ok );

		Assert.IsTrue( transfers.Move( alice, bob, map.Id, Actor.Of( alice ) ).Ok );
		var carried = bob.Inventory.Items.Single();
		Assert.AreEqual( "Here", carried.Map!.Marks.Single().Text, "the grease pencil goes with the map" );
		Assert.AreEqual( 1, carried.Map.Counters.Single().Number, "and so do the counters where they stood" );

		var clean = transfers.Issue( Sources.Operator, bob, "items/city_map.item", 2, 1, Actor.Console, max: 5 ).Value;
		Assert.AreEqual( ErrorCode.Conflict, transfers.Merge( bob, carried.Id, clean.Id, Actor.Of( bob ) ).Code, "a marked map piles with nothing" );
		Assert.HasCount( 2, bob.Inventory.Items );

		var copy = carried.Map.Copy();
		Assert.IsTrue( Maps.Wipe( carried, carried.Map.Marks[0].Id ).Ok );
		Assert.HasCount( 1, copy.Marks, "a copy is its own" );
		Assert.AreEqual( carried.Map.NextMark, copy.NextMark );

		var seen = Seen.Inventory( bob.Inventory ).Items.Single( value => value.Id == carried.Id );
		Assert.IsNull( seen.Map, "a map carried shows nothing of what is on it" );
		Assert.IsNotNull( carried.Map, "the host's copy keeps it" );
	}
}
