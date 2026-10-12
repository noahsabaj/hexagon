using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

[TestClass]
public sealed class VitalsTests
{
	[TestMethod]
	public void HarmPutsACharacterDownAndNeverKills()
	{
		var character = new CharacterData();

		Assert.AreEqual( Harm.Hurt, Vitals.Damage( character, 60 ) );
		Assert.AreEqual( 40, character.Health );
		Assert.AreEqual( Harm.Downed, Vitals.Damage( character, 500 ) );
		Assert.AreEqual( 0, character.Health );
		Assert.IsTrue( character.IsDown );
		Assert.AreEqual( Harm.None, Vitals.Damage( character, 500 ), "someone who is down cannot be hurt further; finishing is its own act" );
		Assert.AreEqual( Harm.None, Vitals.Damage( new CharacterData(), -5 ), "harm is not a way to heal" );
	}

	[TestMethod]
	public void OnlyTheDownedCanBeHelpedUpOrFinished()
	{
		var character = new CharacterData();
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Revive( character ).Code );
		Assert.AreEqual( ErrorCode.Conflict, Vitals.CanFinish( character ).Code );

		Vitals.Damage( character, 100 );
		Assert.IsTrue( Vitals.CanFinish( character ).Ok );
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Treat( character, Bandage, 50 ).Code, "a bandage does not raise the fallen" );
		Assert.IsTrue( Vitals.Revive( character ).Ok );
		Assert.AreEqual( Vitals.Revived, character.Health );
		Assert.IsFalse( character.IsDown );

		Assert.AreEqual( new Treated( null, Vitals.Full - Vitals.Revived ), Vitals.Treat( character, Bandage, 500 ).Value );
		Assert.AreEqual( Vitals.Full, character.Health );
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Treat( character, Bandage, 10 ).Code, "nothing to treat, so the bandage is not used up" );
	}

	private static readonly string[] Bandage = { Places.Arm, Places.Leg };

	[TestMethod]
	public void AWeaponWoundsAnArmOrALegAndTheWoundStaysUntilTreated()
	{
		var character = new CharacterData();
		var shot = Vitals.Strike( character, new Blow( 20, Places.LeftLeg, Wounding: true ) );
		Assert.AreEqual( new Struck( Harm.Hurt, Places.LeftLeg ), shot );
		Assert.IsTrue( Wounds.Limps( character ) );
		Assert.IsFalse( Wounds.CannotHold( character ) );
		Assert.AreEqual( new Struck( Harm.Hurt, null ), Vitals.Strike( character, new Blow( 10, Places.LeftLeg, Wounding: true ) ), "a wounded leg is no more wounded" );
		Assert.AreEqual( new Struck( Harm.Hurt, null ), Vitals.Strike( character, new Blow( 10, Places.RightArm ) ), "a fist bruises" );
		Assert.AreEqual( new Struck( Harm.Hurt, null ), Vitals.Strike( character, new Blow( 10, Places.Body, Wounding: true ) ), "a shot to the body is health alone" );
		Assert.AreEqual( new Struck( Harm.Hurt, Places.RightArm ), Vitals.Strike( character, new Blow( 10, Places.RightArm, Wounding: true ) ) );
		Assert.IsTrue( Wounds.CannotHold( character ) );
		CollectionAssert.AreEqual( new[] { Places.LeftLeg, Places.RightArm }, character.Wounds );
		Assert.AreEqual( 40, character.Health );

		// Getting up does not close a wound, and neither does time.
		Assert.AreEqual( new Struck( Harm.Downed, null ), Vitals.Strike( character, new Blow( 100, Places.LeftLeg, Wounding: true ) ) );
		Vitals.Bleed( character, 60, 0 );
		Vitals.Revive( character );
		CollectionAssert.AreEqual( new[] { Places.LeftLeg, Places.RightArm }, character.Wounds );

		// One wound a use, the oldest first, of the kinds the treatment names.
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Treat( character, new[] { "burn" }, 0 ).Code, "nothing it treats, and nothing to give back" );
		Assert.AreEqual( new Treated( Places.LeftLeg, 10 ), Vitals.Treat( character, Bandage, 10 ).Value );
		Assert.IsFalse( Wounds.Limps( character ) );
		Assert.AreEqual( new Treated( Places.RightArm, 0 ), Vitals.Treat( character, new[] { Places.Arm }, 0 ).Value );
		Assert.IsEmpty( character.Wounds );
	}

	[TestMethod]
	public void AWeaponMadeToKillKillsWithAShotToTheHeadStandingOrDown()
	{
		Assert.AreEqual( Harm.Killed, Vitals.Strike( new CharacterData(), new Blow( 1, Places.Head, Wounding: true, Lethal: true ) ).Harm );
		Assert.AreEqual( Harm.Hurt, Vitals.Strike( new CharacterData(), new Blow( 20, Places.Head, Wounding: true ) ).Harm, "a club to the head is a blow like any other" );
		Assert.AreEqual( Harm.Hurt, Vitals.Strike( new CharacterData(), new Blow( 20, Places.Body, Wounding: true, Lethal: true ) ).Harm, "only the head" );

		var down = new CharacterData();
		Vitals.Damage( down, 100 );
		Assert.AreEqual( Harm.None, Vitals.Strike( down, new Blow( 20, Places.LeftLeg, Wounding: true, Lethal: true ) ).Harm );
		Assert.IsEmpty( down.Wounds, "someone down is not wounded further" );
		Assert.AreEqual( Harm.Killed, Vitals.Strike( down, new Blow( 20, Places.Head, Wounding: true, Lethal: true ) ).Harm );
		Assert.AreEqual( Harm.None, Vitals.Strike( down, new Blow( 0, Places.Head, Wounding: true, Lethal: true ) ).Harm, "a blow of nothing is nothing" );
	}

	[TestMethod]
	public void TheBadlyHurtAndTheDownBreatheHard()
	{
		var character = new CharacterData();
		Assert.IsFalse( Vitals.BreathesHard( character ) );
		Vitals.Damage( character, Vitals.Full - Vitals.BadlyHurt );
		Assert.IsFalse( Vitals.BreathesHard( character ), "half is not yet below half" );
		Vitals.Damage( character, 1 );
		Assert.IsTrue( Vitals.BreathesHard( character ) );
		Vitals.Damage( character, 100 );
		Assert.IsTrue( Vitals.BreathesHard( character ) );
		Vitals.Die( character, default );
		Assert.IsFalse( Vitals.BreathesHard( character ), "the dead do not breathe" );
	}

	[TestMethod]
	public void ComingBackWholeLeavesNoWounds()
	{
		var character = new CharacterData();
		Vitals.Strike( character, new Blow( 10, Places.LeftArm, Wounding: true ) );
		Vitals.Respawn( character );
		Assert.IsEmpty( character.Wounds );
	}

	[TestMethod]
	public void TimeDownCarriesOverLeavingAndCountsOnlyWhileTheCityRuns()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var character = roster.Create( 1, "John Doe", "A tired resident of the city.", default ).Value;
		Assert.IsFalse( Vitals.Bleed( character, 100, 180 ), "someone standing does not bleed" );
		Assert.AreEqual( 0, character.SecondsDown );
		Vitals.Damage( character, 100 );
		Assert.IsFalse( Vitals.Bleed( character, 120, 180 ) );
		roster.Save( character );

		// The player leaves and the server restarts. Coming back does not start the clock again.
		var returned = new CharacterRoster( new DocumentStore( files.Reboot() ) ).Find( character.Id )!;
		Assert.AreEqual( 120, returned.SecondsDown );
		Assert.IsFalse( Vitals.Bleed( returned, 59, 180 ) );
		Assert.IsTrue( Vitals.Bleed( returned, 1, 180 ), "three minutes down in all" );
		Assert.IsFalse( Vitals.Bleed( returned, 1000, 0 ), "with no limit it waits for someone to act" );

		Assert.IsTrue( Vitals.Revive( returned ).Ok );
		Assert.AreEqual( 0, returned.SecondsDown, "the next fall starts from nothing" );
	}

	[TestMethod]
	public void ACharacterWhoLeftWhileDownIsStillDownWhenItReturns()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var character = roster.Create( 1, "John Doe", "A tired resident of the city.", default ).Value;
		Vitals.Damage( character, 100 );
		character.IsRestrained = true;
		roster.Save( character );

		var returned = new CharacterRoster( new DocumentStore( files.Reboot() ) ).Find( character.Id )!;
		Assert.IsTrue( returned.IsDown );
		Assert.IsTrue( returned.IsRestrained );
	}

	[TestMethod]
	public void WoundsAreKeptWithTheCharacter()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var character = roster.Create( 1, "John Doe", "A tired resident of the city.", default ).Value;
		Vitals.Strike( character, new Blow( 10, Places.RightLeg, Wounding: true ) );
		roster.Save( character );

		var returned = new CharacterRoster( new DocumentStore( files.Reboot() ) ).Find( character.Id )!;
		CollectionAssert.AreEqual( new[] { Places.RightLeg }, returned.Wounds );
	}
}
