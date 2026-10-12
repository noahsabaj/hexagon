#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;
using Sandbox.Physics;

namespace Hexagon;

// Moving someone who cannot move themselves, from the side of whoever does it: dragging the dead or
// the down by the part taken hold of, carrying them over the shoulder, or leading someone bound by
// the arm (see Holds). The host holds a limp body to the mover's hands with the engine's own joint,
// as the editor holds what it grabs, and moves the hands with the mover; the body follows as physics
// lets it, and everyone it is sent to sees its parts where the host has them. Someone led walks where
// they are taken, moved by their own client, which the host holds to it. Anyone who can see the mover
// sees its hands on the body (see BodyCues), and nobody is told anything. Whatever stops the mover
// lets go, as does the body catching on something the hands walked on past.
public sealed partial class Player
{
	/// <summary>How this character has hold of someone, as anyone who can see it sees.</summary>
	[Sync( SyncFlags.FromHost )] public HoldKind Holding { get; set; }
	/// <summary>The body held, as it is drawn, for the hands to go to.</summary>
	[Sync( SyncFlags.FromHost )] public SkinnedModelRenderer? HoldingBody { get; set; }
	/// <summary>The part of the body held that the leading hand is on.</summary>
	[Sync( SyncFlags.FromHost )] public string HoldingBone { get; set; } = string.Empty;
	/// <summary>Whoever leads this character by the arm. Its own client walks it where it is taken.</summary>
	[Sync( SyncFlags.FromHost )] public Player? LedBy { get; set; }

	/// <summary>The arm someone led is held by: the right, as they walk at the left hand of whoever leads them.</summary>
	public const string LedArm = "arm_upper_R";
	/// <summary>The middle of a body, which lies across the shoulder of whoever carries it.</summary>
	public const string Middle = "pelvis";
	/// <summary>How far someone down may be from their body before their pawn is brought to it, so they see what it sees.</summary>
	public const float KeepUp = 48f;

	/// <summary>Host: a hold on someone, and what the host holds the body with.</summary>
	private sealed class Hold
	{
		public HoldKind Kind;
		/// <summary>The body held: a corpse or someone down on the ground, someone left standing bound, or a pawn.</summary>
		public Component Target = null!;
		/// <summary>Where the body was when it was taken hold of.</summary>
		public Vector3 From;
		/// <summary>The keyframed body the hands are, the joint holding the body to them, the part held, and where on it.</summary>
		public PhysicsBody? Hands;
		public Sandbox.Physics.FixedJoint? Joint;
		public Rigidbody? Part;
		public Vector3 Grip;
		/// <summary>How a carried body's middle lies across the shoulder: its own turn in the model, laid face down, head behind.</summary>
		public Rotation Lies = Rotation.Identity;
		public RealTimeSince Since;
		/// <summary>Since someone led was last near enough to where they are led.</summary>
		public RealTimeSince Near;
	}

	// Host-side.
	private Hold? _hold;

	/// <summary>A body laid face down, its head behind: the model's up turned to point back, and its front down.</summary>
	private static readonly Rotation Across = Rotation.FromAxis( new Vector3( 1f, 0f, -1f ).Normal, 180f );

	/// <summary>Host: how this character has hold of someone.</summary>
	internal HoldKind HostHolding => _hold?.Kind ?? HoldKind.None;

	/// <summary>Host: the body this character has hold of.</summary>
	internal Component? HostHeldBody => _hold?.Target;

	/// <summary>Host: whoever has hold of a body, if anyone.</summary>
	internal static Player? HostHolderOf( Component body ) =>
		Game.ActiveScene?.GetAllComponents<Player>().FirstOrDefault( value => value._hold?.Target == body );

