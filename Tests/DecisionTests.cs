using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>The decisions the host makes from what it knows, without the engine: who may enter, what can be done to a person, when an offer lapses, how staff are understood.</summary>
[TestClass]
public sealed class DecisionTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 11, 12, 0, 0, TimeSpan.Zero );
	private const long Mine = 7;

	private static CharacterData Character( bool lived = true ) => new()
	{
		Id = Guid.NewGuid(), SteamId = Mine, Name = "John Doe", FirstEntered = lived ? Now.AddDays( -1 ) : null
	};

	[TestMethod]
	public void SomeoneElsesCharacterIsNotFoundAsIfItDidNotExist()
	{
		var theirs = Character();
		theirs.SteamId = 8;
		foreach ( var refusal in new[]
		{
			Admission.ToEnter( theirs, Mine, Now, inCity: false, loadedElsewhere: false ),
			Admission.ToRetire( theirs, Mine, loaded: false ),
			Admission.ToDelete( theirs, Mine ),
			Admission.ToEnter( null, Mine, Now, inCity: false, loadedElsewhere: false ),
		} )
			Assert.AreEqual( Admission.NotFound, refusal );
	}

	[TestMethod]
	public void EnteringTheCityIsRefusedToTheDeadTheWaitingTheLoadedAndTheUnlisted()
	{
		var character = Character();
		Assert.IsNull( Admission.ToEnter( character, Mine, Now, inCity: false, loadedElsewhere: false ) );
		Assert.AreEqual( "Leave the city first.", Admission.ToEnter( character, Mine, Now, inCity: true, loadedElsewhere: false ) );
		Assert.AreEqual( "That character is already in the city.", Admission.ToEnter( character, Mine, Now, inCity: false, loadedElsewhere: true ) );

		character.ArrivesAt = Now.AddHours( 5.5 );
		Assert.AreEqual( "John Doe arrives in the city in 6 hours.", Admission.ToEnter( character, Mine, Now, false, false ) );
		character.ArrivesAt = Now.AddSeconds( -1 );
		Assert.IsNull( Admission.ToEnter( character, Mine, Now, false, false ), "a wait that is over is no wait" );

		character.DiedAt = Now;
		Assert.AreEqual( "John Doe is dead.", Admission.ToEnter( character, Mine, Now, false, false ) );
	}

	[TestMethod]
	public void ALifeEndsByRetiringOnlyOnceItHasBeenLivedAndItsSceneIsOver()
	{
		var lived = Character();
		Assert.IsNull( Admission.ToRetire( lived, Mine, loaded: false ) );
		Assert.AreEqual( "Leave the city with that character before retiring it.", Admission.ToRetire( lived, Mine, loaded: true ) );
		lived.IsRestrained = true;
		Assert.AreEqual( "That character cannot be retired while it is down or bound.", Admission.ToRetire( lived, Mine, loaded: false ) );
		Assert.AreEqual( "John Doe has lived in the city. A life is ended by retiring, not by deleting.", Admission.ToDelete( lived, Mine ) );

		var unlived = Character( lived: false );
		Assert.IsNull( Admission.ToDelete( unlived, Mine ) );
		Assert.AreEqual( "John Doe never entered the city, so it is deleted, not retired.", Admission.ToRetire( unlived, Mine, loaded: false ) );
		unlived.DiedAt = Now;
		Assert.AreEqual( "John Doe is already dead.", Admission.ToRetire( unlived, Mine, loaded: false ) );
	}

	[TestMethod]
	public void AWaitIsSaidAsAPersonWouldSayIt()
	{
		Assert.AreEqual( "in 24 hours", Admission.Until( TimeSpan.FromHours( 23.2 ) ) );
		Assert.AreEqual( "in 3 minutes", Admission.Until( TimeSpan.FromMinutes( 2.5 ) ) );
		Assert.AreEqual( "in a minute", Admission.Until( TimeSpan.FromSeconds( 40 ) ) );
	}

	[TestMethod]
	public void WhatCanBeDoneToAPersonFollowsTheStateAnyoneCanSeeThemIn()
	{
		string[] Ids( bool down, bool restrained, string? finish = null ) => PersonVerbs.For( down, restrained, finish ).Select( verb => verb.Id ).ToArray();
		CollectionAssert.AreEqual( new[] { VerbIds.PersonIntroduce, VerbIds.PersonOffer, VerbIds.PersonShow, VerbIds.PersonTreat, VerbIds.PersonRestrain }, Ids( false, false ) );
		CollectionAssert.AreEqual( new[] { VerbIds.PersonSearch, VerbIds.PersonTreat, VerbIds.PersonShow, VerbIds.PersonRelease }, Ids( false, true ) );
		CollectionAssert.AreEqual( new[] { VerbIds.PersonRevive, VerbIds.PersonSearch, VerbIds.PersonFinish }, Ids( true, true ), "down outweighs bound" );

		Assert.AreEqual( Capability.Restrain, PersonVerbs.For( false, false ).Single( verb => verb.Id == VerbIds.PersonRestrain ).Requires );
		Assert.IsNull( PersonVerbs.For( true, false, "" ).Single( verb => verb.Id == VerbIds.PersonFinish ).Requires, "a game that asks nothing lets anyone finish" );
		Assert.AreEqual( "weapon", PersonVerbs.For( true, false, "weapon" ).Single( verb => verb.Id == VerbIds.PersonFinish ).Requires );
	}

	[TestMethod]
	public void AnOfferIsMadeOnlyToSomeoneNearAndFreeAndLapsesWhenThatEnds()
	{
		Assert.IsNull( OfferRules.ToOffer( false, true, 50f, 120f, false, false ) );
		Assert.AreEqual( "You cannot do that now.", OfferRules.ToOffer( true, true, 50f, 120f, false, false ) );
		Assert.AreEqual( "There is nobody there.", OfferRules.ToOffer( false, false, 0f, 120f, false, false ) );
		Assert.AreEqual( "You are too far away.", OfferRules.ToOffer( false, true, 121f, 120f, false, false ) );
		Assert.AreEqual( "They are in no state to take it.", OfferRules.ToOffer( false, true, 50f, 120f, true, false ) );
		Assert.AreEqual( "They are already being offered something.", OfferRules.ToOffer( false, true, 50f, 120f, false, true ) );

		Assert.IsNull( OfferRules.Lapse( 10f, true, false, 50f, 120f ) );
		Assert.AreEqual( "time", OfferRules.Lapse( OfferRules.Seconds + 0.1f, true, false, 50f, 120f ) );
		Assert.AreEqual( "gone", OfferRules.Lapse( 10f, false, false, 0f, 120f ) );
		Assert.AreEqual( "incapable", OfferRules.Lapse( 10f, true, true, 50f, 120f ) );
		Assert.AreEqual( "apart", OfferRules.Lapse( 10f, true, false, 121f, 120f ) );
	}

	[TestMethod]
	public void AStaffLineKeepsAQuotedNameTogetherAndIsWrittenBackAsTyped()
	{
		var words = CommandLine.Words( "give  \"John Doe\" pistol 2" );
		CollectionAssert.AreEqual( new[] { "give", "John Doe", "pistol", "2" }, words );
		Assert.AreEqual( "give \"John Doe\" pistol 2", CommandLine.Line( words ) );
		Assert.AreEqual( "journal \"\" 5", CommandLine.Line( new[] { "journal", "", "5" } ), "an empty word keeps its place" );
	}

	[TestMethod]
	public void StaffTakeWhatIsAskedPileByPileAndStopWhereATakeFails()
	{
		var piles = new[] { new ItemStack { Count = 20 }, new ItemStack { Count = 10 } };
		var taken = new System.Collections.Generic.List<int>();
		Assert.AreEqual( 25, CommandLine.TakeFromPiles( "25", piles, ( _, some ) => { taken.Add( some ); return true; } ) );
		CollectionAssert.AreEqual( new[] { 20, 5 }, taken );
		Assert.AreEqual( 30, CommandLine.TakeFromPiles( "", piles, ( _, _ ) => true ), "no number is all of it" );
		Assert.AreEqual( 20, CommandLine.TakeFromPiles( "", piles, ( pile, _ ) => pile == piles[0] ), "the first refusal stops the rest" );
	}

	[TestMethod]
	public void AnItemPutDownOnAPileThatCanTakeMoreJoinsIt()
	{
		var inventory = new InventoryData { Width = 4, Height = 2 };
		var rounds = new ItemStack { Id = Guid.NewGuid(), X = 1, Y = 0, Width = 2, Height = 1, Count = 5, Max = 30 };
		var single = new ItemStack { Id = Guid.NewGuid(), X = 0, Y = 1, Width = 1, Height = 1, Count = 1, Max = 1 };
		inventory.Items.Add( rounds );
		inventory.Items.Add( single );
		Assert.AreSame( rounds, InventoryGrid.PileAt( inventory, 2, 0, except: Guid.NewGuid() ) );
		Assert.IsNull( InventoryGrid.PileAt( inventory, 2, 0, except: rounds.Id ), "a pile is not put down on itself" );
		Assert.IsNull( InventoryGrid.PileAt( inventory, 0, 1, except: Guid.NewGuid() ), "a thing that does not pile is only moved past" );
		Assert.IsNull( InventoryGrid.PileAt( inventory, 3, 1, except: Guid.NewGuid() ) );
	}
}
