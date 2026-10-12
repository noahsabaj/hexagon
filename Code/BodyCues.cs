#nullable enable

using System;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What anyone near can see and hear of how someone is, drawn by each machine for itself from what the
/// host says of them: hands bound behind the back, hands on a body being dragged, carried or led, a
/// limp on a wounded leg, the steps of someone led while their player is away, hard breathing from
/// the badly hurt and the down, and a flinch where a blow lands. The engine's citizen animation has no
/// such states, so they are made from what it does have: hand placement, its walk, a walk-only style,
/// and its hit reaction. Nothing here decides anything.
/// </summary>
[Title( "Hexagon Body Cues" ), Category( "Hexagon" ), Icon( "accessibility" )]
public sealed class BodyCues : Component
{
	/// <summary>The animation's movement style that walks and never runs.</summary>
	private const int WalkOnly = 1;

	/// <summary>Where bound wrists are held, in the body's own space: together at the small of the back.</summary>
	public static Transform LeftWrist { get; set; } = new( new Vector3( -9f, 2.5f, 38f ), Rotation.From( 0, 180, 90 ) );
	public static Transform RightWrist { get; set; } = new( new Vector3( -9f, -2.5f, 38f ), Rotation.From( 0, 180, -90 ) );

	/// <summary>How a hand holds onto a body, in the holder's own space: reaching ahead, fingers down.</summary>
	public static Rotation Grip { get; set; } = Rotation.From( 40f, 0f, 0f );

	/// <summary>Which hands are placed: none, the left on what it holds, both on what they hold, or both bound.</summary>
	private enum Hands { Free, Left, Both }

	private SoundHandle? _breath;
	private Hands _hands;
	// Where a body left standing was last drawn, and how fast it has been going, for its steps.
	private Vector3 _drawnAt;
	private Vector3 _going;

	private IEmbodiment? Embodiment => GetComponent<IEmbodiment>();

	protected override void OnUpdate()
	{
		if ( Application.IsDedicatedServer || Embodiment is not { } body ) return;
		var renderer = body.Renderer;
		var drawn = renderer.IsValid() && renderer.Active;
		Breathe( drawn && body.IsBreathingHard ? renderer : null );
		// A limp body is posed by its physics, not by its animation.
		if ( !drawn || body.IsLimp ) return;
		Place( renderer!, body.IsRestrained, body as Player );
		renderer!.Set( "move_style", body.IsLimping ? WalkOnly : 0 );
		if ( body is Sleeper ) Step( renderer );
	}

	protected override void OnDisabled() => Breathe( null );

	/// <summary>
	/// Places the hands: the wrists together behind the back while they are bound; the left hand on the
	/// part of a body being dragged, or on the arm of someone being led; both arms round the knees of a
	/// body carried over the shoulder. Hands no longer placed are let go.
	/// </summary>
	private void Place( SkinnedModelRenderer renderer, bool bound, Player? mover )
	{
		var body = renderer.WorldTransform;
		var now = Hands.Free;
		if ( bound )
		{
			renderer.SetIk( "hand_left", body.ToWorld( LeftWrist ) );
			renderer.SetIk( "hand_right", body.ToWorld( RightWrist ) );
			now = Hands.Both;
		}
		else if ( mover is { HoldingBody: { } held } && held.IsValid() && held.Active )
		{
			var grip = body.Rotation * Grip;
			if ( mover.Holding == HoldKind.Carry )
			{
				if ( held.TryGetBoneTransform( "leg_lower_L", out var left ) && held.TryGetBoneTransform( "leg_lower_R", out var right ) )
				{
					// Whichever knee is on the holder's left is held by the left hand.
					if ( Vector3.Dot( left.Position - right.Position, body.Rotation.Right ) > 0f ) (left, right) = (right, left);
					renderer.SetIk( "hand_left", new Transform( left.Position, grip ) );
					renderer.SetIk( "hand_right", new Transform( right.Position, grip ) );
					now = Hands.Both;
				}
			}
			else if ( mover.Holding != HoldKind.None && held.TryGetBoneTransform( mover.HoldingBone, out var part ) )
			{
				renderer.SetIk( "hand_left", new Transform( part.Position, grip ) );
				now = Hands.Left;
			}
		}
		if ( _hands != Hands.Free && now == Hands.Free ) renderer.ClearIk( "hand_left" );
		if ( _hands == Hands.Both && now != Hands.Both ) renderer.ClearIk( "hand_right" );
		_hands = now;
	}

	/// <summary>
	/// A body left standing has no controller to walk it, so when it is led its steps are made here from
	/// how fast it goes, as the engine's own walking makes them from a controller's.
	/// </summary>
	private void Step( SkinnedModelRenderer renderer )
	{
		var at = WorldPosition;
		var moved = Time.Delta > 0f ? (at - _drawnAt).WithZ( 0f ) / Time.Delta : Vector3.Zero;
		_drawnAt = at;
		// Arriving, or put somewhere, is not walking there.
		if ( moved.Length > 400f ) moved = Vector3.Zero;
		_going = _going.LerpTo( moved, MathF.Min( 1f, Time.Delta * 8f ) );
		var going = _going.IsNearlyZero( 10f ) ? Vector3.Zero : _going;
		var facing = renderer.WorldRotation;
		var (ahead, aside) = (facing.Forward.Dot( going ), facing.Right.Dot( going ));
		renderer.Set( "b_grounded", true );
		renderer.Set( "move_groundspeed", going.Length );
		renderer.Set( "move_x", ahead );
		renderer.Set( "move_y", aside );
		renderer.Set( "move_z", 0f );
		renderer.Set( "wish_groundspeed", going.Length );
		renderer.Set( "wish_x", ahead );
		renderer.Set( "wish_y", aside );
		renderer.Set( "wish_z", 0f );
	}

	/// <summary>
	/// Breathes from the mouth of the body drawn, wherever its head is, for as long as there is one to
	/// breathe from. The recording is not made to loop, so it is played again whenever it ends.
	/// </summary>
	private void Breathe( SkinnedModelRenderer? renderer )
	{
		if ( renderer is null || HexagonServices.Current?.BreathingSound is not { } sound )
		{
			_breath?.Stop( 0.5f );
			_breath = null;
			return;
		}
		if ( _breath is not { IsPlaying: true } )
		{
			_breath = GameObject.PlaySound( sound );
			if ( _breath is null ) return;
			_breath.FollowParent = false;
		}
		_breath.Position = renderer.GetAttachment( "eyes" )?.Position ?? renderer.WorldPosition + Vector3.Up * 60f;
	}

	/// <summary>Host: a blow lands on someone standing, and whoever can see them sees them flinch from it.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Unreliable )]
	public void Flinch( Vector3 position, Vector3 direction, float strength )
	{
		if ( Application.IsDedicatedServer || Embodiment is not { IsLimp: false, Renderer: { Active: true } renderer } ) return;
		if ( renderer.GetClosestBone( position ) is not { } bone ) return;
		var offset = bone.LocalTransform.Position;
		// As the engine's citizen animation helper reacts to a hit.
		renderer.Set( "hit", true );
		renderer.Set( "hit_bone", bone.Index );
		renderer.Set( "hit_offset", offset == Vector3.Zero ? Vector3.One : offset );
		renderer.Set( "hit_direction", direction.Normal );
		renderer.Set( "hit_strength", strength );
	}
}
