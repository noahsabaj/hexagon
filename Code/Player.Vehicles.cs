#nullable enable

using System;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Riding in a vehicle, from the side of whoever sits in it. The host seats a character and gets it
// out again; its owner's client puts its pawn in the seat, which the engine poses and carries along,
// and takes it out where the host says. While it sits, the host has it wherever its seat is, and its
// own claims of where it is count for nothing. At the wheel, its hands and feet are all it sends.
public sealed partial class Player
{
	/// <summary>The seat this pawn sits in, as it is placed here: in the seat's object, which carries it.</summary>
	public Seat? Seat => GameObject.Parent?.GetComponent<Seat>();

	public bool IsSeated => Seat is not null;

	// Host-side.
	private Seat? _seat;
	private RealTimeSince _sinceSat;
	// The wheel and the pedals are worked far more often than anything else is asked for, so they have a budget of their own.
	private readonly RateLimiter _driving = new( capacity: 20, refillPerSecond: 10 );

	/// <summary>How long the owner has to put its pawn in its seat before the host takes it to have refused.</summary>
	public const float SitGrace = 3f;

	/// <summary>Host: the seat this character sits in.</summary>
	public Seat? HostSeat => Networking.IsHost ? _seat : null;

	/// <summary>Host: this character sits in a seat that is free. Whatever it was doing stops, and its owner puts it there.</summary>
	internal Result HostSit( Seat seat )
	{
		if ( _seat is not null ) return Result.Fail( ErrorCode.Conflict, Vehicles.Seated );
		if ( seat.HostOccupant is not null ) return Result.Fail( ErrorCode.Conflict, seat.Driver ? Vehicles.Driven : Vehicles.NoSeat );
		if ( Network.Owner is not { } owner ) return Result.Fail( ErrorCode.Internal, "There is nobody to sit there." );
		HostClose();
		HostStopLeaning( tell: true );
		HostStopWork();
		_seat = seat;
		seat.HostOccupant = this;
		_sinceSat = 0;
		HostFollowSeat();
		using ( Rpc.FilterInclude( owner ) ) ReceiveSit( seat );
		return Result.Success();
	}

	/// <summary>
	/// Host: this character gets out of its seat, onto the ground beside the vehicle, on the record.
	/// Returns where it stands, or null if it was not sitting.
	/// </summary>
	internal Vector3? HostLeaveSeat()
	{
		if ( _seat is not { } seat ) return null;
		var vehicle = seat.Vehicle;
		var at = seat.IsValid() ? seat.HostExitPoint() : HostPosition;
		if ( seat.IsValid() && seat.HostOccupant == this ) seat.HostOccupant = null;
		_seat = null;
		// Where a driver leaves a truck is kept at once, not at the next autosave.
		if ( seat.IsValid() && seat.Driver && vehicle.IsValid() )
		{
			vehicle.HostKeep();
			HexagonServices.Current?.SaveWorld();
		}
		HostRecord( JournalKinds.VehicleLeave, vehicle is null ? null : $"Vehicle:{vehicle.GameObject.Id:N}", data: ("vehicle", vehicle?.Title ?? string.Empty) );
		// Put down beside it, as a teleport is: the owner's claims from the seat are not a path it walked.
		_movement.Reset( at, Time.Now );
		_lastClaim = null;
		if ( Network.Owner is { } owner ) using ( Rpc.FilterInclude( owner ) ) ReceiveUnsit( at );
		return at;
	}

	/// <summary>Host: someone sitting stays only while the seat is there, they are able, and their pawn really is in it.</summary>
	private void HostSeatTick()
	{
		if ( _seat is not { } seat ) return;
		if ( !seat.IsValid() || IsIncapable || (_sinceSat > SitGrace && GameObject.Parent != seat.GameObject) )
		{
			HostLeaveSeat();
			return;
		}
		HostFollowSeat();
	}

	/// <summary>Host: where a character sitting is believed to be is wherever its seat has been carried.</summary>
	private void HostFollowSeat()
	{
		_movement.Reset( _seat!.WorldPosition, Time.Now, settleSeconds: 0 );
		_lastClaim = null;
	}

	/// <summary>Owner: gets out of the seat.</summary>
	public void LeaveSeat() => RequestLeaveSeat();

	[Rpc.Host]
	private void RequestLeaveSeat()
	{
		if ( Authorize( nameof( RequestLeaveSeat ), out _, out _, needsCharacter: false ) ) HostLeaveSeat();
	}

	/// <summary>
	/// The driver's hands and feet: the pedal, from all the way back to all the way down, the wheel, from
	/// full left to full right, and the brake. Sent whenever they change, and now and then besides.
	/// </summary>
	[Rpc.Host]
	public void RequestDrive( float throttle, float steer, bool brake )
	{
		var caller = Rpc.Caller;
		if ( !Networking.IsHost || HexagonServices.Current is not { Roster: not null } game || caller is null ) return;
		if ( caller != Network.Owner )
		{
			game.HostRefuse( caller, JournalKinds.RequestForged, _character?.HolderLabel ?? $"Player:{GameObject.Id:N}", ("request", nameof( RequestDrive )) );
			return;
		}
		// Too fast is dropped without a word: the next change, or the next reminder, puts it right.
		if ( !_driving.TryTake( Time.Now ) ) return;
		if ( _seat is not { Driver: true, Vehicle: { } vehicle } || IsIncapable ) return;
		vehicle.HostControl( Vehicles.Control( throttle ), Vehicles.Control( steer ), brake );
	}

	// Owner-side: what was last sent of the driver's hands and feet, and when.
	private (float Throttle, float Steer, bool Brake) _sentDrive;
	private RealTimeSince _sinceDrive;

	/// <summary>Owner: hands and feet a test drives with, in place of the keys, while set.</summary>
	public (float Throttle, float Steer, bool Brake)? DriveOverride { get; set; }

	/// <summary>How often, at most, the driver is reminded to the host while nothing changes.</summary>
	public const float DriveReminder = 0.5f;

	/// <summary>Owner: at the wheel, the keys that walk drive instead, and jump is the brake. Use gets out.</summary>
	private void DriveTick()
	{
		if ( Seat is not { } seat )
		{
			DriveOverride = null;
			return;
		}
		if ( !UiBusy && Controller is { } controller && Input.Pressed( controller.UseButton ) )
		{
			LeaveSeat();
			return;
		}
		if ( !seat.Driver ) return;
		var move = Input.AnalogMove;
		var wanted = DriveOverride ?? (UiBusy ? (0f, 0f, true) : (Vehicles.Control( move.x ), Vehicles.Control( move.y ), Input.Down( "Jump" )));
		if ( wanted == _sentDrive && _sinceDrive < DriveReminder ) return;
		_sentDrive = wanted;
		_sinceDrive = 0;
		RequestDrive( wanted.Throttle, wanted.Steer, wanted.Brake );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveSit( Seat seat )
	{
		if ( !seat.IsValid() || Controller is not { } controller ) return;
		controller.StopPressing();
		controller.Hovered = null;
		_sentDrive = default;
		GameObject.SetParent( seat.GameObject, false );
		GameObject.LocalTransform = global::Transform.Zero;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveUnsit( Vector3 position )
	{
		var facing = GameObject.Parent?.WorldRotation ?? Rotation.Identity;
		GameObject.SetParent( null, true );
		// The way it looked from the seat is the way it looks standing beside it.
		if ( Controller is { } controller ) controller.EyeAngles = (facing * controller.EyeAngles.ToRotation()).Angles().WithRoll( 0f );
		Arrive( position );
		PrivateVersion++;
	}
}
