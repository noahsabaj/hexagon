using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class SpawnCandidateSelectionTests
{
	[TestMethod]
	public void NamespacedSpawnsWinOutrightOverMapSpawns()
	{
		// A mounted map ships its own spawn points; a schema-authored one must take every slot,
		// or players are distributed onto arbitrary map geometry.
		var candidates = new[]
		{
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a1" ), false ),
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a2" ), true ),
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a3" ), false )
		};

		CollectionAssert.AreEqual( new[] { 1 }, SpawnCandidateSelection.Prefer( candidates ) );

		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 0, out var first ) );
		Assert.AreEqual( 1, first.CandidateIndex );
		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 7, out var later ) );
		Assert.AreEqual( 1, later.CandidateIndex, "Every slot must land on the only namespaced spawn." );
	}

	[TestMethod]
	public void WithoutNamespacedSpawnsEveryPointIsUsedInIdOrder()
	{
		// ID ordering, not scene order, so the same slot resolves to the same point across hosts
		// and across restarts.
		var candidates = new[]
		{
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000c0" ), false ),
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a0" ), false ),
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000b0" ), false )
		};

		CollectionAssert.AreEqual( new[] { 1, 2, 0 }, SpawnCandidateSelection.Prefer( candidates ) );
		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 0, out var zero ) );
		Assert.AreEqual( 1, zero.CandidateIndex );
		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 1, out var one ) );
		Assert.AreEqual( 2, one.CandidateIndex );
		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 2, out var two ) );
		Assert.AreEqual( 0, two.CandidateIndex );
	}

	[TestMethod]
	public void SlotsBeyondTheSpawnCountOverflowIntoRingsRatherThanStacking()
	{
		var candidates = new[]
		{
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a1" ), true ),
			new SpawnCandidate( Guid.Parse( "00000000-0000-0000-0000-0000000000a2" ), true )
		};

		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 0, out var first ) );
		Assert.AreEqual( 0, first.Placement.Ring, "The first layer sits on the point itself." );

		// Two points, so slot 2 wraps to the first point on the next layer out.
		Assert.IsTrue( SpawnCandidateSelection.TrySelect( candidates, 2, out var wrapped ) );
		Assert.AreEqual( 0, wrapped.CandidateIndex );
		Assert.IsGreaterThan( 0, wrapped.Placement.Ring );
		Assert.IsGreaterThan( 0.0, wrapped.Placement.Radius, "An overflow slot must be offset, not stacked." );
	}

	[TestMethod]
	public void AnEmptySceneSelectsNothingRatherThanThrowing()
	{
		Assert.IsFalse( SpawnCandidateSelection.TrySelect( Array.Empty<SpawnCandidate>(), 0, out _ ) );
	}
}