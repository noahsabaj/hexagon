using System;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

[TestClass]
public sealed class RosterTests
{
	private const string Description = "A tired resident of the city.";
	private static readonly DateTimeOffset Now = new( 2026, 9, 18, 0, 0, 0, TimeSpan.Zero );

	[TestMethod]
	public void ACreatedCharacterSurvivesARestart()
	{
		var files = new MemoryFiles();
		var created = new CharacterRoster( new DocumentStore( files ) ).Create( 1, "  John Doe ", Description, Now );

		Assert.IsTrue( created.Ok, created.Message );
		var reloaded = new CharacterRoster( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( "John Doe", reloaded.Find( created.Value.Id )!.Name );
		Assert.IsEmpty( reloaded.Find( created.Value.Id )!.Inventory.Items, "a new character arrives with nothing but its clothes" );
		Assert.IsNull( created.Value.ArrivesAt, "with no death before it, it may enter at once" );
	}

	[TestMethod]
	public void LookAlikeNamesAreRefusedIncludingAfterARestart()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		Assert.IsTrue( roster.Create( 1, "Sara Connor", Description, Now ).Ok );

		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "sara  connor", Description, Now ).Code );
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "5ara Connor", Description, Now ).Code );
		// x01BC is a Latin letter the Unicode table folds onto the digit five.
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 2, "\x01BCara Connor", Description, Now ).Code );
		var restarted = new CharacterRoster( new DocumentStore( files.Reboot() ) );
		Assert.AreEqual( ErrorCode.Conflict, restarted.Create( 2, "Sara Connor", Description, Now ).Code );
	}

	[TestMethod]
	public void MixedScriptAndInvisibleCharactersAreRefused()
	{
		var roster = new CharacterRoster( new DocumentStore( new MemoryFiles() ) );

		// x0430 is the Cyrillic letter that looks like a Latin "a"; x200B is a zero-width space.
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "S\x0430ra Connor", Description, Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Sara\x200BConnor", Description, Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Al", Description, Now ).Code );
		Assert.AreEqual( ErrorCode.Invalid, roster.Create( 1, "Sara Connor", "too short", Now ).Code );
	}

	[TestMethod]
	public void ACharacterDeletedWhileStillHeldIsNotWrittenBack()
	{
		// One account on two connections: one deletes the character the other still has loaded.
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var held = roster.Create( 1, "John Doe", Description, Now ).Value;
		Assert.IsTrue( roster.Delete( 1, held.Id ).Ok );

		held.Health = 50;
		Assert.IsFalse( roster.Save( held ), "the other connection's next save is refused" );
		Assert.IsNull( new CharacterRoster( new DocumentStore( files.Reboot() ) ).Find( held.Id ), "so it stays deleted after a restart" );
		Assert.IsTrue( roster.Create( 1, "John Doe", Description, Now ).Ok, "and its name and its life stay free" );
	}

	[TestMethod]
	public void AnAccountLivesOneLifeAtATimeAndTheNextWaitsADayAfterTheDeath()
	{
		var roster = new CharacterRoster( new DocumentStore( new MemoryFiles() ) );
		var john = roster.Create( 1, "John Doe", Description, Now ).Value;
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 1, "Jane Roe", Description, Now ).Code, "one living character at a time" );
		Assert.IsTrue( roster.Create( 2, "Jane Roe", Description, Now ).Ok, "for each account" );
		Assert.AreEqual( ErrorCode.NotFound, roster.Delete( 2, john.Id ).Code, "another account cannot delete it" );

		john.FirstEntered = Now;
		Assert.AreEqual( ErrorCode.Conflict, roster.Delete( 1, john.Id ).Code, "a character that has lived is not deleted" );
		Vitals.Die( john, Now.AddHours( 1 ) );
		roster.Save( john );

		var next = roster.Create( 1, "Max Muster", Description, Now.AddHours( 2 ) );
		Assert.IsTrue( next.Ok, "the next can be made at once" );
		Assert.AreEqual( Now.AddHours( 25 ), next.Value.ArrivesAt, "and enters a day after the death" );
		Assert.IsTrue( roster.Delete( 1, next.Value.Id ).Ok, "one that never lived can be deleted" );
		Assert.AreEqual( Now.AddHours( 25 ), roster.Create( 1, "Max Muster", Description, Now.AddHours( 3 ) ).Value.ArrivesAt,
			"which frees its name, and making it again does not move the wait" );
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 3, "John Doe", Description, Now ).Code, "the dead keep their names" );
		Assert.IsNull( roster.Create( 4, "Erika Muster", Description, Now ).Value.ArrivesAt );

		var lenient = new LifeRules( 2, TimeSpan.Zero );
		Assert.IsTrue( roster.Create( 4, "Hans Muster", Description, Now, lenient ).Ok, "a game may allow more" );
		Assert.AreEqual( ErrorCode.Conflict, roster.Create( 4, "Fritz Muster", Description, Now, lenient ).Code );
	}

	[TestMethod]
	public void TheDeadAreRestoredOnlyOnceTheirAccountHasRoomForThem()
	{
		var roster = new CharacterRoster( new DocumentStore( new MemoryFiles() ) );
		var john = roster.Create( 1, "John Doe", Description, Now ).Value;
		john.FirstEntered = Now;
		Assert.AreEqual( ErrorCode.Conflict, roster.Restore( john ).Code, "the living cannot be restored" );

		Vitals.Die( john, Now );
		var max = roster.Create( 1, "Max Muster", Description, Now ).Value;
		Assert.AreEqual( ErrorCode.Conflict, roster.Restore( john ).Code, "nor the dead while their account has its living character" );
		Assert.IsTrue( roster.Delete( 1, max.Id ).Ok );

		Assert.IsTrue( roster.Restore( john ).Ok );
		Assert.AreEqual( (false, false, Vitals.Revived), (john.IsDead, john.IsDown, john.Health), "up, barely, as if helped up" );
	}
}
