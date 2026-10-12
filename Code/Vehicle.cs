#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;
using Sandbox.Movement;
using Sandbox.Physics;

namespace Hexagon;

/// <summary>
/// A truck. The host drives it as a body of its own, held up by its wheels: each traces down to the
/// road under it, springs against it, and grips it, driving and braking as whoever sits at its wheel
/// asks, which is all a driver sends. A driver needs a key cut to its lock and fuel in its tank, which
/// driving burns by the distance and which is filled by hand from a can. It has a seat at the wheel,
/// a seat beside it, and a bed at the back that locks to the same key. Fast enough, it hurts whoever it
/// strikes, on the record under its driver; slower, it only pushes. It keeps where it was left and what
/// is in its tank across a restart. See <see cref="Vehicles"/>.
/// </summary>
[Title( "Hexagon Vehicle" ), Category( "Hexagon" ), Icon( "local_shipping" )]
public sealed class Vehicle : Component, Component.IPressable, IVerbTarget
{
	/// <summary>The tag a vehicle's colliders carry, which people's are set apart from where the host simulates it.</summary>
	public const string Tag = "vehicle";

	[Property] public string Title { get; set; } = "Truck";
	/// <summary>What its ignition and its bed are cut to: only a key cut the same starts it or opens the bed. Empty means anyone can.</summary>
	[Property] public string LockPattern { get; set; } = string.Empty;
	/// <summary>How much its tank holds.</summary>
	[Property, Range( 1f, 500f )] public float Tank { get; set; } = 60f;
	/// <summary>What is in its tank before it has ever been driven.</summary>
	[Property, Range( 0f, 500f )] public float StartingFuel { get; set; } = 20f;
	/// <summary>Fuel burned for every thousand units driven.</summary>
	[Property, Range( 0f, 50f )] public float Burns { get; set; } = 1f;
	/// <summary>How fast it goes forward, at most, in units a second. Backward, a third of that.</summary>
	[Property, Range( 50f, 2000f )] public float TopSpeed { get; set; } = 600f;
	/// <summary>How hard it pulls away, in units a second, each second, with the pedal all the way down.</summary>
	[Property, Range( 10f, 2000f )] public float Pull { get; set; } = 300f;
	/// <summary>How hard it brakes, in units a second, each second.</summary>
	[Property, Range( 10f, 5000f )] public float Braking { get; set; } = 900f;
	/// <summary>How far its steering wheels turn, in degrees, at full lock.</summary>
	[Property, Range( 1f, 60f )] public float SteerAngle { get; set; } = 30f;
	/// <summary>How far its wheels travel on their springs: half of it under its own weight.</summary>
	[Property, Range( 1f, 50f )] public float Travel { get; set; } = 10f;
	/// <summary>How hard its tyres hold the road before they slide, as a share of the weight on them.</summary>
	[Property, Range( 0.1f, 5f )] public float Grip { get; set; } = 1.2f;

	/// <summary>How full its tank is, as its gauge shows it, from nothing to one.</summary>
	[Sync( SyncFlags.FromHost )] public float Gauge { get; set; }

	public IReadOnlyList<Verb> Verbs { get; } = new[]
	{
		new Verb( VerbIds.VehicleDrive, "Drive" ),
		new Verb( VerbIds.VehicleRide, "Ride" ),
		new Verb( VerbIds.VehicleFuel, "Fill the tank" ),
	};

	/// <summary>Its seats, the one at the wheel first.</summary>
	public IEnumerable<Seat> Seats => GameObject.Components.GetAll<Seat>( FindMode.EverythingInSelfAndDescendants ).OrderBy( value => !value.Driver );

	private Rigidbody? Body => GetComponent<Rigidbody>();

	// Host-side.
	private bool _restored;
	private Wheel[] _wheels = Array.Empty<Wheel>();
	// What it is made of, in its own space, which is what strikes someone.
	private BBox _shape;
	private float _throttle;
	private float _steer;
	private float _steering;
	private bool _brake;
	private bool _toldEmpty;
	private readonly Dictionary<Guid, RealTimeSince> _struck = new();

