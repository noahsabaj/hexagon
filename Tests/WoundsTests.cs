using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

[TestClass]
public sealed class WoundsTests
{
	[TestMethod]
	public void AHitboxIsThePlaceItsTagsName()
	{
		// As the engine's citizen and human bodies tag their hitboxes.
		Assert.AreEqual( Places.Head, Places.OfTags( new[] { "head" } ) );
		Assert.AreEqual( Places.Head, Places.OfTags( new[] { "neck", "head" } ), "the neck is the head" );
		Assert.AreEqual( Places.Body, Places.OfTags( new[] { "spine", "chest" } ) );
		Assert.AreEqual( Places.Body, Places.OfTags( new[] { "stomach", "pelvis" } ) );
		Assert.AreEqual( Places.RightArm, Places.OfTags( new[] { "arm", "right", "clavicle" } ), "a shoulder is its arm" );
		Assert.AreEqual( Places.LeftArm, Places.OfTags( new[] { "arm", "left", "lower" } ) );
		Assert.AreEqual( Places.RightArm, Places.OfTags( new[] { "right", "hand" } ) );
		Assert.AreEqual( Places.LeftLeg, Places.OfTags( new[] { "leg", "left", "upper" } ) );
		Assert.AreEqual( Places.RightLeg, Places.OfTags( new[] { "leg", "right", "ankle" } ), "a foot is its leg" );
		Assert.AreEqual( Places.Body, Places.OfTags( new string[0] ), "an untagged hitbox is the body" );
		Assert.AreEqual( Places.Body, Places.OfTags( new[] { "left" } ) );
	}

	[TestMethod]
	public void ALimpBodyIsHitInThePlaceItsBoneIsPartOf()
	{
		// The bones the engine's ragdolls give a body to, as a limp body is hit.
		Assert.AreEqual( Places.Head, Places.OfBone( "head" ) );
		Assert.AreEqual( Places.Head, Places.OfBone( "neck_0" ) );
		Assert.AreEqual( Places.Body, Places.OfBone( "pelvis" ) );
		Assert.AreEqual( Places.Body, Places.OfBone( "spine_2" ) );
		Assert.AreEqual( Places.LeftArm, Places.OfBone( "clavicle_L" ) );
		Assert.AreEqual( Places.RightArm, Places.OfBone( "arm_upper_R" ) );
		Assert.AreEqual( Places.LeftArm, Places.OfBone( "hand_L" ) );
		Assert.AreEqual( Places.RightLeg, Places.OfBone( "leg_lower_R" ) );
		Assert.AreEqual( Places.LeftLeg, Places.OfBone( "ankle_L" ) );
	}

	[TestMethod]
	public void ATreatmentNamesAKindOfWound()
	{
		Assert.AreEqual( Places.Arm, Places.KindOf( Places.LeftArm ) );
		Assert.AreEqual( Places.Leg, Places.KindOf( Places.RightLeg ) );
		Assert.AreEqual( Places.Head, Places.KindOf( Places.Head ) );
		Assert.IsTrue( Wounds.Stays( Places.LeftArm ) );
		Assert.IsFalse( Wounds.Stays( Places.Head ) );
		Assert.IsFalse( Wounds.Stays( Places.Body ) );
	}

	[TestMethod]
	public void WoundsAreSaidAsAPersonWouldSayThem()
	{
		Assert.AreEqual( "no wounds", Wounds.Describe( new string[0] ) );
		Assert.AreEqual( "a wounded left leg", Wounds.Describe( new[] { Places.LeftLeg } ) );
		Assert.AreEqual( "a wounded left leg and right arm", Wounds.Describe( new[] { Places.LeftLeg, Places.RightArm } ) );
		Assert.AreEqual( "a wounded left leg, right leg and right arm", Wounds.Describe( new[] { Places.LeftLeg, Places.RightLeg, Places.RightArm } ) );
	}
}
