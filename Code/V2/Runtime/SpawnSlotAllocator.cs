#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Sandbox-independent lease table and deterministic placement mapping for
/// connection spawn slots.
/// </summary>
internal sealed class SpawnSlotAllocator
{
	private readonly Dictionary<Guid, int> _leases = new();

	public int Acquire( Guid connectionId )
	{
		if ( _leases.TryGetValue( connectionId, out var existing ) ) return existing;

		var used = new HashSet<int>( _leases.Values );
		var slot = 0;
		while ( used.Contains( slot ) ) slot++;
		_leases.Add( connectionId, slot );
		return slot;
	}

	public bool Release( Guid connectionId ) => _leases.Remove( connectionId );

	public void Clear() => _leases.Clear();

	public static SpawnSlotPlacement Describe( int slot, int spawnPointCount )
	{
		ArgumentOutOfRangeException.ThrowIfNegative( slot );
		ArgumentOutOfRangeException.ThrowIfLessThan( spawnPointCount, 1 );

		var spawnPointIndex = slot % spawnPointCount;
		var layer = slot / spawnPointCount;
		if ( layer == 0 ) return new SpawnSlotPlacement( spawnPointIndex, 0, 0 );

		var overflowIndex = layer - 1;
		return new SpawnSlotPlacement(
			spawnPointIndex,
			(overflowIndex / SpawnSlotPlacement.PositionsPerRing) + 1,
			overflowIndex % SpawnSlotPlacement.PositionsPerRing );
	}
}

internal readonly record struct SpawnSlotPlacement( int SpawnPointIndex, int Ring, int RingIndex )
{
	public const int PositionsPerRing = 8;
	public const float RingSpacing = 64.0f;

	public double AngleRadians => RingIndex * (Math.PI * 2.0 / PositionsPerRing);
	public double Radius => Ring * RingSpacing;
}

/// <summary>One spawn point as the selection rule sees it, free of any engine type.</summary>
internal readonly record struct SpawnCandidate( Guid Id, bool IsNamespaced );

internal readonly record struct SpawnSelection( int CandidateIndex, SpawnSlotPlacement Placement );

/// <summary>
/// Which spawn point a slot gets, and where around it. Kept engine-free so the rule that decides
/// where players land is unit-tested rather than only observable by running the game.
/// <para>
/// Mounted map packages ship their own spawn points - flatgrass carries a whole roof-top grid,
/// and map spawns can carry the generic "spawn" tag too - so a schema-authored spawn marked with
/// the namespaced tag must win outright, or players are distributed onto arbitrary map geometry.
/// Ordering is by object ID so the same slot resolves to the same point across hosts and restarts.
/// </para>
/// </summary>
internal static class SpawnCandidateSelection
{
	public const string NamespacedTag = "hexagon_spawn";

	public static bool TrySelect(
		IReadOnlyList<SpawnCandidate> candidates,
		int slot,
		out SpawnSelection selection )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		ArgumentOutOfRangeException.ThrowIfNegative( slot );
		var preferred = Prefer( candidates );
		if ( preferred.Length == 0 )
		{
			selection = default;
			return false;
		}

		var placement = SpawnSlotAllocator.Describe( slot, preferred.Length );
		selection = new SpawnSelection( preferred[placement.SpawnPointIndex], placement );
		return true;
	}

	/// <summary>
	/// Indices into <paramref name="candidates"/> in the order slots consume them: namespaced
	/// spawns alone when any exist, otherwise every candidate, each ordered by ID.
	/// </summary>
	public static int[] Prefer( IReadOnlyList<SpawnCandidate> candidates )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		var indices = Enumerable.Range( 0, candidates.Count ).ToArray();
		var namespaced = indices.Where( index => candidates[index].IsNamespaced ).ToArray();
		return (namespaced.Length > 0 ? namespaced : indices)
			.OrderBy( index => candidates[index].Id )
			.ToArray();
	}
}