	/// <summary>
	/// The fastest this character goes for what it holds: a drag at the pace of a limp, a carry or a lead
	/// at a walk, or the pace of whoever it leads if they limp.
	/// </summary>
	private float HoldPace
	{
		get
		{
			var walk = Holding == HoldKind.Lead && HoldingBody?.GameObject.Components.GetInAncestorsOrSelf<Player>() is { IsLimping: true }
				? MathF.Min( _speeds.Walk, LimpSpeed )
				: _speeds.Walk;
			return Holds.Pace( Holding, walk, LimpSpeed );
		}
	}

	/// <summary>
	/// Host: this character takes hold of someone, as a verb on them asks: a body on the ground to drag by
	/// the part it looks at, or to carry over its shoulder, or someone standing bound to lead by the arm.
	/// </summary>
	internal Result HostTakeHold( Component target, HoldKind kind )
	{
		if ( _character is null || Controller is not { } controller ) return Result.Fail( ErrorCode.Denied, "You have no character." );
		var limp = target is Corpse or Sleeper { IsLimp: true };
		var bound = target is Sleeper { IsRestrained: true } or Player { IsRestrained: true, IsDown: false };
		var can = Holds.ToTake( kind, HostHolding, _seat is not null, _character.Equipped is not null, limp, bound, HostHolderOf( target ) is not null );
		if ( !can.Ok ) return can;
		// Both hands on a body leaves none for a holder.
		if ( kind == HoldKind.Carry ) HostClose();
		var renderer = target is Corpse ? target.GetComponent<SkinnedModelRenderer>( includeDisabled: true ) : (target as IEmbodiment)?.Renderer;
		var hold = new Hold { Kind = kind, Target = target, From = HostBodyAt( target ), Since = 0, Near = 0 };
		var bone = LedArm;
		if ( kind != HoldKind.Lead )
		{
			if ( target.GetComponent<ModelPhysics>() is not { } physics || HostTaken( physics, kind, controller ) is not { } taken )
				return Result.Fail( ErrorCode.NotFound, "There is nothing there to take hold of." );
			(hold.Part, hold.Grip) = taken;
			bone = physics.Model?.Bones.AllBones.FirstOrDefault( value => value.Index == physics.Bodies.First( body => body.Component == taken.Part ).Bone )?.Name ?? Middle;
			if ( kind == HoldKind.Carry ) hold.Lies = physics.Model?.Bones.GetBone( Middle )?.LocalTransform.Rotation ?? Rotation.Identity;
			HostGrasp( hold, physics );
		}
		_hold = hold;
		Holding = kind;
		HoldingBody = renderer;
		HoldingBone = bone;
		if ( target is Player led ) led.LedBy = this;
		return Result.Success();
	}

	/// <summary>
	/// Host: the part of a limp body taken hold of, and where on it. Carried, it is the middle. Dragged,
	/// it is whatever part the mover is looking at, where it looks; or else the part nearest its eyes.
	/// </summary>
	private (Rigidbody Part, Vector3 Grip)? HostTaken( ModelPhysics physics, HoldKind kind, PlayerController controller )
	{
		var parts = physics.Bodies.Where( value => value.Component.IsValid() && value.Component.PhysicsBody.IsValid() ).ToArray();
		if ( parts.Length == 0 ) return null;
		if ( kind == HoldKind.Carry )
		{
			var middle = physics.Model?.Bones.GetBone( Middle )?.Index;
			var root = parts.FirstOrDefault( value => value.Bone == middle ).Component ?? parts[0].Component;
			return (root, Vector3.Zero);
		}
		var eye = HostPosition + Vector3.Up * HostEyeHeight;
		var look = controller.EyeAngles.Forward;
		var seen = Scene.Trace.Ray( eye, eye + look * PersonReach * 1.5f ).IgnoreGameObjectHierarchy( GameObject ).Run();
		if ( seen.Hit && parts.FirstOrDefault( value => value.Component.PhysicsBody == seen.Body ).Component is { } struck )
			return (struck, struck.PhysicsBody.Transform.PointToLocal( seen.HitPosition ));
		// Nearest the line the eyes look along.
		var nearest = parts.OrderBy( value => Vector3.Cross( look, value.Component.WorldPosition - eye ).Length ).First().Component;
		return (nearest, nearest.PhysicsBody.LocalMassCenter);
	}

