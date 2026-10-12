using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Paper: ink stays, a form's blanks are filled in once, and a stamp goes on top of what is written by then.</summary>
[TestClass]
public sealed class PapersTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 11, 12, 0, 0, TimeSpan.Zero );
	private static readonly string[] Card = { "Name", "Occupation" };
	private const string OfficeStamp = "items/office_stamp.item";

	private static ItemStack Sheet( int count = 1 ) => new() { Id = Guid.NewGuid(), Definition = "items/paper.item", Width = 1, Height = 1, Count = count, Max = 10 };

	private static CharacterData Holder( int width = 3, int height = 1 ) =>
		new() { Id = Guid.NewGuid(), Inventory = new InventoryData { Width = width, Height = height } };

	[TestMethod]
	public void InkStaysAndMoreGoesBelowUntilTheSheetIsFull()
	{
		var sheet = Sheet();
		Assert.IsTrue( Papers.Write( sheet, 2, "  Meet me   at the\tbridge.\n " ).Ok );
		CollectionAssert.AreEqual( new[] { "Meet me at the bridge." }, sheet.Paper!.Lines, "one line, its spacing tidied" );
		Assert.AreEqual( 1, sheet.Max, "a written sheet piles with nothing" );
		Assert.IsTrue( Papers.Write( sheet, 2, "Come alone." ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, Papers.Write( sheet, 2, "P.S." ).Code, "two lines fill a sheet that takes two" );
		CollectionAssert.AreEqual( new[] { "Meet me at the bridge.", "Come alone." }, sheet.Paper.Lines, "what was written stays as it was" );

		Assert.AreEqual( ErrorCode.Invalid, Papers.Write( Sheet(), 2, " \n\t " ).Code, "nothing to write" );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Write( Sheet(), 2, new string( 'a', Papers.LineLength + 1 ) ).Code );
		Assert.IsTrue( Papers.Write( Sheet(), 2, new string( 'a', Papers.LineLength ) ).Ok );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Write( Sheet(), 0, "words" ).Code, "a form has no lines to write on" );
		var pile = Sheet( 3 );
		Assert.AreEqual( ErrorCode.Conflict, Papers.Write( pile, 2, "words" ).Code, "never a whole pile at once" );
		Assert.IsNull( pile.Paper );
		Assert.AreEqual( 10, pile.Max, "a refused pen leaves a pile as it was" );
	}

	[TestMethod]
	public void EachBlankOnAFormIsFilledInOnce()
	{
		var card = Sheet();
		Assert.IsTrue( Papers.Fill( card, Card, "name", "Jane   Roe" ).Ok, "a blank is found whatever the case" );
		Assert.AreEqual( "Jane Roe", card.Paper!.Fields["Name"], "kept under the label as printed" );
		Assert.AreEqual( ErrorCode.Conflict, Papers.Fill( card, Card, "Name", "John Doe" ).Code, "ink stays" );
		Assert.AreEqual( "Jane Roe", card.Paper.Fields["Name"] );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Fill( card, Card, "Rank", "General" ).Code, "no such blank" );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Fill( card, Card, null, "General" ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Fill( card, Card, "Occupation", "  " ).Code );
		Assert.AreEqual( ErrorCode.Invalid, Papers.Fill( card, Card, "Occupation", new string( 'a', Papers.FieldLength + 1 ) ).Code );
		Assert.IsTrue( Papers.Fill( card, Card, "Occupation", "Clerk" ).Ok );
		Assert.HasCount( 2, card.Paper.Fields );
		Assert.IsEmpty( card.Paper.Lines, "a blank is not a line" );
	}

	[TestMethod]
	public void AStampGoesOnTopOfWhatIsWrittenByThen()
	{
		var letter = Sheet();
		Assert.IsTrue( Papers.Write( letter, 4, "Pass the bearer." ).Ok );
		Assert.IsTrue( Papers.Stamp( letter, OfficeStamp ).Ok );
		Assert.IsTrue( Papers.Write( letter, 4, "And his friend." ).Ok );
		var mark = letter.Paper!.Stamps.Single();
		Assert.AreEqual( OfficeStamp, mark.Stamp );
		Assert.AreEqual( 1, mark.AfterLine, "the second line went on after the stamp, so it lies below it" );

		Assert.AreEqual( ErrorCode.Invalid, Papers.Stamp( letter, "" ).Code );
		var blank = Sheet();
		Assert.IsTrue( Papers.Stamp( blank, OfficeStamp ).Ok, "a blank sheet can be stamped" );
		Assert.AreEqual( 0, blank.Paper!.Stamps[0].AfterLine );
		Assert.AreEqual( 1, blank.Max, "stamped, it is one of a kind" );
		for ( var more = 1; more < Papers.MaximumStamps; more++ ) Assert.IsTrue( Papers.Stamp( blank, OfficeStamp ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, Papers.Stamp( blank, OfficeStamp ).Code, "there is only so much room" );
		Assert.AreEqual( ErrorCode.Conflict, Papers.Stamp( Sheet( 2 ), OfficeStamp ).Code, "never a whole pile at once" );
	}

	[TestMethod]
	public void ASheetIsHeldUpForSomeoneCloseAndAwake()
	{
		Assert.IsNull( Papers.ToShow( false, true, 60f, 120f, false ) );
		Assert.AreEqual( "You cannot do that now.", Papers.ToShow( true, true, 60f, 120f, false ) );
		Assert.AreEqual( "There is nobody there.", Papers.ToShow( false, false, 0f, 120f, false ) );
		Assert.AreEqual( "You are too far away.", Papers.ToShow( false, true, 121f, 120f, false ) );
		Assert.AreEqual( "They are in no state to read it.", Papers.ToShow( false, true, 60f, 120f, true ) );
	}

	[TestMethod]
	public void WordsGoOnPaperAsOneTidyLine()
	{
		Assert.AreEqual( string.Empty, Papers.Clean( null ) );
		Assert.AreEqual( string.Empty, Papers.Clean( " \r\n\t" ) );
		Assert.AreEqual( "a b c", Papers.Clean( "\ta\r\nb  \u0007 c " ), "breaks, tabs and bells become single spaces" );
		Assert.AreEqual( "Grüße, Fräulein!", Papers.Clean( "Grüße,  Fräulein!" ) );
	}

	[TestMethod]
	public void WhatIsOnASheetGoesWithItAndASheetWithAnythingOnItPilesWithNothing()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder();
		var bob = Holder();
		var pile = transfers.Issue( Sources.Operator, alice, "items/paper.item", 1, 1, Actor.Console, count: 3, max: 10 ).Value;
		var note = transfers.Split( alice, pile.Id, 1, Actor.Console ).Value;
		Assert.IsTrue( Papers.Write( note, 4, "I owe Bob ten marks." ).Ok );

		Assert.IsTrue( transfers.Move( alice, bob, note.Id, Actor.Of( alice ) ).Ok );
		var held = bob.Inventory.Items.Single();
		Assert.AreEqual( note.Id, held.Id );
		CollectionAssert.AreEqual( new[] { "I owe Bob ten marks." }, held.Paper!.Lines, "the writing goes with the sheet" );

		var blank = transfers.Split( alice, pile.Id, 1, Actor.Console ).Value;
		Assert.IsTrue( transfers.Move( alice, bob, blank.Id, Actor.Of( alice ) ).Ok );
		Assert.HasCount( 2, bob.Inventory.Items, "a blank sheet does not pile onto a written one" );
		Assert.AreEqual( ErrorCode.Conflict, transfers.Merge( bob, blank.Id, note.Id, Actor.Of( bob ) ).Code );

		Assert.IsTrue( Papers.Write( held, 4, "Paid." ).Ok );
		Assert.HasCount( 1, note.Paper!.Lines, "what was handed on is its own copy, not the one left behind" );
	}

	[TestMethod]
	public void AnInventoryIsShownWithoutWhatIsOnItsSheets()
	{
		var transfers = new Transfers( new Journal( new MemoryFiles(), () => Now ) );
		var alice = Holder();
		var card = transfers.Issue( Sources.Arrival, alice, "items/card.item", 1, 1, Actor.Console ).Value;
		Assert.IsTrue( Papers.Fill( card, Card, "Name", "Jane Roe" ).Ok );
		var ration = transfers.Issue( Sources.Operator, alice, "items/ration.item", 1, 1, Actor.Console ).Value;

		var seen = Seen.Inventory( alice.Inventory );
		var shown = seen.Items.Single( value => value.Id == card.Id );
		Assert.IsNull( shown.Paper, "what is on a sheet is read a sheet at a time" );
		Assert.AreEqual( (card.Definition, card.X, card.Y, card.Max), (shown.Definition, shown.X, shown.Y, shown.Max), "what it is and where it lies are seen" );
		Assert.AreEqual( "Jane Roe", card.Paper!.Fields["Name"], "the host's copy keeps it" );
		Assert.AreSame( ration, seen.Items.Single( value => value.Id == ration.Id ), "nothing hidden, nothing copied" );
	}
}
