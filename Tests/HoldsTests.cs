using System.Numerics;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Moving someone who cannot move themselves: dragged, carried, or led by the arm.</summary>
[TestClass]
public sealed class HoldsTests
{
	private static Result Take( HoldKind kind, HoldKind holding = HoldKind.None, bool seated = false, bool handFull = false, bool limp = true, bool bound = false, bool taken = false ) =>
		Holds.ToTake( kind, holding, seated, handFull, limp, bound, taken );

	[TestMethod]
	public void TheDeadAndTheDownAreDraggedOrCarriedAndTheBoundAreLed()
	{
		Assert.IsTrue( Take( HoldKind.Drag ).Ok, "a body on the ground is dragged" );
		Assert.IsTrue( Take( HoldKind.Carry ).Ok, "or carried" );
		Assert.AreEqual( Holds.OnTheirFeet, Take( HoldKind.Drag, limp: false, bound: true ).Message, "someone standing is not dragged, bound or not" );
		Assert.AreEqual( Holds.OnTheirFeet, Take( HoldKind.Carry, limp: false ).Message );

		Assert.IsTrue( Take( HoldKind.Lead, limp: false, bound: true ).Ok, "someone standing bound is led by the arm" );
		Assert.AreEqual( Holds.NotBound, Take( HoldKind.Lead, limp: false ).Message, "someone free walks where they like" );
		Assert.AreEqual( ErrorCode.Conflict, Take( HoldKind.Lead, bound: true ).Code, "someone down cannot be walked anywhere" );
		Assert.AreEqual( ErrorCode.Invalid, Take( HoldKind.None ).Code );
	}

	[TestMethod]
	public void OnePairOfHandsHoldsOneBodyAndCarryingTakesBoth()
	{
		Assert.AreEqual( Holds.AlreadyHolding, Take( HoldKind.Drag, holding: HoldKind.Drag ).Message, "nobody drags two" );
		Assert.AreEqual( Holds.AlreadyHolding, Take( HoldKind.Lead, holding: HoldKind.Carry, limp: false, bound: true ).Message );
		Assert.AreEqual( Holds.Taken, Take( HoldKind.Carry, taken: true ).Message, "a body someone else has hold of is theirs to let go of" );
		Assert.AreEqual( Holds.Sitting, Take( HoldKind.Drag, seated: true ).Message );

		Assert.AreEqual( Holds.BothHands, Take( HoldKind.Carry, handFull: true ).Message, "a body over the shoulder takes both hands" );
		Assert.IsTrue( Take( HoldKind.Drag, handFull: true ).Ok, "dragging takes one, whatever is in the other" );
		Assert.IsTrue( Take( HoldKind.Lead, handFull: true, limp: false, bound: true ).Ok );
		Assert.IsFalse( Holds.LeavesAHand( HoldKind.Carry ) );
		Assert.IsTrue( Holds.LeavesAHand( HoldKind.Drag ) && Holds.LeavesAHand( HoldKind.Lead ) && Holds.LeavesAHand( HoldKind.None ) );
	}

	[TestMethod]
	public void DraggingIsAsSlowAsALimpAndNothingHeldIsEverRun()
	{
		const float walk = 110f, slow = 60f;
		Assert.AreEqual( slow, Holds.Pace( HoldKind.Drag, walk, slow ) );
		Assert.AreEqual( walk, Holds.Pace( HoldKind.Carry, walk, slow ) );
		Assert.AreEqual( walk, Holds.Pace( HoldKind.Lead, walk, slow ) );
		Assert.AreEqual( 40f, Holds.Pace( HoldKind.Drag, 40f, slow ), "never faster than walking" );
		Assert.IsTrue( float.IsPositiveInfinity( Holds.Pace( HoldKind.None, walk, slow ) ), "holding nobody slows nobody" );
	}

	[TestMethod]
	public void ABedTakesBodiesUntilItIsFull()
	{
		Assert.IsTrue( Holds.ToLayIn( HoldKind.Carry, 0, 2 ).Ok );
		Assert.IsTrue( Holds.ToLayIn( HoldKind.Drag, 1, 2 ).Ok );
		Assert.AreEqual( Holds.NoRoom, Holds.ToLayIn( HoldKind.Carry, 2, 2 ).Message );
		Assert.AreEqual( Holds.NothingToLay, Holds.ToLayIn( HoldKind.Lead, 0, 2 ).Message, "someone led walks; they are not laid anywhere" );
		Assert.AreEqual( Holds.NothingToLay, Holds.ToLayIn( HoldKind.None, 0, 2 ).Message );
		Assert.AreEqual( Holds.NoRoom, Holds.ToLayIn( HoldKind.Carry, 0, 0 ).Message, "a bed with no room for bodies takes none" );
	}

	[TestMethod]
	public void TheHandsAreWhereTheMoverStandsAndFaces()
	{
		var feet = new Vector3( 100, 200, 10 );
		AssertNear( new Vector3( 140, 200, 40 ), Holds.Hand( feet, 0f ), "an arm's length ahead, at the hip" );
		AssertNear( new Vector3( 100, 240, 40 ), Holds.Hand( feet, 90f ), "turned, the hand goes round with it" );
		AssertNear( new Vector3( 100, 192, 74 ), Holds.Shoulder( feet, 0f ), "the right shoulder, facing x, is toward -y" );
		AssertNear( new Vector3( 108, 200, 74 ), Holds.Shoulder( feet, 90f ) );
		AssertNear( new Vector3( 120, 224, 10 ), Holds.Beside( feet, 0f ), "someone led walks a little ahead, at the left hand" );

		Assert.IsFalse( Holds.Strays( new Vector3( 140, 200, 40 ), Holds.Hand( feet, 0f ) ) );
		Assert.IsTrue( Holds.Strays( new Vector3( 140, 200 + Holds.Stray + 1f, 40 ), Holds.Hand( feet, 0f ) ), "caught on something the mover walked past" );
	}

	private static void AssertNear( Vector3 expected, Vector3 actual, string? message = null ) =>
		Assert.IsLessThan( 0.01f, Vector3.Distance( expected, actual ), $"{message}: expected {expected}, was {actual}" );
}