	/// <summary>
	/// Host: holds the part taken hold of to the hands, as the engine's editor holds what it grabs: a
	/// keyframed body the hands move, jointed to the part by a spring strong enough to pull the body
	/// along. Dragged, the part turns as it likes; carried, the body is turned to lie across the shoulder.
	/// </summary>
	private void HostGrasp( Hold hold, ModelPhysics physics )
	{
		var hands = new PhysicsBody( Scene.PhysicsWorld ) { BodyType = PhysicsBodyType.Keyframed };
		hands.Transform = HostHands( hold );
		var joint = PhysicsJoint.CreateFixed( new PhysicsPoint( hands ), new PhysicsPoint( hold.Part!.PhysicsBody, hold.Grip ) );
		var weight = physics.Mass * MathF.Abs( Scene.PhysicsWorld.Gravity.z );
		if ( hold.Kind == HoldKind.Carry )
		{
			joint.SpringLinear = new PhysicsSpring( 6f, 1f, weight * 4f );
			joint.SpringAngular = new PhysicsSpring( 3f, 1f );
		}
		else
		{
			joint.SpringLinear = new PhysicsSpring( 5f, 1f, weight * 2f );
			joint.SpringAngular = new PhysicsSpring( 0f, 0f, 0f );
		}
		hold.Hands = hands;
		hold.Joint = joint;
		// A body at rest is asleep, and one asleep would not feel the pull.
		foreach ( var part in physics.Bodies ) if ( part.Component.IsValid() ) part.Component.Sleeping = false;
	}

	/// <summary>
	/// Host: where the mover's hands are now: an arm's length ahead at the hip to drag, at the shoulder
	/// to carry, turned for the body to lie across it, or where someone led is walked to.
	/// </summary>
	private Transform HostHands( Hold hold )
	{
		var feet = HostFeet;
		var yaw = Controller?.EyeAngles.yaw ?? WorldRotation.Yaw();
		return hold.Kind switch
		{
			HoldKind.Carry => new Transform( Holds.Shoulder( feet, yaw ), Rotation.FromYaw( yaw ) * Across * hold.Lies ),
			HoldKind.Lead => new Transform( Holds.Beside( feet, yaw ), Rotation.FromYaw( yaw ) ),
			_ => new Transform( Holds.Hand( feet, yaw ), Rotation.FromYaw( yaw ) )
		};
	}

	/// <summary>
	/// Host: where the mover stands, as its owner says, while that is near where the host believes it is:
	/// the claim moves smoothly, and the belief only as often as claims are judged.
	/// </summary>
	private Vector3 HostFeet => WorldPosition.Distance( HostPosition ) <= 64f ? WorldPosition : HostPosition;

	/// <summary>Host: the hands go where the mover goes, and let go whenever something stops them.</summary>
	private void HostHoldTick()
	{
		if ( _hold is not { } hold ) return;
		if ( HostHoldEnds( hold ) is { } why )
		{
			HostLetGo( why );
			return;
		}
		var hands = HostHands( hold );
		if ( hold.Kind == HoldKind.Lead )
		{
			HostLeadTick( hold, hands.Position );
			return;
		}
		hold.Hands!.Transform = hands;
		// Caught on something the mover walked on past, once the body has had a moment to come to the hands.
		if ( hold.Since > 1f && hold.Part.IsValid() && Holds.Strays( hold.Part.PhysicsBody.Transform.PointToWorld( hold.Grip ), hands.Position ) )
		{
			HostLetGo( "caught" );
			return;
		}
		// Someone down looks out of their body, so their pawn goes with it: what they are sent is what is near it.
		if ( hold.Target is Sleeper { IsLimp: true } lying && HostLoaded( lying.CharacterId ) is { } down && down.HostPosition.Distance( lying.HostGroundPoint ) > KeepUp )
			down.HostTeleport( lying.HostGroundPoint );
	}