	/// <summary>How firmly a tyre takes out its share of a slide in one step: less than all, so four of them do not overshoot.</summary>
	private const float Firmness = 0.5f;
	/// <summary>What slows it with no foot on either pedal, in units a second, each second.</summary>
	private const float Rolling = 40f;
	/// <summary>How fast the wheel turns from lock to lock, in turns of full lock a second.</summary>
	private const float SteerRate = 2.5f;
	/// <summary>How far past what it is made of someone is struck, for the speed it closes in at.</summary>
	private const float StrikeReach = 24f;
	/// <summary>How long one person is struck once, however long they stay in its way.</summary>
	private const float StrikeEvery = 1.5f;

	/// <summary>Host: who sits at its wheel, if anyone.</summary>
	public Player? HostDriver => Seats.FirstOrDefault( value => value.Driver )?.HostOccupant;

	protected override void OnUpdate()
	{
		if ( !_restored ) HostRestore();
	}

	/// <summary>
	/// Host: join the session, and pick up where it was left, with what was in its tank. As the host has
	/// it, it goes through people rather than stopping dead against them: they are moved by their own
	/// players, who are pushed aside by it, and are struck by it here.
	/// </summary>
	private void HostRestore()
	{
		if ( !HostScene.Join( this ) ) return;
		_restored = true;
		_wheels = GameObject.Components.GetAll<Wheel>( FindMode.EverythingInSelfAndDescendants ).ToArray();
		_shape = Shape();
		HostIgnorePeople( Scene );
		var data = Remembered();
		if ( data.Position is { Length: 3 } at )
		{
			WorldPosition = new Vector3( at[0], at[1], at[2] );
			WorldRotation = Rotation.FromYaw( data.Yaw );
		}
		Gauge = Shown( data.Fuel );
		if ( _wheels.Length == 0 ) Log.Warning( $"[scene] {GameObject.Name} has no wheels, and will not move." );
	}

	/// <summary>Host: the scene's people are not solid to vehicles as the host simulates it. Their own players still are.</summary>
	private static void HostIgnorePeople( Scene scene )
	{
		var world = scene.PhysicsWorld;
		if ( world.GetCollisionRule( Tag, "capsule" ) == CollisionRules.Result.Ignore ) return;
		// A copy: the rules the world starts with are the project's own, which every scene shares.
		var rules = new CollisionRules();
		rules.Deserialize( world.CollisionRules.Serialize().ToJsonString() );
		rules.Pairs[(Tag, "capsule")] = CollisionRules.Result.Ignore;
		rules.Deserialize( rules.Serialize().ToJsonString() );
		world.CollisionRules = rules;
	}

	/// <summary>What its boxes make up, in its own space.</summary>
	private BBox Shape()
	{
		BBox? shape = null;
		foreach ( var box in GameObject.Components.GetAll<BoxCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			var local = WorldTransform.ToLocal( box.WorldTransform );
			var half = box.Scale / 2f;
			foreach ( var corner in new BBox( box.Center - half, box.Center + half ).Corners )
			{
				var point = local.PointToWorld( corner );
				shape = shape is { } so ? so.AddPoint( point ) : BBox.FromPositionAndSize( point );
			}
		}
		return shape ?? BBox.FromPositionAndSize( Vector3.Zero, 64f );
	}

	protected override void OnFixedUpdate()
	{
		if ( !_restored || !Networking.IsHost || Body is not { } body || HexagonServices.Current?.Roster is null ) return;
		var driver = HostDriver;
		var fuel = Remembered().Fuel;
		// Nobody at the wheel, nothing pressed.
		if ( driver is null ) (_throttle, _steer, _brake) = (0f, 0f, false);
		var running = driver is not null && fuel > 0;
		_steering = _steering.Approach( _steer, SteerRate * Time.Delta );
		if ( running && _throttle != 0f ) body.PhysicsBody.Sleeping = false;
		var travelled = HostDrive( body, running ? _throttle : 0f, parked: driver is null );
		if ( running && _throttle != 0f ) HostBurn( travelled, driver! );
		HostStrike( body, driver );
	}

