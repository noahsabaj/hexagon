#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>Why a placement is being chosen, so a game can spawn differently on respawn.</summary>
public readonly record struct HexSpawnRequest(
	Guid ConnectionId,
	CharacterId? CharacterId,
	bool IsRespawn );

/// <summary>
/// Decides where a player is placed. The framework owns the MECHANISM of placement - moving a
/// body without tripping the movement validator is something only it can do correctly - while
/// this seam owns the POLICY of which point a given player gets, which is a game rule.
/// <para>
/// Both the initial connect placement and every later embodiment resolve through one
/// implementation of this, so there is a single answer to "where does this player go" rather
/// than a connect-time answer and a stale copy of it.
/// </para>
/// </summary>
public interface IHexSpawnSelector
{
	OperationResult<Transform> Select( HexSpawnRequest request );
}

/// <summary>
/// The default policy: schema-authored spawn points tagged
/// <see cref="SpawnCandidateSelection.NamespacedTag"/> when the scene has any, otherwise every
/// active spawn point, distributed across leased slots and then outward in rings so players do
/// not stack. A game replaces this through <c>HexSchemaRuntimeDescriptor.CreateSpawnSelector</c>
/// when it needs faction spawns, cells, or respawn-near-death.
/// </summary>
public sealed class SceneTagSpawnSelector : IHexSpawnSelector
{
	private readonly Scene _scene;
	private readonly SpawnSlotAllocator _slots;

	internal SceneTagSpawnSelector( Scene scene, SpawnSlotAllocator slots )
	{
		_scene = scene ?? throw new ArgumentNullException( nameof(scene) );
		_slots = slots ?? throw new ArgumentNullException( nameof(slots) );
	}

	public OperationResult<Transform> Select( HexSpawnRequest request )
	{
		var points = _scene.GetAll<SpawnPoint>().Where( candidate => candidate.Active ).ToArray();
		var candidates = points
			.Select( point => new SpawnCandidate(
				point.GameObject.Id,
				point.GameObject.Tags.Has( SpawnCandidateSelection.NamespacedTag ) ) )
			.ToArray();
		// A slot lease is per connection and is reused, so a respawn returns to the same point
		// the connection was assigned rather than drifting to whichever slot is free now.
		var slot = _slots.Acquire( request.ConnectionId );
		if ( !SpawnCandidateSelection.TrySelect( candidates, slot, out var selection ) )
			return OperationResult<Transform>.Failure(
				ErrorCode.ConfigurationInvalid, "The scene has no active spawn point." );

		var point = points[selection.CandidateIndex];
		var position = point.WorldPosition;
		if ( selection.Placement.Ring > 0 )
		{
			var offset = new Vector3(
				(float)(Math.Cos( selection.Placement.AngleRadians ) * selection.Placement.Radius),
				(float)(Math.Sin( selection.Placement.AngleRadians ) * selection.Placement.Radius),
				0.0f );
			position += point.WorldRotation * offset;
		}

		return OperationResult<Transform>.Success(
			point.WorldTransform.WithPosition( position ).WithScale( 1 ) );
	}
}