	/// <summary>Host: why the hold has ended, if it has: whatever stops the mover, or the body gone, or no longer as it was taken.</summary>
	private string? HostHoldEnds( Hold hold )
	{
		if ( _character is null ) return "left";
		if ( _character.IsDown ) return "down";
		if ( _character.IsRestrained ) return "bound";
		if ( _seat is not null ) return "sat";
		if ( _leaning is not null ) return "leaning";
		if ( _work is not null ) return "working";
		if ( !hold.Target.IsValid() || hold.Target.GameObject.IsDestroyed ) return "gone";
		var still = hold.Target switch
		{
			Corpse => true,
			Sleeper sleeper => hold.Kind == HoldKind.Lead ? !sleeper.IsLimp && sleeper.IsRestrained : sleeper.IsLimp,
			Player led => led._character is { IsRestrained: true, IsDown: false } && led._seat is null,
			_ => false
		};
		if ( !still ) return "changed";
		if ( !Holds.LeavesAHand( hold.Kind ) && _character.Equipped is not null ) return "hands";
		return null;
	}

	/// <summary>
	/// Host: someone led is walked to the leading hand. A pawn's own client walks it there, and one that
	/// does not is pulled there, if nothing is in the way, or else let go. Someone left standing bound is
	/// walked there by the host, at a walk, along the floor, and no further than a wall lets them.
	/// </summary>
	private void HostLeadTick( Hold hold, Vector3 beside )
	{
		var at = hold.Target switch
		{
			Player led => led.HostPosition,
			_ => hold.Target.WorldPosition
		};
		if ( hold.Target is Sleeper standing ) at = HostWalkLed( standing, beside );
		if ( at.WithZ( 0f ).Distance( beside.WithZ( 0f ) ) <= Holds.Slack )
		{
			hold.Near = 0;
			return;
		}
		if ( hold.Near < Holds.Grace ) return;
		if ( hold.Target is not Player pulled || HostBlocked( HostPosition, beside ) || HostFloor( beside ) is not { } floor )
		{
			HostLetGo( "caught" );
			return;
		}
		pulled.HostTeleport( floor );
		hold.Near = 0;
	}

	/// <summary>Host: walks someone left standing bound toward where they are led, at a walk, and returns where they are now.</summary>
	private Vector3 HostWalkLed( Sleeper led, Vector3 to )
	{
		var at = led.WorldPosition;
		var way = (to - at).WithZ( 0f );
		var step = MathF.Min( way.Length, _speeds.Walk * Time.Delta );
		if ( step < 0.5f ) return at;
		var next = at + way.Normal * step;
		if ( HostBlocked( at, next ) || HostFloor( next ) is not { } floor ) return at;
		led.WorldPosition = floor;
		led.WorldRotation = Rotation.FromYaw( way.EulerAngles.yaw );
		return floor;
	}

	/// <summary>Host: the floor under a place, a step above it or below.</summary>
	private Vector3? HostFloor( Vector3 at )
	{
		var floor = Scene.Trace.Ray( at + Vector3.Up * 24f, at + Vector3.Down * 64f )
			.WithoutTags( "player", "item", "corpse", "sleeper", "capsule" )
			.Run();
		return floor.Hit && !floor.StartedSolid ? floor.HitPosition : null;
	}

	/// <summary>Host: where a body is: on the ground under its middle, where someone left standing stands, or where a pawn is believed to be.</summary>
	private static Vector3 HostBodyAt( Component body ) => body switch
	{
		Corpse => Bodies.GroundPoint( body.GameObject ),
		Sleeper sleeper => sleeper.HostGroundPoint,
		Player pawn => pawn.HostPosition,
		_ => body.WorldPosition
	};