	/// <summary>
	/// Host: one step of its wheels on the road. Each is a spring between its mount and the ground under
	/// it, pushing up by how far it is pressed in, and a tyre that holds the road up to its grip, along it
	/// and across it: driven forward and back, braked, or let roll. Returns how far it went forward or back.
	/// </summary>
	private float HostDrive( Rigidbody body, float throttle, bool parked )
	{
		var step = Time.Delta;
		if ( step <= 0f || _wheels.Length == 0 ) return 0f;
		var mass = body.Mass;
		var share = mass / _wheels.Length;
		var gravity = MathF.Abs( Scene.PhysicsWorld.Gravity.z );
		// Stiff enough that its weight presses each wheel halfway in, damped to just short of bouncing.
		var stiffness = share * gravity / Travel;
		var damping = 2f * 0.6f * MathF.Sqrt( stiffness * share );
		var free = 2f * Travel;
		var up = WorldRotation.Up;
		var heading = Vector3.Dot( body.Velocity, WorldRotation.Forward );
		var driven = _wheels.Count( value => value.Drives && value.Grounded );
		var braking = _brake || (throttle != 0f && heading * throttle < 0f && MathF.Abs( heading ) > 20f);
		var limit = throttle > 0f ? TopSpeed : TopSpeed / 3f;
		foreach ( var wheel in _wheels )
		{
			var mount = wheel.Mount( Travel );
			var trace = Scene.Trace.Ray( mount, mount - up * (free + wheel.Radius) )
				.IgnoreGameObjectHierarchy( GameObject )
				.WithoutTags( "player", "item", "corpse", "sleeper" )
				.Run();
			var length = trace.Hit ? MathF.Max( 0f, trace.Distance - wheel.Radius ) : free;
			var lengthening = wheel.Grounded ? (length - wheel.Length) / step : 0f;
			wheel.Length = length;
			wheel.Grounded = trace.Hit;
			if ( !trace.Hit ) continue;
			var load = Vehicles.Spring( stiffness, damping, free, length, lengthening );
			// Along the ground, the way the wheel points; and across it.
			var normal = trace.Normal;
			var pointing = (wheel.Steers ? WorldRotation * Rotation.FromYaw( _steering * SteerAngle ) : WorldRotation).Forward;
			var along = (pointing - normal * Vector3.Dot( pointing, normal )).Normal;
			var across = Vector3.Cross( normal, along ).Normal;
			var velocity = body.GetVelocityAtPoint( mount );
			var hold = Grip * load;
			var side = Vehicles.Hold( Vector3.Dot( velocity, across ), share * Firmness, step, hold );
			var rolling = Vector3.Dot( velocity, along );
			float push;
			if ( parked ) push = Vehicles.Hold( rolling, share, step, hold );
			else if ( braking ) push = Vehicles.Hold( rolling, share, step, MathF.Min( hold, Braking * share ) );
			else if ( throttle != 0f && wheel.Drives && driven > 0 && rolling * MathF.Sign( throttle ) < limit )
				push = Math.Clamp( throttle * Pull * mass / driven, -hold, hold );
			else push = Vehicles.Hold( rolling, share, step, MathF.Min( hold, Rolling * share ) );
			body.ApplyForceAt( mount, up * load + across * side + along * push );
		}
		return heading * step;
	}

