using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Conservation, memory and capabilities: the rules every later feature is built on.</summary>
[TestClass]
public sealed class LawTests
{
	private static readonly DateTimeOffset Now = new( 2026, 9, 19, 12, 0, 0, TimeSpan.Zero );

	private static CharacterData Holder( int width = 2, int height = 1 ) =>
		new() { Id = Guid.NewGuid(), Inventory = new InventoryData { Width = width, Height = height } };

	[TestMethod]
	public void AMovedItemKeepsItsIdentityAndAFullDestinationChangesNothing()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder();
		var bob = Holder( 1, 1 );
		var radio = transfers.Issue( Sources.Operator, alice, "items/radio.item", 2, 1, Actor.Console );

		Assert.AreEqual( ErrorCode.Conflict, transfers.Move( alice, bob, radio.Value.Id, Actor.Of( alice ) ).Code, "too wide for Bob" );
		Assert.HasCount( 1, alice.Inventory.Items, "a refused move leaves the item where it was" );
		Assert.IsEmpty( bob.Inventory.Items );

		var ration = transfers.Issue( Sources.Operator, Holder(), "items/ration.item", 1, 1, Actor.Console );
		Assert.AreEqual( ErrorCode.NotFound, transfers.Move( alice, bob, ration.Value.Id, Actor.Of( alice ) ).Code, "Alice cannot give what she does not hold" );

		var carol = Holder();
		Assert.IsTrue( transfers.Move( alice, carol, radio.Value.Id, Actor.Of( alice ) ).Ok );
		Assert.IsEmpty( alice.Inventory.Items );
		Assert.AreEqual( radio.Value.Id, carol.Inventory.Items.Single().Id );
		Assert.AreEqual( 1, alice.Inventory.Items.Count + bob.Inventory.Items.Count + carol.Inventory.Items.Count, "one radio exists" );

