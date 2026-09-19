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