	/// <summary>Host: driving under power burns fuel by the distance. An empty tank stops it pulling, and its driver is told once.</summary>
	private void HostBurn( float travelled, Player driver )
	{
		var data = Remembered();
		data.Fuel = Vehicles.Burn( data.Fuel, MathF.Abs( travelled ), Burns / 1000.0 );
		var shown = Shown( data.Fuel );
		if ( Gauge != shown ) Gauge = shown;
		if ( data.Fuel > 0 ) _toldEmpty = false;
		else if ( !_toldEmpty )
		{
			_toldEmpty = true;
			if ( driver.Network.Owner is { } owner ) Chat.Tell( owner, Vehicles.Empty );
		}
	}

	/// <summary>
	/// Host: whoever is in its way as it closes on them faster than a walk is struck, by how fast, once
	/// for a while, and on the record under whoever was at its wheel. Someone sitting in a vehicle, or
	/// already down, is not.
	/// </summary>
	private void HostStrike( Rigidbody body, Player? driver )
	{
		if ( body.Velocity.Length <= Vehicles.SafeSpeed ) return;
		var reach = _shape.Grow( StrikeReach );
		foreach ( var player in Scene.GetAllComponents<Player>() )
		{
			if ( player.HostCharacter is not { IsDown: false } character || player.HostSeat is not null ) continue;
			var at = player.HostPosition + Vector3.Up * 36f;
			var local = WorldTransform.PointToLocal( at );
			if ( !reach.Contains( local ) ) continue;
			var nearest = WorldTransform.PointToWorld( _shape.ClosestPoint( local ) );
			var toward = (at - nearest).WithZ( 0f );
			if ( toward.Length < 1f ) toward = (at - WorldTransform.PointToWorld( _shape.Center )).WithZ( 0f );
			toward = toward.Normal;
			var speed = Vector3.Dot( body.GetVelocityAtPoint( nearest ), toward );
			if ( Vehicles.Strike( speed, Places.Body ) is null ) continue;
			if ( _struck.TryGetValue( character.Id, out var since ) && since < StrikeEvery ) continue;
			_struck[character.Id] = 0;
			// The leg on the side it comes from.
			var facing = (player as IEmbodiment).Renderer?.WorldRotation ?? player.WorldRotation;
			var leg = Vector3.Dot( -toward, facing.Right ) > 0f ? Places.RightLeg : Places.LeftLeg;
			var blow = Vehicles.Strike( speed, leg )!.Value;
			var by = driver?.HostCharacter is { } driving ? Actor.Of( driving ) : Actor.Console;
			Player.HostRecordAt( JournalKinds.VehicleStrike, by, player.HostPosition, character.HolderLabel,
				data: new[] { ("vehicle", Title), ("speed", $"{speed:0}"), ("place", blow.Place) } );
			if ( player.GetComponent<CharacterBody>() is Component.IDamageable struck )
				struck.OnDamage( new CharacterHarm
				{
					Attacker = driver?.GameObject, Damage = blow.Amount, Origin = nearest - toward * 32f, Position = at,
					Cause = Title.ToLowerInvariant(), Place = blow.Place, Wounding = blow.Wounding, Lethal = blow.Lethal
				} );
		}
	}

	/// <summary>Host: what the driver's hands and feet are doing, as they send it.</summary>
	internal void HostControl( float throttle, float steer, bool brake ) => (_throttle, _steer, _brake) = (throttle, steer, brake);

	/// <summary>Host: everything that is to be kept of it is put with the world, to be saved with it.</summary>
	public void HostKeep()
	{
		if ( !_restored ) return;
		var data = Remembered();
		data.Position = new[] { WorldPosition.x, WorldPosition.y, WorldPosition.z };
		data.Yaw = WorldRotation.Yaw();
	}

	/// <summary>Host: what the staff and tests are told of it.</summary>
	public string HostDescribe() =>
		$"{GameObject.Name}: at={WorldPosition.x:0},{WorldPosition.y:0},{WorldPosition.z:0} yaw={WorldRotation.Yaw():0} speed={Body?.Velocity.Length ?? 0f:0} " +
		$"fuel={Remembered().Fuel:0.##} driver='{HostDriver?.HostCharacter?.Name}' riders={Seats.Count( value => value.HostOccupant is not null )}";

