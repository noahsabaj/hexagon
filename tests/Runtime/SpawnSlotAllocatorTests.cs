#nullable enable

using System;
using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class SpawnSlotAllocatorTests
{
	[TestMethod]
	public void SequentialConnectionsReceiveDistinctLowestSlots()
	{
		var allocator = new SpawnSlotAllocator();

		Assert.AreEqual( 0, allocator.Acquire( Guid.NewGuid() ) );
		Assert.AreEqual( 1, allocator.Acquire( Guid.NewGuid() ) );
		Assert.AreEqual( 2, allocator.Acquire( Guid.NewGuid() ) );
	}

	[TestMethod]
	public void RepeatedAcquireReturnsTheExistingLease()
	{
		var allocator = new SpawnSlotAllocator();
		var connection = Guid.NewGuid();

		Assert.AreEqual( 0, allocator.Acquire( connection ) );
		Assert.AreEqual( 0, allocator.Acquire( connection ) );
		Assert.AreEqual( 1, allocator.Acquire( Guid.NewGuid() ) );
	}

	[TestMethod]
	public void ReleasedSlotIsReusedBeforeHigherSlots()
	{
		var allocator = new SpawnSlotAllocator();
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		var third = Guid.NewGuid();
		Assert.AreEqual( 0, allocator.Acquire( first ) );
		Assert.AreEqual( 1, allocator.Acquire( second ) );
		Assert.AreEqual( 2, allocator.Acquire( third ) );

		Assert.IsTrue( allocator.Release( second ) );
		Assert.AreEqual( 1, allocator.Acquire( Guid.NewGuid() ) );
	}

	[TestMethod]
	public void ConfiguredSpawnPointsAreUsedExactlyBeforeOverflow()
	{
		Assert.AreEqual( new SpawnSlotPlacement( 0, 0, 0 ), SpawnSlotAllocator.Describe( 0, 3 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 1, 0, 0 ), SpawnSlotAllocator.Describe( 1, 3 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 2, 0, 0 ), SpawnSlotAllocator.Describe( 2, 3 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 0, 1, 0 ), SpawnSlotAllocator.Describe( 3, 3 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 1, 1, 0 ), SpawnSlotAllocator.Describe( 4, 3 ) );
	}

	[TestMethod]
	public void OverflowUsesDeterministicEightPositionRings()
	{
		Assert.AreEqual( new SpawnSlotPlacement( 0, 1, 0 ), SpawnSlotAllocator.Describe( 1, 1 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 0, 1, 7 ), SpawnSlotAllocator.Describe( 8, 1 ) );
		Assert.AreEqual( new SpawnSlotPlacement( 0, 2, 0 ), SpawnSlotAllocator.Describe( 9, 1 ) );
		Assert.AreEqual( 64.0, SpawnSlotAllocator.Describe( 1, 1 ).Radius );
		Assert.AreEqual( Math.PI / 4.0, SpawnSlotAllocator.Describe( 2, 1 ).AngleRadians, 1e-12 );
		Assert.AreEqual( 128.0, SpawnSlotAllocator.Describe( 9, 1 ).Radius );
	}
}
