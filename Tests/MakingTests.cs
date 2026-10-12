using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Making: something is made from something used up, and carries the mark of what made it wherever it goes.</summary>
[TestClass]
public sealed class MakingTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 12, 12, 0, 0, TimeSpan.Zero );
	private const string Blank = "items/blank.item";
	private const string Token = "items/token.item";
	private static readonly (string, int)[] TenBlanks = { (Blank, 10) };

	private static HolderData Press( int width = 2, int height = 1 ) =>
		new() { Id = Guid.NewGuid(), Kind = "machine", Inventory = new InventoryData { Width = width, Height = height } };

	private static CharacterData Person( int width = 2, int height = 1 ) =>
		new() { Id = Guid.NewGuid(), Inventory = new InventoryData { Width = width, Height = height } };

	private static ItemStack Tokens => new() { Definition = Token, Max = 100 };

	[TestMethod]
	public void APressMakesFromWhatItHoldsAllOrNothing()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var press = Press();
		transfers.Issue( Sources.Scene, press, Blank, 1, 1, Actor.Console, count: 9, max: 50 );

		var wanting = transfers.Make( press, press, TenBlanks, Tokens, 10, Making.Marks( "press:a", 10 ), "press:a", Actor.Console );
		Assert.AreEqual( ErrorCode.NotFound, wanting.Code, "nine blanks do not make ten tokens" );
		Assert.AreEqual( 9, Making.Has( press.Inventory, Blank ), "and nothing was used up trying" );

		transfers.Issue( Sources.Scene, press, Blank, 1, 1, Actor.Console, count: 1, max: 50 );
		var made = transfers.Make( press, press, TenBlanks, Tokens, 10, Making.Marks( "press:a", 10 ), "press:a", Actor.Console );
		Assert.IsTrue( made.Ok, made.Message );
		Assert.AreEqual( 0, Making.Has( press.Inventory, Blank ), "the blanks are used up" );
		Assert.AreEqual( 10, Making.Has( press.Inventory, Token ) );
		Assert.IsTrue( made.Value.Made!.All( mark => mark.By == "press:a" ), "each token carries the press's mark" );

		var entries = journal.Read( Now ).ToArray();
		Assert.AreEqual( "10", entries.Single( entry => entry.Kind == "item.destroy" ).Data["count"] );
		Assert.AreEqual( Sinks.Made, entries.Single( entry => entry.Kind == "item.destroy" ).Data["sink"] );
		var issued = entries.Last( entry => entry.Kind == "item.issue" );
		Assert.AreEqual( Sources.Made, issued.Data["source"], "what is made enters the world from a named source, so the supply is still a sum over the journal" );
		Assert.AreEqual( "press:a", issued.Data["maker"] );
	}

	[TestMethod]
	public void WithNoRoomForWhatItMakesNothingIsUsedUp()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var worker = Person( 1, 1 );
		worker.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = Blank, Count = 1, Max = 50 } );
		var full = Press( 1, 1 );
		full.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = "items/radio.item" } );

		var made = transfers.Make( worker, full, new[] { (Blank, 1) }, Tokens, 1, Making.Marks( "die:a", 1 ), "die:a", Actor.Of( worker ) );

		Assert.AreEqual( ErrorCode.Conflict, made.Code );
		Assert.AreEqual( 1, Making.Has( worker.Inventory, Blank ), "the blank is still there" );
		Assert.IsFalse( journal.Read( Now ).Any( entry => entry.Kind is "item.destroy" or "item.issue" ), "nothing was made, so nothing is on record as made" );
	}

	[TestMethod]
	public void EveryThingKeepsItsMarkThroughEveryHand()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var alice = Person();
		var bob = Person();
		// Three tokens from before anything was made, then five off a press, then two struck by a die.
		var old = transfers.Issue( Sources.Scene, alice, Token, 1, 1, Actor.Console, count: 3, max: 100 ).Value;
		alice.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = Blank, Count = 7, Max = 50, X = 1 } );
		transfers.Make( alice, alice, new[] { (Blank, 5) }, Tokens, 5, Making.Marks( "press:a", 5 ), "press:a", Actor.Of( alice ) );
		transfers.Make( alice, alice, new[] { (Blank, 2) }, Tokens, 2, Making.Marks( "die:b", 2 ), "die:b", Actor.Of( alice ) );
		Assert.AreEqual( 10, old.Count, "made and unmade tokens pile together: nothing tells them apart at a glance" );

		// The top three go to Bob: the two struck by the die and one off the press.
		Assert.IsTrue( transfers.Move( alice, bob, old.Id, Actor.Of( alice ), count: 3 ).Ok );
		var given = bob.Inventory.Items.Single();
		CollectionAssert.AreEqual( new[] { "press:a", "die:b", "die:b" }, given.Made!.Select( mark => mark.By ).ToArray() );
		CollectionAssert.AreEqual( new[] { "", "", "", "press:a", "press:a", "press:a", "press:a" }, old.Made!.Select( mark => mark.By ).ToArray() );

		// Split off the top two, and put them back on.
		var part = transfers.Split( alice, old.Id, 2, Actor.Of( alice ) ).Value;
		CollectionAssert.AreEqual( new[] { "press:a", "press:a" }, part.Made!.Select( mark => mark.By ).ToArray() );
		Assert.HasCount( 5, old.Made!, "a pile has a mark for each thing in it" );
		Assert.IsTrue( transfers.Merge( alice, part.Id, old.Id, Actor.Of( alice ) ).Ok );
		Assert.HasCount( 7, old.Made! );

		// Spending takes from the top.
		Assert.IsTrue( transfers.Destroy( Sinks.Consumed, bob, given.Id, Actor.Of( bob ), count: 2 ).Ok );
		CollectionAssert.AreEqual( new[] { "press:a" }, given.Made!.Select( mark => mark.By ).ToArray() );
	}

	private const string Scrap = "items/scrap_metal.item";
	private const string Lock = "items/lock.item";
	private const string Key = "items/key.item";
	private const string KeyBlank = "items/key_blank.item";

	[TestMethod]
	public void ALockIsMadeWithItsKeysAllCutToAPatternNobodyHasCutBefore()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var bench = Press( 4, 2 );
		transfers.Issue( Sources.Scene, bench, Scrap, 1, 1, Actor.Console, count: 4, max: 50 );
		var recipe = new Recipe( "Make a lock", new[] { (Scrap, 2) }, Lock, 1, 10f, Also: new[] { (Key, 2) }, Cut: MakingCut.New );
		Assert.IsTrue( Making.Enough( recipe, bench.Inventory ) );

		var cut = Making.Cut( recipe, bench.Inventory ).Value!;
		Assert.IsTrue( Keys.Pattern( cut ).Ok, "a new pattern is one staff could have typed" );
		Assert.AreNotEqual( cut, Making.Cut( recipe, bench.Inventory ).Value, "and no two locks are made alike" );
		var made = transfers.Make( bench, bench, recipe.Inputs, new[]
		{
			new Transfers.Made( new ItemStack { Definition = Lock, Pattern = cut }, 1, Making.Marks( "bench:a", 1 ) ),
			new Transfers.Made( new ItemStack { Definition = Key, Max = 10, Pattern = cut }, 2, Making.Marks( "bench:a", 2 ) ),
		}, "bench:a", Actor.Console );

		Assert.IsTrue( made.Ok, made.Message );
		Assert.AreEqual( Lock, made.Value.Definition, "what is made first is what the work is for" );
		Assert.AreEqual( 2, Making.Has( bench.Inventory, Scrap ), "two of the four pieces of scrap are used up" );
		Assert.AreEqual( cut, bench.Inventory.Items.Single( item => item.Definition == Lock ).Pattern );
		var keys = bench.Inventory.Items.Single( item => item.Definition == Key );
		Assert.AreEqual( 2, keys.Count );
		Assert.AreEqual( cut, keys.Pattern, "the keys that come with a lock turn it" );
		var issued = journal.Read( Now ).Where( entry => entry.Kind == "item.issue" && entry.Data["source"] == Sources.Made ).ToArray();
		CollectionAssert.AreEqual( new[] { Lock, Key }, issued.Select( entry => entry.Data["definition"] ).ToArray(), "each thing made is on record" );
		Assert.IsTrue( issued.All( entry => entry.Data["pattern"] == cut ), "with what it is cut to, as a key issued by staff is" );
	}

	[TestMethod]
	public void AKeyIsCopiedFromOneThatIsKept()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var bench = Press( 4, 2 );
		var recipe = new Recipe( "Copy a key", new[] { (KeyBlank, 1) }, Key, 1, 5f, Kept: new[] { (Key, 1) }, Cut: MakingCut.Copied );
		transfers.Issue( Sources.Scene, bench, KeyBlank, 1, 1, Actor.Console, count: 2, max: 10 );
		Assert.IsFalse( Making.Enough( recipe, bench.Inventory ), "a blank alone is not enough: there is nothing to copy" );

		transfers.Issue( Sources.Scene, bench, Key, 1, 1, Actor.Console, max: 10 );
		Assert.IsTrue( Making.Enough( recipe, bench.Inventory ) );
		Assert.AreEqual( Making.NothingToCopy, Making.Cut( recipe, bench.Inventory ).Message, "a key cut to nothing has nothing to copy" );

		bench.Inventory.Items.Clear();
		transfers.Issue( Sources.Scene, bench, KeyBlank, 1, 1, Actor.Console, count: 2, max: 10 );
		var model = transfers.Issue( Sources.Scene, bench, Key, 1, 1, Actor.Console, max: 10, pattern: "flat2" ).Value;
		var cut = Making.Cut( recipe, bench.Inventory );
		Assert.AreEqual( "flat2", cut.Value );
		var made = transfers.Make( bench, bench, recipe.Inputs,
			new[] { new Transfers.Made( new ItemStack { Definition = Key, Max = 10, Pattern = cut.Value }, 1, Making.Marks( "bench:a", 1 ) ) }, "bench:a", Actor.Console );

		Assert.IsTrue( made.Ok, made.Message );
		Assert.AreEqual( 1, Making.Has( bench.Inventory, KeyBlank ), "the blank is used up" );
		Assert.AreEqual( 2, model.Count, "and the copy is cut like the key it was copied from, which is kept, so the two pile together" );
		Assert.IsTrue( Keys.Opens( bench.Inventory, "flat2" ) );
	}

	[TestMethod]
	public void EnoughCountsWhatIsUsedUpAndWhatIsKeptOfOneThingTogether()
	{
		var bench = Press( 4, 1 );
		bench.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = Key, Count = 1, Max = 10, Pattern = "flat2" } );
		var recipe = new Recipe( "Recut", new[] { (Key, 1) }, Key, 1, 5f, Kept: new[] { (Key, 1) }, Cut: MakingCut.Copied );

		Assert.IsFalse( Making.Enough( recipe, bench.Inventory ), "one key cannot be both used up and kept" );
		bench.Inventory.Items.Add( new ItemStack { Id = Guid.NewGuid(), Definition = Key, Count = 1, Max = 10, X = 1 } );
		Assert.IsTrue( Making.Enough( recipe, bench.Inventory ) );
	}

	[TestMethod]
	public void AnEyeUnderTheRightToolTellsOnlyWhetherThingsWereMadeAlike()
	{
		var pile = new ItemStack { Definition = Token, Count = 1, Max = 100 };
		Assert.AreEqual( "There is only one, with nothing to compare it to.", Making.Compare( pile ) );

		Making.PutOnTop( pile, Making.Marks( "press:a", 4 ), 4 );
		pile.Count += 4;
		Assert.AreEqual( "They were made 2 ways: 1 one way; 4 another.", Making.Compare( pile ), "a token from before presses is not like those off one" );

		var notes = new ItemStack { Definition = "items/note.item", Max = 100, Count = 0 };
		Making.PutOnTop( notes, Making.Marks( "press:bank", 2, "A", 101 ), 2 );
		notes.Count = 2;
		Assert.AreEqual( "All 2 were made alike.", Making.Compare( notes ) );
		Making.PutOnTop( notes, Making.Marks( "press:forger", 1, "A", 101 ), 1 );
		notes.Count = 3;
		Assert.AreEqual( "They were made 2 ways: A0000101, A0000102 one way; A0000101 another.", Making.Compare( notes ),
			"a forger who copies a serial is told apart by the press, not by the number" );
		Assert.DoesNotContain( "press:", Making.Compare( notes ), "and never named" );
	}

	[TestMethod]
	public void ASerialIsReadByAnyoneAndWhatPrintedItByNobody()
	{
		var holder = Person();
		var notes = new ItemStack { Id = Guid.NewGuid(), Definition = "items/note.item", Max = 100, Count = 2, Made = Making.Marks( "press:bank", 2, "A", 1 ) };
		var tokens = new ItemStack { Id = Guid.NewGuid(), Definition = Token, Max = 100, Count = 2, X = 1, Made = Making.Marks( "press:a", 2 ) };
		holder.Inventory.Items.Add( notes );
		holder.Inventory.Items.Add( tokens );

		var seen = Seen.Inventory( holder.Inventory );

		CollectionAssert.AreEqual( new[] { "A0000001", "A0000002" }, seen.Items[0].Made!.Select( mark => mark.Serial ).ToArray() );
		Assert.IsTrue( seen.Items[0].Made!.All( mark => mark.By.Length == 0 ), "what printed a note never leaves the host" );
		Assert.IsNull( seen.Items[1].Made, "a token has no serial, so nothing about how it was made is sent" );
	}
}