	/// <summary>Host: tops its tank up or drains it, for staff and tests.</summary>
	public void HostSetFuel( double fuel )
	{
		Remembered().Fuel = Math.Clamp( fuel, 0, Tank );
		Gauge = Shown( Remembered().Fuel );
		HexagonServices.Current?.SaveWorld();
	}

	private float Shown( double fuel ) => MathF.Round( (float)(fuel / Tank) * 100f ) / 100f;

	// Driving, riding or filling it up are chosen from a menu: a passenger does not try the ignition by accident.
	bool IVerbTarget.UseOpensMenu => true;

	// Sitting in it, Use is for getting out.
	bool IPressable.CanPress( IPressable.Event e ) => Player.Local is { IsSeated: false };

	bool IPressable.Press( IPressable.Event e )
	{
		if ( Player.Local is { } local ) local.MenuTarget = this;
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( Title, "local_shipping",
			(LockPattern.Length > 0 ? "It starts with a key. " : string.Empty) + $"The gauge reads {Gauge * 100f:0}%." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		switch ( verb.Id )
		{
			case VerbIds.VehicleDrive:
				if ( LockPattern.Length > 0 && !actor.HostHasKey( LockPattern ) ) return Result.Fail( ErrorCode.Denied, Vehicles.NoKey );
				if ( Remembered().Fuel <= 0 ) return Result.Fail( ErrorCode.Conflict, Vehicles.Empty );
				if ( Seats.FirstOrDefault( value => value.Driver ) is not { } wheel ) return Result.Fail( ErrorCode.NotFound, "It has no seat at the wheel." );
				if ( wheel.HostOccupant is not null ) return Result.Fail( ErrorCode.Conflict, Vehicles.Driven );
				return actor.HostSit( wheel );
			case VerbIds.VehicleRide:
				if ( Seats.FirstOrDefault( value => !value.Driver && value.HostOccupant is null ) is not { } free ) return Result.Fail( ErrorCode.Conflict, Vehicles.NoSeat );
				return actor.HostSit( free );
			case VerbIds.VehicleFuel:
				return HostFill( actor );
			default:
				return Result.Fail( ErrorCode.Invalid, "That cannot be done." );
		}
	}

	/// <summary>Host: the fuel in the actor's hand goes into the tank, whole, and the can is used up, as one change.</summary>
	private Result HostFill( Player actor )
	{
		if ( HexagonServices.Current is not { Transfers: { } transfers } game || actor.HostCharacter is not { } character ) return Result.Fail( ErrorCode.Internal, "The city is not ready." );
		var held = actor.HostHeld;
		var can = held is null ? 0 : ItemDefinition.Find( held.Definition )?.Fuel ?? 0;
		var data = Remembered();
		var filled = Vehicles.Fill( data.Fuel, Tank, can );
		if ( !filled.Ok ) return filled.ToResult();
		var used = transfers.Destroy( Sinks.Consumed, character, held!.Id, Actor.Of( character ), count: 1 );
		if ( !used.Ok ) return used;
		data.Fuel = filled.Value;
		Gauge = Shown( data.Fuel );
		game.SaveTogether( character, game.World );
		Player.HostRefresh( character );
		if ( actor.Network.Owner is { } owner ) Chat.Tell( owner, $"You fill the tank. The gauge reads {Gauge * 100f:0}%." );
		return Result.Success();
	}

	/// <summary>Host: what it remembers, kept with the world.</summary>
	private VehicleData Remembered()
	{
		var world = HexagonServices.Current!.World;
		if ( !world.Vehicles.TryGetValue( GameObject.Id, out var data ) ) world.Vehicles[GameObject.Id] = data = new VehicleData { Fuel = Math.Min( StartingFuel, Tank ) };
		return data;
	}
}

/// <summary>
/// A seat in a vehicle, at its wheel or beside it. Someone sitting in it is carried with it, posed by
/// the engine's own sitting, and looks out from where its eyes would be. Use gets them out, to where
/// there is room beside it.
/// </summary>
[Title( "Hexagon Seat" ), Category( "Hexagon" ), Icon( "airline_seat_recline_normal" )]
public sealed class Seat : Component, ISitTarget
{
	/// <summary>At the wheel: whoever sits here drives.</summary>
	[Property] public bool Driver { get; set; }
	[Property] public BaseChair.AnimatorSitPose Pose { get; set; } = BaseChair.AnimatorSitPose.Chair;
	/// <summary>Where the eyes are, from the seat.</summary>
	[Property] public Vector3 Eyes { get; set; } = new( 4f, 0f, 28f );
	/// <summary>Where someone getting out stands, from the seat, before looking for room anywhere else around it.</summary>
	[Property] public Vector3 Exit { get; set; } = new( 0f, 80f, -30f );
	/// <summary>How far round from straight ahead someone sitting here can turn to look.</summary>
	[Property, Range( 0f, 180f )] public float LookAround { get; set; } = 120f;

