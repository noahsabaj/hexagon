using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Clothes are items: worn on the body rather than carried, and moved like anything else.</summary>
[TestClass]
public sealed class WearingTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 11, 12, 0, 0, TimeSpan.Zero );

	private static CharacterData Holder( int width = 2, int height = 1 ) =>
		new() { Id = Guid.NewGuid(), Inventory = new InventoryData { Width = width, Height = height } };

	[TestMethod]
	public void WhatIsWornTakesNoRoomAndComesOffOnlyWhereThereIsRoom()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder( 2, 1 );
		var coat = transfers.Issue( Sources.Arrival, alice, "items/coat.item", 2, 1, Actor.Console, worn: true );
		Assert.IsTrue( coat.Ok );
		Assert.IsTrue( coat.Value.Worn, "she arrives wearing it" );
		Assert.IsTrue( InventoryGrid.Fits( alice.Inventory, 0, 0, 2, 1 ), "a coat on her back takes no room in her pockets" );
		Assert.AreEqual( ErrorCode.Invalid, transfers.Issue( Sources.Arrival, alice, "items/coat.item", 2, 1, Actor.Console, count: 2, worn: true ).Code,
			"nobody wears two of a thing" );

		var ration = transfers.Issue( Sources.Operator, alice, "items/ration.item", 1, 1, Actor.Console );
		Assert.AreEqual( ErrorCode.Conflict, Wearing.TakeOff( alice.Inventory, coat.Value.Id ).Code, "with one cell free, a coat two wide stays on" );
		Assert.IsTrue( coat.Value.Worn );

		Assert.IsTrue( transfers.Destroy( Sinks.Consumed, alice, ration.Value.Id, Actor.Of( alice ) ).Ok );
		Assert.IsTrue( Wearing.TakeOff( alice.Inventory, coat.Value.Id ).Ok );
		Assert.IsFalse( coat.Value.Worn );
		Assert.AreEqual( (0, 0), (coat.Value.X, coat.Value.Y), "taken off into the first free cell" );
		Assert.AreEqual( ErrorCode.Conflict, Wearing.TakeOff( alice.Inventory, coat.Value.Id ).Code, "what is carried is not worn" );
	}

	[TestMethod]
	public void PuttingSomethingOnFreesItsCellsAndItMustComeOffBeforeItIsMovedAbout()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder( 2, 1 );
		var coat = transfers.Issue( Sources.Operator, alice, "items/coat.item", 2, 1, Actor.Console ).Value;
		Assert.AreEqual( ErrorCode.Conflict, transfers.Issue( Sources.Operator, alice, "items/radio.item", 2, 1, Actor.Console ).Code, "carried, it fills her pockets" );

		Assert.IsTrue( Wearing.PutOn( alice.Inventory, coat.Id ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, Wearing.PutOn( alice.Inventory, coat.Id ).Code, "already on" );
		Assert.IsTrue( transfers.Issue( Sources.Operator, alice, "items/radio.item", 2, 1, Actor.Console ).Ok, "worn, it leaves them empty" );

		Assert.AreEqual( ErrorCode.Conflict, InventoryGrid.Move( alice.Inventory, coat.Id, 0, 0 ).Code, "something worn has no cell to move from" );
		var piles = Holder( 2, 1 );
		var cloth = transfers.Issue( Sources.Operator, piles, "items/cloth.item", 1, 1, Actor.Console, count: 2, max: 5 ).Value;
		Assert.AreEqual( ErrorCode.Conflict, Wearing.PutOn( piles.Inventory, cloth.Id ).Code, "a pile is not worn as a pile: one is split off first" );
	}

	[TestMethod]
	public void SomethingWornTakenOffItsWearerBySomeoneElseArrivesCarried()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var prisoner = Holder( 2, 1 );
		var guard = Holder( 2, 1 );
		var cap = transfers.Issue( Sources.Arrival, prisoner, "items/cap.item", 1, 1, Actor.Console, worn: true ).Value;

		Assert.IsTrue( transfers.Move( prisoner, guard, cap.Id, Actor.Of( guard ) ).Ok );
		Assert.IsEmpty( prisoner.Inventory.Items, "the cap is off her head" );
		var taken = guard.Inventory.Items.Single();
		Assert.AreEqual( cap.Id, taken.Id, "the same cap, followed by id" );
		Assert.IsFalse( taken.Worn, "in his hands, not on his head" );
		Assert.AreEqual( 1, journal.Read( Now ).Count( entry => entry.Kind == "item.move" && entry.Ok ), "taking it off her is on the record" );

		Assert.IsTrue( Wearing.PutOn( guard.Inventory, cap.Id ).Ok, "a disguise is a cap he puts on himself" );
	}

	[TestMethod]
	public void ABodyStillWearsWhatTheCharacterWoreAndGivesItBackOnARestore()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var character = Holder( 1, 1 );
		var coat = transfers.Issue( Sources.Arrival, character, "items/coat.item", 2, 1, Actor.Console, worn: true ).Value;
		var ration = transfers.Issue( Sources.Operator, character, "items/ration.item", 1, 1, Actor.Console ).Value;
		var body = new HolderData { Id = Guid.NewGuid(), Kind = HolderKinds.Corpse, Inventory = new InventoryData { Width = 1, Height = 1 } };

		Assert.IsTrue( transfers.MoveAll( character, body, Actor.Of( character ) ).Ok, "a body the size of the pockets takes the pockets and what is worn" );
		Assert.IsTrue( body.Inventory.Items.Single( item => item.Id == coat.Id ).Worn, "the dead still wear their clothes" );
		Assert.IsFalse( body.Inventory.Items.Single( item => item.Id == ration.Id ).Worn );

		Assert.IsTrue( transfers.MoveAll( body, character, Actor.Console ).Ok );
		Assert.IsTrue( character.Inventory.Items.Single( item => item.Id == coat.Id ).Worn, "restored, they get up dressed" );
	}

	[TestMethod]
	public void APileDoesNotGrowOnSomethingWornAndAKeyWornShowsOnlyItsCut()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder( 2, 1 );
		var scarf = transfers.Issue( Sources.Arrival, alice, "items/scarf.item", 1, 1, Actor.Console, max: 5, worn: true ).Value;
		Assert.IsTrue( transfers.Issue( Sources.Operator, alice, "items/scarf.item", 1, 1, Actor.Console, count: 2, max: 5 ).Ok );
		Assert.AreEqual( 1, scarf.Count, "what is worn is not topped up" );
		Assert.AreEqual( 2, Wearing.Carried( alice.Inventory ).Single().Count );
		Assert.AreEqual( ErrorCode.Conflict, transfers.Merge( alice, Wearing.Carried( alice.Inventory ).Single().Id, scarf.Id, Actor.Of( alice ) ).Code );

		var tag = transfers.Issue( Sources.Operator, alice, "items/key.item", 1, 1, Actor.Console, pattern: "city" ).Value;
		Assert.IsTrue( Wearing.PutOn( alice.Inventory, tag.Id ).Ok );
		var seen = Seen.Inventory( alice.Inventory ).Items.Single( item => item.Id == tag.Id );
		Assert.IsTrue( seen.Worn, "she sees it on her" );
		Assert.AreNotEqual( "city", seen.Pattern, "and only its cut" );
	}
}
