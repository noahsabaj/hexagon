using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
	public void ABodyTakesEverythingTheCharacterCarriedHoweverItWasPacked()
	{
		// A full inventory packed by hand. Moved one pile at a time into the first free cell of a body
		// the same size, the last can of water finds no room. Kept where each pile was, all of it fits.
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var character = Holder( 6, 4 );
		var layout = new (string Definition, int Width, int Height, int X, int Y)[]
		{
			("items/keycard.item", 1, 1, 0, 0), ("items/pistol.item", 2, 1, 1, 0), ("items/water.item", 1, 2, 5, 2),
			("items/keycard.item", 1, 1, 4, 1), ("items/pistol.item", 2, 1, 0, 2), ("items/pistol.item", 2, 1, 3, 3),
			("items/pistol.item", 2, 1, 2, 2), ("items/pistol.item", 2, 1, 0, 3), ("items/pistol.item", 2, 1, 1, 1),
			("items/water.item", 1, 2, 3, 0),
		};
		foreach ( var (definition, width, height, x, y) in layout )
			character.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = definition, Width = width, Height = height, X = x, Y = y } );
		var carried = character.Inventory.Items.Select( item => item.Id ).ToArray();
		character.Equipped = carried[1];
		var body = new HolderData { Id = Guid.NewGuid(), Kind = HolderKinds.Corpse, Inventory = new InventoryData { Width = 6, Height = 4 } };

		Assert.IsTrue( transfers.MoveAll( character, body, Actor.Of( character ) ).Ok );

		Assert.IsEmpty( character.Inventory.Items, "nothing stays on the dead" );
		Assert.IsNull( character.Equipped, "not even in their hand" );
		CollectionAssert.AreEquivalent( carried, body.Inventory.Items.Select( item => item.Id ).ToArray(), "each is the same object it was" );
		Assert.AreEqual( 10, journal.Read( Now ).Count( entry => entry.Kind == "item.move" && entry.Ok ) );
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

		Assert.IsTrue( transfers.Issue( Sources.Scene, alice, Round, 1, 1, Actor.Console, count: 30, max: 30 ).Ok );
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
	public void AKeyFitsEveryLockCutToItsPatternAndShowsOnlyItsCut()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder( 3, 1 );
		var bob = Holder( 3, 1 );
		const string Key = "items/key.item";
		var city = transfers.Issue( Sources.Operator, alice, Key, 1, 1, Actor.Console, max: 5, pattern: "city" ).Value;
		var flat = transfers.Issue( Sources.Operator, alice, Key, 1, 1, Actor.Console, max: 5, pattern: "flat2" ).Value;

		Assert.HasCount( 2, alice.Inventory.Items, "keys cut differently never pile up" );
		Assert.AreEqual( ErrorCode.Conflict, transfers.Merge( alice, flat.Id, city.Id, Actor.Of( alice ) ).Code, "nor can they be put together" );
		Assert.AreEqual( "city", journal.Read( Now ).First( entry => entry.Kind == "item.issue" ).Data["pattern"], "a key is issued on the record with its pattern" );
		Assert.IsTrue( Keys.Opens( alice.Inventory, "city" ) );
		Assert.IsTrue( Keys.Opens( alice.Inventory, "CITY" ), "a pattern is not a spelling" );
		Assert.IsFalse( Keys.Opens( alice.Inventory, "ministry" ) );
		Assert.IsFalse( Keys.Opens( alice.Inventory, "" ), "a lock with no pattern has no keyhole" );

		alice.Equipped = city.Id;
		Assert.IsTrue( transfers.Move( alice, bob, city.Id, Actor.Of( alice ) ).Ok );
		Assert.IsNull( alice.Equipped, "what left her hand is no longer in it" );
		Assert.IsFalse( Keys.Opens( alice.Inventory, "city" ) );
		Assert.IsTrue( Keys.Opens( bob.Inventory, "city" ), "the cut goes with the key" );

		var seen = Seen.Inventory( bob.Inventory ).Items.Single();
		Assert.AreEqual( Keys.Cut( "city" ), seen.Pattern, "a player is shown the cut" );
		Assert.AreEqual( "city", bob.Inventory.Items.Single().Pattern, "and showing it changes nothing" );
		Assert.IsTrue( Regex.IsMatch( seen.Pattern!, "^[1-7](-[1-7]){4}$" ), seen.Pattern );
		Assert.AreEqual( Keys.Cut( "City" ), Keys.Cut( "city" ) );
		Assert.AreNotEqual( Keys.Cut( "flat2" ), Keys.Cut( "city" ) );

		Assert.AreEqual( "flat2", Keys.Pattern( "  Flat2 " ).Value );
		Assert.AreEqual( ErrorCode.Invalid, Keys.Pattern( "two words" ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Keys.Pattern( " " ).Code );
	}

	[TestMethod]
	public void RetiringACharacterSendsEverythingItHadThroughASink()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Holder();
		transfers.Issue( Sources.Operator, alice, "items/ration.item", 1, 1, Actor.Console );
		transfers.Issue( Sources.Operator, alice, "items/token.item", 1, 1, Actor.Console, count: 40, max: 100 );

		transfers.DestroyAll( Sinks.CharacterRetired, alice, Actor.Of( alice ) );

		Assert.IsEmpty( alice.Inventory.Items );
		Assert.AreEqual( 41, journal.Read( Now ).Where( entry => entry.Data.GetValueOrDefault( "sink" ) == Sinks.CharacterRetired ).Sum( entry => int.Parse( entry.Data["count"] ) ) );
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
	public void TheJournalNamesWhoWasActedOnAndWhoSawItAndSearchesPastMidnight()
	{
		var clock = new DateTimeOffset( 2026, 9, 19, 23, 59, 0, TimeSpan.Zero );
		var journal = new Journal( new MemoryFiles(), () => clock );
		var alice = new CharacterData { Id = Guid.NewGuid(), SteamId = 7, Name = "Alice" };
		var bob = new CharacterData { Id = Guid.NewGuid(), SteamId = 8, Name = "Bob" };
		var names = new Dictionary<Guid, string> { [alice.Id] = alice.Name, [bob.Id] = bob.Name };
		journal.NameOf = id => names.GetValueOrDefault( id );

		journal.Record( "verb.person.search", Actor.Of( alice ), bob.HolderLabel, witnesses: new[] { bob.Id, Guid.NewGuid() } );
		clock = clock.AddMinutes( 2 );
		// Bob is deleted afterwards, and his name could be taken again. The entry says who it was then.
		names.Remove( bob.Id );
		journal.Record( "item.move", Actor.Of( alice ), alice.HolderLabel, data: ("from", bob.HolderLabel) );

		Assert.IsEmpty( journal.Search( clock, "Bob", 10 ), "today alone misses what happened a minute before midnight" );
		var found = journal.Search( clock, "Bob", 10, days: 2 );
		Assert.HasCount( 1, found );
		Assert.AreEqual( $"23:59:00 verb.person.search by Alice on Bob ({bob.HolderLabel}) seen by Bob, someone", found[0].Describe() );
		CollectionAssert.AreEqual( new[] { "verb.person.search", "item.move" }, journal.Search( clock, "alice", 10, days: 2 ).Select( entry => entry.Kind ).ToArray(), "oldest first" );
	}

	[TestMethod]
	public void CapabilitiesAreGrantedByNameOrPrefixAndDenialWins()
	{
		Assert.IsTrue( Capabilities.Can( Capability.Restrain, new[] { "person.restrain" } ) );
		Assert.IsTrue( Capabilities.Can( Capability.Restrain, new[] { "person.*" } ) );
		Assert.IsTrue( Capabilities.Can( Capability.Restrain, new[] { "*" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.Restrain, new[] { "person" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.Restrain, new[] { "persons.*", "person.restrainer" } ) );
		Assert.IsFalse( Capabilities.Can( Capability.Restrain, Array.Empty<string>() ) );
		Assert.IsFalse( Capabilities.Can( Capability.Restrain, new[] { "*" }, denied: new[] { "person.*" } ), "a denial beats any grant" );
	}
}
