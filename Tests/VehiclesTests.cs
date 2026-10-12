using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Trucks: a tank that only fills by the can and drains by the mile, and harm only at speed.</summary>
[TestClass]
public sealed class VehiclesTests
{
	[TestMethod]
	public void DrivingBurnsFuelByDistanceDownToEmpty()
	{
		Assert.AreEqual( 19d, Vehicles.Burn( 20, 1000, 0.001 ), 1e-9, "a thousand units at one per thousand" );
		Assert.AreEqual( 0d, Vehicles.Burn( 0.5, 1000, 0.001 ), "never below empty" );
		Assert.AreEqual( 20d, Vehicles.Burn( 20, -50, 0.001 ), "standing still burns nothing" );
	}

	[TestMethod]
	public void ACanGoesInWholeOrNotAtAll()
	{
		Assert.AreEqual( 40d, Vehicles.Fill( 20, 60, 20 ).Value, "a can into a tank with room" );
		Assert.AreEqual( 60d, Vehicles.Fill( 40, 60, 20 ).Value, "to the brim" );
		var over = Vehicles.Fill( 50, 60, 20 );
		Assert.IsFalse( over.Ok, "a tank without room for the whole can takes none of it" );
		Assert.AreEqual( Vehicles.TankFull, over.Message );
		Assert.IsFalse( Vehicles.Fill( 20, 60, 0 ).Ok, "something that is not fuel fills nothing" );
	}

	[TestMethod]
	public void ATruckHurtsOnlyAtSpeedAndBreaksALegWhenFast()
	{
		Assert.IsNull( Vehicles.Strike( Vehicles.SafeSpeed, Places.LeftLeg ), "a nudge at a walk only pushes" );
		var slow = Vehicles.Strike( 250, Places.LeftLeg )!.Value;
		Assert.AreEqual( 20, slow.Amount, "harm by how much faster than a walk" );
		Assert.IsFalse( slow.Wounding, "and below wounding speed, a bruise" );
		Assert.AreEqual( Places.Body, slow.Place );
		var fast = Vehicles.Strike( 600, Places.RightLeg )!.Value;
		Assert.AreEqual( 90, fast.Amount );
		Assert.IsTrue( fast.Wounding, "fast, it breaks the leg it meets" );
		Assert.AreEqual( Places.RightLeg, fast.Place );
		Assert.IsFalse( fast.Lethal, "a truck never kills outright" );
	}

	[TestMethod]
	public void ADriversHandsComeInQuarters()
	{
		Assert.AreEqual( 1f, Vehicles.Control( 0.93f ) );
		Assert.AreEqual( -0.5f, Vehicles.Control( -0.4f ) );
		Assert.AreEqual( 1f, Vehicles.Control( 7f ), "nothing past all the way" );
		Assert.AreEqual( 0f, Vehicles.Control( float.NaN ), "and nothing from nonsense" );
	}

	[TestMethod]
	public void AWheelsSpringPushesUpAndItsTyreHoldsTheRoadUpToItsGrip()
	{
		Assert.AreEqual( 100f, Vehicles.Spring( 10f, 2f, 20f, 10f, 0f ), 1e-4, "pressed in halfway" );
		Assert.AreEqual( 80f, Vehicles.Spring( 10f, 2f, 20f, 10f, 10f ), 1e-4, "less while it springs back out" );
		Assert.AreEqual( 0f, Vehicles.Spring( 10f, 2f, 20f, 25f, 0f ), "a wheel off the ground pulls nothing down" );
		Assert.AreEqual( -50f, Vehicles.Hold( 10f, 5f, 1f, 1000f ), 1e-4, "enough to stop the slide" );
		Assert.AreEqual( -30f, Vehicles.Hold( 10f, 5f, 1f, 30f ), 1e-4, "but no more than the grip, so it slides" );
	}
}
