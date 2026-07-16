#nullable enable

using System;
using System.Collections.Generic;

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