	/// <summary>
	/// Host: lets go of whoever this character has hold of. Where the body was left is kept with it, and
	/// the journal has who let go of whom, how they held them, why they let go, and how far it had come.
	/// </summary>
	internal void HostLetGo( string why )
	{
		if ( _hold is not { } hold ) return;
		_hold = null;
		if ( hold.Joint.IsValid() ) hold.Joint.Remove();
		if ( hold.Hands.IsValid() ) hold.Hands.Remove();
		Holding = HoldKind.None;
		HoldingBody = null;
		HoldingBone = string.Empty;
		if ( hold.Target is Player led && led.IsValid() && led.LedBy == this ) led.LedBy = null;
		var subject = hold.Target.IsValid() ? HostKeepBody( hold.Target ) : null;
		var moved = hold.Target.IsValid() ? HostBodyAt( hold.Target ).Distance( hold.From ) : 0f;
		HostRecord( JournalKinds.HoldLetGo, subject, data: new[] { ("hold", hold.Kind.ToString().ToLowerInvariant()), ("reason", why), ("moved", $"{moved:0}") } );
	}

	/// <summary>Host: keeps where a body was left with whoever it is, so a restart finds it there, and says who it is for the journal.</summary>
	private static string? HostKeepBody( Component body )
	{
		if ( HexagonServices.Current is not { Roster: { } roster, Holders: { } holders } ) return null;
		switch ( body )
		{
			case Corpse corpse when holders.Find( corpse.HolderId ) is { } holder:
				if ( Corpse.HostKeepPosition( holder ) ) holders.Save( holder );
				return holder.HolderLabel;
			case Sleeper sleeper when roster.Find( sleeper.CharacterId ) is { } character:
				sleeper.HostKeepPosition( character );
				roster.Save( character );
				return character.HolderLabel;
			case Player pawn:
				return pawn._character?.HolderLabel;
			default:
				return null;
		}
	}

	/// <summary>Owner: Use, with nothing in front of the hands to press, lets go.</summary>
	private void LetGoTick()
	{
		if ( Holding == HoldKind.None || UiBusy || Controller is not { } controller ) return;
		if ( Input.Pressed( controller.UseButton ) && !controller.Hovered.IsValid() ) LetGo();
	}

	/// <summary>Owner: lets go of whoever this character has hold of.</summary>
	public void LetGo() => RequestLetGo();

	[Rpc.Host]
	private void RequestLetGo()
	{
		if ( Authorize( nameof( RequestLetGo ), out _, out _, needsCharacter: false ) ) HostLetGo( "let go" );
	}

	/// <summary>Owner: walking for a test, as forward and right from -1 to 1 and whether running, in place of the keys, while set.</summary>
	public (float Forward, float Right, bool Run)? WalkOverride { get; set; }

	/// <summary>Whether this character's legs are someone else's to move: led by the arm, or walked by a test.</summary>
	private bool Walked => LedBy.IsValid() || WalkOverride is not null;

	/// <summary>
	/// Owner: someone led walks toward the leading hand, at no more than a walk; a test walks where it
	/// says. Otherwise, whenever the keys do not move the legs, the legs are still.
	/// </summary>
	private void WalkTick()
	{
		if ( Controller is not { } controller || controller.UseInputControls ) return;
		var wish = Vector3.Zero;
		if ( HasCharacter && !IsDown && LedBy is { } leader && leader.IsValid() && leader.Controller is { } leading )
		{
			var to = ((Vector3)Holds.Beside( leader.WorldPosition, leading.EyeAngles.yaw ) - WorldPosition).WithZ( 0f );
			wish = to.Length < 4f ? Vector3.Zero : to.Normal * MathF.Min( controller.WalkSpeed, to.Length * 4f );
		}
		else if ( HasCharacter && !IsDown && WalkOverride is { } walk )
		{
			var facing = Rotation.FromYaw( controller.EyeAngles.yaw );
			var way = facing.Forward * walk.Forward + facing.Right * walk.Right;
			wish = way.Length < 0.01f ? Vector3.Zero : way.Normal * (walk.Run ? controller.RunSpeed : controller.WalkSpeed);
		}
		controller.WishVelocity = wish;
	}
}
