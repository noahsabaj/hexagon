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
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Heal( character, 50 ).Code, "a bandage does not raise the fallen" );
		Assert.IsTrue( Vitals.Revive( character ).Ok );
		Assert.AreEqual( Vitals.Revived, character.Health );
		Assert.IsFalse( character.IsDown );

		Assert.IsTrue( Vitals.Heal( character, 500 ).Ok );
		Assert.AreEqual( Vitals.Full, character.Health );
		Assert.AreEqual( ErrorCode.Conflict, Vitals.Heal( character, 10 ).Code, "nothing to treat, so the bandage is not used up" );
	}

	[TestMethod]
	public void ACharacterWhoLeftWhileDownIsStillDownWhenItReturns()
	{
		var files = new MemoryFiles();
		var roster = new CharacterRoster( new DocumentStore( files ) );
		var character = roster.Create( 1, "John Doe", "A tired resident of the city.", "citizen", default ).Value;
		Vitals.Damage( character, 100 );
		character.IsRestrained = true;
		roster.Save( character );

		var returned = new CharacterRoster( new DocumentStore( files.Reboot() ) ).Find( character.Id )!;
		Assert.IsTrue( returned.IsDown );
		Assert.IsTrue( returned.IsRestrained );
	}
}