	private Player? _occupant;

	/// <summary>Host: who sits here. A pawn that has gone, or got out some other way, sits nowhere.</summary>
	public Player? HostOccupant
	{
		get => _occupant.IsValid() && _occupant.HostSeat == this ? _occupant : null;
		internal set => _occupant = value;
	}

	public Vehicle? Vehicle => GameObject.Components.GetInAncestorsOrSelf<Vehicle>();

	void ISitTarget.UpdatePlayerAnimator( PlayerController controller, SkinnedModelRenderer renderer )
	{
		controller.LocalTransform = global::Transform.Zero;
		renderer.LocalRotation = Rotation.Identity;
		renderer.Set( "sit", (int)Pose );
		renderer.Set( "sit_offset_height", 0f );
		renderer.Set( "b_grounded", true );
		renderer.Set( "b_swim", false );
		renderer.Set( "duck", false );
		var looking = controller.EyeTransform.Forward;
		renderer.SetLookDirection( "aim_eyes", looking, controller.AimStrengthEyes );
		renderer.SetLookDirection( "aim_head", looking, controller.AimStrengthHead );
		renderer.SetLookDirection( "aim_body", looking, controller.AimStrengthBody );
		Hold( controller );
	}

	Transform ISitTarget.CalculateEyeTransform( PlayerController controller )
	{
		Hold( controller );
		return new Transform( WorldTransform.PointToWorld( Eyes ), WorldRotation * controller.EyeAngles.ToRotation() );
	}

	void ISitTarget.AskToLeave( PlayerController controller )
	{
		if ( controller.GetComponent<Player>() is { IsProxy: false } player ) player.LeaveSeat();
	}

	/// <summary>Keeps the eyes within what can be seen from the seat.</summary>
	private void Hold( PlayerController controller )
	{
		var angles = controller.EyeAngles;
		angles.pitch = Math.Clamp( angles.pitch, -80f, 70f );
		angles.yaw = Math.Clamp( angles.yaw, -LookAround, LookAround );
		controller.EyeAngles = angles;
	}

