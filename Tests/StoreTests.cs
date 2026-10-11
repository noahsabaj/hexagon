using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

[TestClass]
public sealed class StoreTests
{
	[TestMethod]
	public void SavedDocumentsRoundTripAndLeaveNoPendingCopy()
	{
		var files = new MemoryFiles();
		var store = new DocumentStore( files );
		store.Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 7 } );

		Assert.AreEqual( 7, store.Load<CharacterData>( "characters/a.json" )!.Tokens );
		Assert.IsFalse( files.Exists( "characters/a.json" + DocumentStore.PendingSuffix ) );
	}

	[TestMethod]
	public void ACrashAtAnyPointOfASaveLeavesTheOldOrTheNewDocumentNeverNeither()
	{
		// A save is two writes. Tear each one in turn and reboot.
		for ( var crashAt = 1; crashAt <= 2; crashAt++ )
		{
			var files = new MemoryFiles();
			new DocumentStore( files ).Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 1 } );
			files.CrashOnWrite = files.Writes + crashAt;
			Assert.ThrowsExactly<InvalidOperationException>( () =>
				new DocumentStore( files ).Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 2 } ) );

			var recovered = new DocumentStore( files.Reboot() ).Load<CharacterData>( "characters/a.json" );

			Assert.IsNotNull( recovered, $"crash at write {crashAt} lost the document" );
			// Tearing the sibling keeps the old state; tearing the real file recovers the new one.
			Assert.AreEqual( crashAt == 1 ? 1 : 2, recovered.Tokens, $"crash at write {crashAt}" );
		}
	}

	[TestMethod]
	public void ACrashAnywhereInASaveTogetherKeepsAllOfTheChangeOrNoneOfIt()
	{
		// A sale moves tokens one way and goods the other, so no order of two separate saves is safe.
		// Saved together it is five writes (the change, then each document's sibling and real file)
		// and three deletes. Lose power at each in turn and reboot.
		const int Writes = 5, Deletes = 3;
		for ( var crash = 1; crash <= Writes + Deletes; crash++ )
		{
			var files = new MemoryFiles();
			var store = new DocumentStore( files );
			store.Save( "characters/a.json", new CharacterData { Name = "Alice", Tokens = 10 } );
			store.Save( "holders/v.json", new HolderData { Kind = HolderKinds.Vendor } );
			if ( crash <= Writes ) files.CrashOnWrite = files.Writes + crash;
			else files.CrashOnDelete = files.Deletes + crash - Writes;

			Assert.ThrowsExactly<InvalidOperationException>( () => store.SaveTogether(
				("characters/a.json", new CharacterData { Name = "Alice", Tokens = 0 }),
				("holders/v.json", new HolderData { Kind = HolderKinds.Vendor, Tokens = 10 }) ), $"crash {crash}" );

			var rebooted = files.Reboot();
			var after = new DocumentStore( rebooted );
			var alice = after.Load<CharacterData>( "characters/a.json" )!;
			var vendor = after.Load<HolderData>( "holders/v.json" )!;
			Assert.AreEqual( 10, alice.Tokens + vendor.Tokens, $"crash {crash} made or lost tokens" );
			Assert.AreEqual( crash == 1 ? 10 : 0, alice.Tokens, $"crash {crash}: only a torn change file is no change" );
			Assert.IsFalse( rebooted.Files.Keys.Any( path => path.StartsWith( DocumentStore.TogetherFolder + "/" ) ), $"crash {crash} left a change behind" );
		}
	}

	[TestMethod]
	public void AHalfWrittenChangeIsFinishedBeforeAnythingIsLoaded()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var alice = roster.Create( 1, "Alice Smith", "A tired resident of the city.", "citizen", default ).Value;
		var bob = roster.Create( 2, "Bob Jones", "A tired resident of the city.", "citizen", default ).Value;
		alice.Tokens = 10;
		roster.Save( alice );

		// Alice's new state is written and power goes before Bob's: on disk she has paid and he has nothing.
		alice.Tokens = 0;
		bob.Tokens = 10;
		files.CrashOnWrite = files.Writes + 4;
		Assert.ThrowsExactly<InvalidOperationException>( () => new DocumentStore( files ).SaveTogether(
			(CharacterRoster.PathOf( alice.Id ), alice), (CharacterRoster.PathOf( bob.Id ), bob) ) );

		var reloaded = new CharacterRoster( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( (0L, 10L), (reloaded.Find( alice.Id )!.Tokens, reloaded.Find( bob.Id )!.Tokens) );
	}

	[TestMethod]
	public void LoadAllFindsADocumentWhoseOnlyCompleteCopyIsPending()
	{
		var files = new MemoryFiles();
		var store = new DocumentStore( files );
		store.Save( "characters/a.json", new CharacterData { Name = "Alice" } );
		files.Files["characters/b.json" + DocumentStore.PendingSuffix] = files.Files["characters/a.json"].Replace( "Alice", "Bob" );

		var all = store.LoadAll<CharacterData>( "characters" );

		Assert.HasCount( 2, all );
		Assert.IsTrue( files.Exists( "characters/b.json" ), "the recovered document is promoted to its real path" );
	}

	[TestMethod]
	public void AnUnreadableDocumentIsSkippedWithAWarningInsteadOfStoppingTheServer()
	{
		var files = new MemoryFiles();
		files.Files["characters/bad.json"] = "{ not json";
		var warnings = 0;

		var all = new DocumentStore( files, _ => warnings++ ).LoadAll<CharacterData>( "characters" );

		Assert.HasCount( 0, all );
		Assert.AreEqual( 1, warnings );
	}
}
