#nullable enable

using System.Linq;
using Sandbox;

namespace Hexagon;

/// <summary>A push from what struck a body: where it landed, and which way it was travelling.</summary>
public readonly record struct Impact( Vector3 Position, Vector3 Direction );

/// <summary>
/// How a person's body is made in the world, whichever object carries it.
///
/// A shot is judged against the body's own hitboxes, from its model, which the host alone keeps: the
/// capsule a body walks with is only for walking into. A body that is down or dead goes limp, as the
/// engine's ragdoll (<see cref="ModelPhysics"/>): the host owns it and simulates it, and the engine
/// sends where its parts are to everyone it is sent to. It falls from the pose of the body it
/// replaces, so someone shot falls from where they stood, and the shot that put them down pushes it.
/// A limp body with nothing to take a pose from, after a restart, starts out lying on its back.
/// </summary>
internal static class Bodies
{
	/// <summary>How fast a blow pushes the part of a limp body it lands on, in units a second.</summary>
	public const float Push = 120f;

	/// <summary>
	/// Host: the hitboxes a shot is traced against, from the body's own model, on the host alone. A hit
	/// on one reports <paramref name="target"/>, and its tags say where on the body it landed.
	/// </summary>
	public static void AddHitboxes( SkinnedModelRenderer renderer, GameObject target )
	{
		if ( !Networking.IsHost ) return;
		var holder = new GameObject( renderer.GameObject, true, "Hitboxes" ) { NetworkMode = NetworkMode.Never };
		var hitboxes = holder.AddComponent<ModelHitboxes>();
		hitboxes.Renderer = renderer;
		hitboxes.Target = target;
	}

	/// <summary>
	/// Host: makes the body on <paramref name="root"/> limp, in the look's own model, before the object
	/// is put on the network so it arrives whole. The root follows the body's middle from then on.
	/// </summary>
	public static SkinnedModelRenderer MakeLimp( GameObject root, string look, SkinnedModelRenderer? from, Impact? impact )
	{
		var renderer = root.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Looks.ModelOf( look );
		var physics = root.AddComponent<ModelPhysics>( false );
		physics.Renderer = renderer;
		physics.Model = renderer.Model;
		// Bones are matched by their place in the skeleton, so only a body of the same model lends its pose.
		if ( from.IsValid() && from.Model == renderer.Model ) physics.CopyBonesFrom( from, teleport: true );
		physics.Enabled = true;
		if ( impact is { } blow ) Strike( physics, blow );
		return renderer;
	}

	/// <summary>Host: a blow pushes the part of a limp body nearest where it landed.</summary>
	public static void Strike( ModelPhysics physics, Impact impact )
	{
		var part = physics.Bodies.Select( value => value.Component ).Where( value => value.IsValid() )
			.OrderBy( value => value.WorldPosition.Distance( impact.Position ) ).FirstOrDefault();
		if ( part?.PhysicsBody is not { } body ) return;
		part.ApplyImpulseAt( impact.Position, impact.Direction.Normal * body.Mass * Push );
	}

	/// <summary>Where a limp body lies, as someone getting up from it would stand: the floor under its middle.</summary>
	public static Vector3 GroundPoint( GameObject body )
	{
		var middle = body.WorldPosition;
		var floor = body.Scene.Trace.Ray( middle + Vector3.Up * 16f, middle + Vector3.Down * 96f )
			.WithoutTags( "player", "item", "corpse", "sleeper", "capsule" )
			.Run();
		return floor.Hit && !floor.StartedSolid ? floor.HitPosition : middle;
	}

	/// <summary>
	/// Where a limp body starts out with no pose to fall from: on its back, a little above the floor, its
	/// middle where it was kept, which is where a limp body is kept. A model stands on its feet, so its
	/// feet go beyond that.
	/// </summary>
	public static Transform Lying( Model model, Vector3 at, float yaw )
	{
		var rotation = Rotation.From( -90f, yaw, 0f );
		var middle = model.Bones.GetBone( "pelvis" )?.LocalTransform.Position.z ?? 40f;
		return new Transform( at + Vector3.Up * 8f - rotation * (Vector3.Up * middle), rotation );
	}
}
