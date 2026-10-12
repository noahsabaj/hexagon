#nullable enable

using Sandbox;

namespace Hexagon;

/// <summary>
/// What anyone near can see and hear of how someone is, drawn by each machine for itself from what the
/// host says of them: hands bound behind the back, a limp on a wounded leg, hard breathing from the
/// badly hurt and the down, and a flinch where a blow lands. The engine's citizen animation has no
/// such states, so they are made from what it does have: hand placement, a walk-only style, and its
/// hit reaction. Nothing here decides anything.
/// </summary>
[Title( "Hexagon Body Cues" ), Category( "Hexagon" ), Icon( "accessibility" )]
public sealed class BodyCues : Component
{
	/// <summary>The animation's movement style that walks and never runs.</summary>
	private const int WalkOnly = 1;

	/// <summary>Where bound wrists are held, in the body's own space: together at the small of the back.</summary>
	public static Transform LeftWrist { get; set; } = new( new Vector3( -9f, 2.5f, 38f ), Rotation.From( 0, 180, 90 ) );
	public static Transform RightWrist { get; set; } = new( new Vector3( -9f, -2.5f, 38f ), Rotation.From( 0, 180, -90 ) );

	private SoundHandle? _breath;
	private bool _bound;

	private IEmbodiment? Embodiment => GetComponent<IEmbodiment>();

	protected override void OnUpdate()
	{
		if ( Application.IsDedicatedServer || Embodiment is not { } body ) return;
		var renderer = body.Renderer;
		var drawn = renderer.IsValid() && renderer.Active;
		Breathe( drawn && body.IsBreathingHard ? renderer : null );
		// A limp body is posed by its physics, not by its animation.
		if ( !drawn || body.IsLimp ) return;
		Bind( renderer!, body.IsRestrained );
		renderer!.Set( "move_style", body.IsLimping ? WalkOnly : 0 );
	}

	protected override void OnDisabled() => Breathe( null );

	/// <summary>Holds the wrists together behind the back while the hands are bound, and lets them go after.</summary>
	private void Bind( SkinnedModelRenderer renderer, bool bound )
	{
		if ( bound )
		{
			var body = renderer.WorldTransform;
			renderer.SetIk( "hand_left", body.ToWorld( LeftWrist ) );
			renderer.SetIk( "hand_right", body.ToWorld( RightWrist ) );
		}
		else if ( _bound )
		{
			renderer.ClearIk( "hand_left" );
			renderer.ClearIk( "hand_right" );
		}
		_bound = bound;
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
