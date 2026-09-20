using System.Numerics;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

[TestClass]
public sealed class MovementTests
{
	private const float Speed = 480f;

	[TestMethod]
	public void RunningIsBelievedAndATeleportIsNot()
	{
		var audit = new MovementAudit();
		var now = 0.0;
		var position = new Vector3( 0, 0, 0 );
		Assert.IsTrue( audit.Observe( new Vector3( 9000, 0, 0 ), now, Speed ), "before the host has placed it, a claim is not punished" );
		Assert.AreEqual( default, audit.Position, "and is not believed either" );
		audit.Reset( position, now, 0 );

		for ( var step = 0; step < 40; step++ )
		{
			now += MovementAudit.Window;
			position.X += Speed * (float)MovementAudit.Window;
			Assert.IsTrue( audit.Observe( position, now, Speed ), $"step {step}" );
		}
		Assert.AreEqual( position, audit.Position );

		now += MovementAudit.Window;
		Assert.IsFalse( audit.Observe( position + new Vector3( 0, 2000, 0 ), now, Speed ), "across the map in a quarter second" );
		Assert.AreEqual( position, audit.Position, "the host still has the character where it was" );
	}

	[TestMethod]
	public void SomethingDroppedFromARoofIsBelievedAndSomethingThatPassesThroughItIsNot()
	{
		var falling = new MovementAudit();
		falling.Reset( new Vector3( 0, 0, 400 ), 0, 0 );
		double now = 0;
		float height = 400, speed = 0;
		while ( height > 0 )
		{
			now += MovementAudit.Window;
			speed += MovementAudit.Gravity * (float)MovementAudit.Window;
			height = System.MathF.Max( 0, height - speed * (float)MovementAudit.Window );
			Assert.IsTrue( falling.Observe( new Vector3( 0, 0, height ), now, Speed ), $"falling past {height}" );
		}

		var cheat = new MovementAudit();
		cheat.Reset( new Vector3( 0, 0, 400 ), 0, 0 );
		Assert.IsFalse( cheat.Observe( new Vector3( 60, 0, 60 ), MovementAudit.Window, Speed ), "340 units down in a quarter second is not a fall" );
		Assert.AreEqual( 400f, cheat.Position.Z );
		// After a refusal the host puts the character back, and a drop is then expected. Whether a floor
		// is in the way of that drop is something only the engine can see: Player traces for it.
	}

	[TestMethod]
	public void SteppingOffALedgeIsBelievedAndFlightIsNot()
	{
		var audit = new MovementAudit();
		audit.Reset( new Vector3( 0, 0, 1000 ), 0, 0 );

		Assert.IsTrue( audit.Observe( new Vector3( 40, 0, 950 ), MovementAudit.Window, Speed ), "gravity is not the player's doing" );
		Assert.IsFalse( audit.Observe( new Vector3( 40, 0, 2000 ), MovementAudit.Window * 2, Speed ), "flight is" );
	}

	[TestMethod]
	public void AHostTeleportIsNotAFreeOneForTheClient()
	{
		var audit = new MovementAudit();
		audit.Reset( new Vector3( 0, 0, 0 ), 0, 0 );
		audit.Reset( new Vector3( 5000, 0, 0 ), 1.0 );

		Assert.IsTrue( audit.Observe( new Vector3( 10, 0, 0 ), 1.5, Speed ), "a claim from the old place, before the owner applied it, is ignored" );
		Assert.AreEqual( new Vector3( 5000, 0, 0 ), audit.Position, "ignored, not accepted" );
		Assert.IsTrue( audit.Observe( new Vector3( -9000, 0, 0 ), 1.8, Speed ), "so is a claim from anywhere else" );
		Assert.AreEqual( new Vector3( 5000, 0, 0 ), audit.Position );

		Assert.IsTrue( audit.Observe( new Vector3( 5010, 0, 0 ), 2.1, Speed ), "the owner arrived" );
		Assert.AreEqual( new Vector3( 5010, 0, 0 ), audit.Position );
		Assert.IsFalse( audit.Observe( new Vector3( 10, 0, 0 ), 2.4, Speed ), "and arriving ends the grace" );

		audit.Reset( new Vector3( 0, 0, 0 ), 3.0 );
		Assert.IsTrue( audit.Observe( new Vector3( 300, 0, 0 ), 3.5, Speed ), "ignored while settling" );
		Assert.IsFalse( audit.Observe( new Vector3( 300, 0, 0 ), 5.2, Speed ), "a long wait does not turn a short teleport into a walk" );
		Assert.AreEqual( new Vector3( 0, 0, 0 ), audit.Position );

		Assert.IsFalse( audit.Observe( new Vector3( 5000, 0, 0 ), 5.5, Speed ), "an owner who never arrives is judged once the grace runs out" );
	}
}
