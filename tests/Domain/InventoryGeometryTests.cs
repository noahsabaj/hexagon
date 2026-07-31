#nullable enable

using Hexagon.V2.Domain;

namespace Hexagon.V2.Tests.Domain;

[TestClass]
public sealed class InventoryGeometryTests
{
	[TestMethod]
	public void RectangleEndpointsUseWidenedArithmeticAtIntegerExtrema()
	{
		var rectangle = new InventoryRectangle(
			new InventoryGridPosition( int.MaxValue, int.MaxValue ),
			new InventoryGridSize( int.MaxValue, int.MaxValue ) );

		Assert.AreEqual( (long)int.MaxValue * 2, rectangle.Right );
		Assert.AreEqual( (long)int.MaxValue * 2, rectangle.Bottom );
		Assert.IsFalse( rectangle.FitsWithin( new InventoryGridSize( int.MaxValue, int.MaxValue ) ) );
	}

	[TestMethod]
	public void BoundsAndOverlapUseTheSameCheckedRectangles()
	{
		var bounds = new InventoryGridSize( 4, 4 );
		var first = new InventoryRectangle( new InventoryGridPosition( 2, 2 ), new InventoryGridSize( 2, 2 ) );
		var touching = new InventoryRectangle( new InventoryGridPosition( 0, 2 ), new InventoryGridSize( 2, 2 ) );
		var overlap = new InventoryRectangle( new InventoryGridPosition( 1, 2 ), new InventoryGridSize( 2, 2 ) );

		Assert.IsTrue( first.FitsWithin( bounds ) );
		Assert.IsFalse( first.Overlaps( touching ) );
		Assert.IsTrue( first.Overlaps( overlap ) );
	}
}