		var refused = journal.Read( Now ).Where( entry => entry.Kind == "item.move" && !entry.Ok ).ToArray();
		Assert.HasCount( 1, refused, "the refused move is on record" );
	}

	[TestMethod]
	public void IdenticalThingsPileUpAndEveryOneOfThemIsAccountedFor()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder( 2, 1 );
		var bob = Holder( 1, 1 );
		const string Round = "items/pistol_round.item";
		int Held( IHolder holder ) => holder.Inventory.Items.Where( item => item.Definition == Round ).Sum( item => item.Count );

		Assert.IsTrue( transfers.Issue( Sources.CharacterStart, alice, Round, 1, 1, Actor.Console, count: 30, max: 30 ).Ok );
		Assert.HasCount( 1, alice.Inventory.Items, "thirty rounds are one slot" );
		Assert.AreEqual( ErrorCode.Conflict, transfers.Issue( Sources.Operator, alice, Round, 1, 1, Actor.Console, count: 31, max: 30 ).Code, "two more slots are not there" );
		Assert.AreEqual( 30, Held( alice ), "a refused issue leaves nothing behind" );

		var pile = alice.Inventory.Items.Single();
		Assert.AreEqual( ErrorCode.Invalid, transfers.Move( alice, bob, pile.Id, Actor.Of( alice ), count: 31 ).Code, "no more than there are" );
		Assert.AreEqual( ErrorCode.Invalid, transfers.Move( alice, bob, pile.Id, Actor.Of( alice ), count: -5 ).Code, "a negative gift is a theft" );
		Assert.IsTrue( transfers.Move( alice, bob, pile.Id, Actor.Of( alice ), count: 10 ).Ok );
		Assert.AreNotEqual( pile.Id, bob.Inventory.Items.Single().Id, "part of a pile is a new pile" );
		Assert.IsTrue( transfers.Move( alice, bob, pile.Id, Actor.Of( alice ) ).Ok, "the rest joins it" );
		Assert.IsEmpty( alice.Inventory.Items );
		Assert.AreEqual( 30, bob.Inventory.Items.Single().Count );

		Assert.IsTrue( transfers.Issue( Sources.Operator, alice, Round, 1, 1, Actor.Console, count: 5, max: 30 ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, transfers.Move( alice, bob, alice.Inventory.Items.Single().Id, Actor.Of( alice ) ).Code, "Bob's one slot is full" );
		Assert.AreEqual( 30, Held( bob ), "and a refused move tops nothing up" );

		var part = transfers.Split( alice, alice.Inventory.Items.Single().Id, 2, Actor.Of( alice ) );
		Assert.IsTrue( part.Ok );
		Assert.HasCount( 2, alice.Inventory.Items );
		Assert.AreEqual( ErrorCode.Invalid, transfers.Split( alice, part.Value.Id, 2, Actor.Of( alice ) ).Code, "all of it is not part of it" );
		Assert.IsTrue( transfers.Merge( alice, part.Value.Id, alice.Inventory.Items.First( item => item.Id != part.Value.Id ).Id, Actor.Of( alice ) ).Ok );
		Assert.AreEqual( 5, alice.Inventory.Items.Single().Count );
		Assert.IsTrue( transfers.Destroy( Sinks.Consumed, bob, bob.Inventory.Items.Single().Id, Actor.Of( bob ), count: 1 ).Ok, "one shot" );

		int Sum( string kind ) => journal.Read( Now ).Where( entry => entry.Kind == kind && entry.Ok ).Sum( entry => int.Parse( entry.Data["count"] ) );
		Assert.AreEqual( 34, Held( alice ) + Held( bob ) );
		Assert.AreEqual( Held( alice ) + Held( bob ), Sum( "item.issue" ) - Sum( "item.destroy" ), "the supply of a thing is the journal's issues minus its destroys" );
		Assert.AreEqual( 1, journal.Read( Now ).Count( entry => entry.Kind == "item.split" && entry.Ok ) );
		Assert.AreEqual( 1, journal.Read( Now ).Count( entry => entry.Kind == "item.merge" ) );
	}

	[TestMethod]
	public void TheTokenSupplyIsTheJournalsIssuesMinusItsDestroys()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder();
		var bob = Holder();

		Assert.IsTrue( transfers.IssueTokens( Sources.CharacterStart, alice, 100, Actor.Of( alice ) ).Ok );
		Assert.IsTrue( transfers.MoveTokens( alice, bob, 30, Actor.Of( alice ) ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, transfers.MoveTokens( alice, bob, 71, Actor.Of( alice ) ).Code, "no debt" );
		Assert.AreEqual( ErrorCode.Invalid, transfers.MoveTokens( bob, alice, -30, Actor.Of( bob ) ).Code, "a negative gift is a theft" );
		Assert.AreEqual( ErrorCode.Invalid, transfers.MoveTokens( alice, alice, 5, Actor.Of( alice ) ).Code );
		Assert.IsTrue( transfers.DestroyTokens( Sinks.Discard, bob, 10, Actor.Of( bob ) ).Ok );

		long Sum( string kind ) => journal.Read( Now ).Where( entry => entry.Kind == kind ).Sum( entry => long.Parse( entry.Data["amount"] ) );
		Assert.AreEqual( 90, alice.Tokens + bob.Tokens );
		Assert.AreEqual( alice.Tokens + bob.Tokens, Sum( "tokens.issue" ) - Sum( "tokens.destroy" ) );
	}

	[TestMethod]
	public void DeletingAHolderSendsEverythingItHadThroughASink()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder();
		transfers.Issue( Sources.CharacterStart, alice, "items/ration.item", 1, 1, Actor.Of( alice ) );
		transfers.IssueTokens( Sources.CharacterStart, alice, 100, Actor.Of( alice ) );

		transfers.DestroyAll( Sinks.CharacterDeleted, alice, Actor.Of( alice ) );

		Assert.IsEmpty( alice.Inventory.Items );
		Assert.AreEqual( 0, alice.Tokens );
		Assert.AreEqual( 2, journal.Read( Now ).Count( entry => entry.Data.GetValueOrDefault( "sink" ) == Sinks.CharacterDeleted ) );
	}

	[TestMethod]
	public void TheJournalSurvivesACrashMidEntryAndAFullDisk()
	{
		var files = new MemoryFiles();
		var journal = new Journal( files, () => Now );
		journal.Record( "first", Actor.Console );
		files.CrashOnWrite = files.Writes + 1;
		// Does not throw: the decision it records was already made.
		journal.Record( "torn", Actor.Console );

		var rebooted = files.Reboot();
		var after = new Journal( rebooted, () => Now );
		after.Record( "second", new Actor( 7, Guid.NewGuid(), "Alice" ), "door:1", witnesses: new[] { Guid.NewGuid() }, data: ("verb", "lock") );

		var entries = after.Read( Now );
		CollectionAssert.AreEqual( new[] { "first", "second" }, entries.Select( entry => entry.Kind ).ToArray() );
		Assert.AreEqual( "lock", entries[1].Data["verb"] );
		Assert.HasCount( 1, entries[1].Witnesses );
		Assert.IsEmpty( after.Read( Now.AddDays( 1 ) ), "each day is its own file" );
	}

	[TestMethod]
	public void ACrateKeepsWhatWasPutInItAcrossARestartAndCannotVanishFull()
	{
		var files = new MemoryFiles();
		var transfers = new Transfers( new Journal( files, () => Now ) );
		var holders = new HolderStore( new DocumentStore( files ) );
		var crateId = Guid.NewGuid();
		var crate = holders.GetOrCreate( crateId, HolderKinds.Container, "Crate", 4, 4 );
		var alice = Holder();
		var ration = transfers.Issue( Sources.Operator, alice, "items/ration.item", 1, 1, Actor.Console );

		Assert.IsTrue( transfers.Move( alice, crate, ration.Value.Id, Actor.Of( alice ) ).Ok );
		holders.Save( crate );

		var reloaded = new HolderStore( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( ration.Value.Id, reloaded.Find( crateId )!.Inventory.Items.Single().Id );
		Assert.AreSame( reloaded.Find( crateId ), reloaded.GetOrCreate( crateId, HolderKinds.Container, "Crate", 4, 4 ) );
		Assert.AreEqual( ErrorCode.Conflict, reloaded.Delete( crateId ).Code, "deleting a full crate would delete what is in it" );
		Assert.IsTrue( transfers.Move( reloaded.Find( crateId )!, alice, ration.Value.Id, Actor.Of( alice ) ).Ok );
		Assert.IsTrue( reloaded.Delete( crateId ).Ok );
	}

	[TestMethod]
	public void AStrangerIsWhatTheyLookLikeUntilTheyIntroduceThemselves()
	{
		var john = new CharacterData { Id = Guid.NewGuid(), Name = "John Doe", Description = "A tired resident of the city." };
		var jane = new CharacterData { Id = Guid.NewGuid(), Name = "Jane Roe", Description = "A woman in a grey coat, watching the street from a doorway." };

		Assert.AreEqual( "John Doe", Recognition.Label( john, john ), "everyone knows their own name" );
		Assert.AreEqual( "[A tired resident of the city.]", Recognition.Label( jane, john ) );
		Assert.AreEqual( "[A woman in a grey coat, watching the str...]", Recognition.Label( john, jane ) );

		Assert.IsTrue( Recognition.Introduce( john, jane ).Ok );
		Assert.AreEqual( "John Doe", Recognition.Label( jane, john ) );
		Assert.AreEqual( "[A woman in a grey coat, watching the str...]", Recognition.Label( john, jane ), "telling is one way" );
		Assert.AreEqual( ErrorCode.Conflict, Recognition.Introduce( john, jane ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Recognition.Introduce( john, john ).Code );
	}

	[TestMethod]
	public void ASaleMovesTheItemAndTheTokensTogetherOrNeither()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var shop = new HolderData { Id = Guid.NewGuid(), Kind = HolderKinds.Vendor, Inventory = new InventoryData { Width = 4, Height = 4 } };
		var alice = Holder( 1, 1 );
		transfers.IssueTokens( Sources.CharacterStart, alice, 15, Actor.Of( alice ) );
		var ration = transfers.Issue( "vendor.restock", shop, "items/ration.item", 1, 1, Actor.Console ).Value;
		var second = transfers.Issue( "vendor.restock", shop, "items/ration.item", 1, 1, Actor.Console ).Value;

		Assert.AreEqual( 10, Trade.Price( 10, 1f ) );
		Assert.AreEqual( 5, Trade.Price( 9, 0.5f ), "prices round up, never to nothing" );
		Assert.AreEqual( ErrorCode.Conflict, Trade.Sell( transfers, shop, alice, ration.Id, 20, Actor.Of( alice ) ).Code, "she cannot afford it" );
		Assert.AreEqual( 15, alice.Tokens );
		Assert.IsTrue( Trade.Sell( transfers, shop, alice, ration.Id, 10, Actor.Of( alice ) ).Ok );
		Assert.AreEqual( (5L, 10L), (alice.Tokens, shop.Tokens) );
		Assert.AreEqual( ErrorCode.Conflict, Trade.Sell( transfers, shop, alice, second.Id, 5, Actor.Of( alice ) ).Code, "no room" );
		Assert.AreEqual( (5L, 10L), (alice.Tokens, shop.Tokens), "and so no payment" );
		Assert.AreEqual( ErrorCode.Conflict, Trade.Sell( transfers, alice, shop, ration.Id, 11, Actor.Of( alice ) ).Code, "the till cannot pay what it does not hold" );
		Assert.IsTrue( Trade.Sell( transfers, alice, shop, ration.Id, 5, Actor.Of( alice ) ).Ok );
		Assert.AreEqual( 15, alice.Tokens + shop.Tokens, "a sale creates no tokens" );
	}

	[TestMethod]
	public void TheJournalCanBeSearchedAndReadsAsSentences()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var alice = new Actor( 7, Guid.NewGuid(), "Alice" );
		journal.Record( "verb.door.lock", alice, "Door:1", ok: false, witnesses: new[] { Guid.NewGuid() }, data: ("reason", "Denied") );
		journal.Record( "chat.say", alice, data: ("text", "hello") );
		journal.Record( "operator.give", Actor.Console, "character:2" );

		Assert.AreEqual( "12:00:00 verb.door.lock REFUSED by Alice on Door:1 (reason=Denied) seen by 1", journal.Read( Now )[0].Describe() );
		Assert.AreEqual( "12:00:00 operator.give by console on character:2", journal.Read( Now )[2].Describe() );
		Assert.HasCount( 2, journal.Search( Now, "alice", 10 ) );
		Assert.AreEqual( "chat.say", journal.Search( Now, "alice", 1 ).Single().Kind, "the latest are kept" );
		Assert.HasCount( 3, journal.Search( Now, "", 10 ) );
		Assert.IsEmpty( journal.Search( Now, "bob", 10 ) );
	}

	[TestMethod]
	public void CapabilitiesAreGrantedByNameOrPrefixAndDenialWins()
	{
		Assert.IsTrue( Capabilities.Can( Capability.DoorLock, new[] { "door.lock" } ) );
		Assert.IsTrue( Capabilities.Can( Capability.DoorLock, new[] { "door.*" } ) );
		Assert.IsTrue( Capabilities.Can( Capability.DoorLock, new[] { "*" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.DoorLock, new[] { "door" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.DoorLock, new[] { "doors.*", "door.locksmith" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.DoorLock, Array.Empty<string>() ) );
		Assert.IsFalse( Capabilities.Can( Capability.DoorLock, new[] { "*" }, denied: new[] { "door.*" } ), "restraints beat rank" );
	}
}