	/// <summary>
	/// Host: where someone getting out stands: beside the seat, or else on the other side, behind, or in
	/// front, on the ground, wherever there is room for a body.
	/// </summary>
	public Vector3 HostExitPoint()
	{
		var mirrored = Exit.WithY( -Exit.y );
		var vehicle = Vehicle?.GameObject ?? GameObject;
		var bounds = vehicle.GetBounds();
		var around = new[] { Exit, mirrored }.Select( value => WorldTransform.PointToWorld( value ) )
			.Concat( new[] { vehicle.WorldRotation.Backward, vehicle.WorldRotation.Forward }
				.Select( value => bounds.Center + value * (MathF.Abs( value.x ) * bounds.Extents.x + MathF.Abs( value.y ) * bounds.Extents.y + 40f) ) );
		var body = BBox.FromPositionAndSize( Vector3.Up * 40f, new Vector3( 32f, 32f, 64f ) );
		foreach ( var spot in around )
		{
			var floor = Scene.Trace.Ray( spot.WithZ( bounds.Maxs.z ), spot.WithZ( bounds.Mins.z - 256f ) )
				.WithoutTags( "player", "item", "corpse", "sleeper" )
				.IgnoreGameObjectHierarchy( vehicle )
				.Run();
			if ( !floor.Hit ) continue;
			var at = floor.HitPosition + Vector3.Up * 2f;
			var room = Scene.Trace.Box( body, at, at ).WithoutTags( "player", "item", "corpse", "sleeper" ).Run();
			if ( !room.Hit && !room.StartedSolid ) return at;
		}
		// Nowhere is clear: on top of it.
		return bounds.Center.WithZ( bounds.Maxs.z + 2f );
	}
}

/// <summary>
/// The back of a truck, a holder that goes where the truck does, opened by a key cut to the truck's
/// lock if it has one.
/// </summary>
[Title( "Hexagon Truck Bed" ), Category( "Hexagon" ), Icon( "inventory_2" )]
public sealed class Bed : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Truck bed";
	[Property, Range( 1, 10 )] public int Width { get; set; } = 6;
	[Property, Range( 1, 10 )] public int Height { get; set; } = 4;

	public IReadOnlyList<Verb> Verbs { get; } = new[] { new Verb( VerbIds.BedOpen, "Open" ) };

	private Vehicle? Vehicle => GameObject.Components.GetInAncestorsOrSelf<Vehicle>();

	bool IPressable.CanPress( IPressable.Event e ) => Player.Local is { IsSeated: false };

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, VerbIds.BedOpen );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Open the {Title.ToLowerInvariant()}", "inventory_2", Vehicle is { LockPattern.Length: > 0 } ? "It locks with the truck's key." : "What the truck carries." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( Vehicle is { LockPattern.Length: > 0 } vehicle && !actor.HostHasKey( vehicle.LockPattern ) ) return Result.Fail( ErrorCode.Denied, "It is locked, and you have no key that fits." );
		actor.HostOpen( this, Holder(), Title );
		return Result.Success();
	}

	/// <summary>Host: what it carries.</summary>
	internal HolderData Holder()
	{
		var holders = HexagonServices.Current!.Holders!;
		return holders.Find( GameObject.Id ) ?? holders.GetOrCreate( GameObject.Id, HolderKinds.Bed, Title, Width, Height );
	}
}

/// <summary>
/// One of a vehicle's wheels: where it is mounted, and what it does. It is placed where the wheel's
/// middle is with the vehicle standing at rest; its mount is above that by half its travel.
/// </summary>
[Title( "Hexagon Wheel" ), Category( "Hexagon" ), Icon( "trip_origin" )]
public sealed class Wheel : Component
{
	[Property, Range( 1f, 100f )] public float Radius { get; set; } = 16f;
	/// <summary>It turns with the steering.</summary>
	[Property] public bool Steers { get; set; }
	/// <summary>The engine drives it.</summary>
	[Property] public bool Drives { get; set; }

	// Host-side: how far its spring reaches, and whether it touched the road, at the last step.
	internal float Length { get; set; }
	internal bool Grounded { get; set; }

	/// <summary>The top of its travel, in the world.</summary>
	internal Vector3 Mount( float travel ) => WorldPosition + WorldRotation.Up * travel;

	protected override void DrawGizmos()
	{
		Gizmo.Draw.Color = Steers ? Color.Cyan : Color.White;
		Gizmo.Draw.LineCircle( Vector3.Zero, Vector3.Left, Radius );
	}
}
