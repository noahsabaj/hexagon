#nullable enable

using System;
// Named apart from the engine's own Vector3, which is global in a game project.
using Vec3 = System.Numerics.Vector3;

namespace Hexagon.Logic;

/// <summary>How one character has hold of someone else's body.</summary>
public enum HoldKind
{
	None,
	/// <summary>Dragged along the ground by the part taken hold of, with one hand, slowly.</summary>
	Drag,
	/// <summary>Carried over the shoulder, with both hands, at a walk.</summary>
	Carry,
	/// <summary>Led by the arm, standing with their hands bound, with one hand, at a walk.</summary>
	Lead
}

/// <summary>
/// Moving someone who cannot move themselves. The dead and the down are dragged along the ground by
/// whatever part is taken hold of, slowly and with one hand, or carried over the shoulder, at a walk
/// and with both hands; the bound are led by the arm, at a walk, with one. One pair of hands has hold
/// of a body at a time, and whatever stops the one moving it lets go of it, as does the body being
/// left somewhere the hands cannot follow. Where the hands are, which is where the body is drawn to,
/// is worked out here from where the mover stands and which way it faces. A truck's bed takes a few
/// bodies laid in it, which go wherever the truck goes.
/// </summary>
public static class Holds
{
	public const string HandsFull = "Your hands are full.";
	public const string BothHands = "You need both hands free to carry them.";
	public const string AlreadyHolding = "You already have hold of someone.";
	public const string Taken = "Someone else has hold of them.";
	public const string OnTheirFeet = "They are on their feet.";
	public const string NotBound = "Their hands are not bound.";
	public const string Sitting = "You cannot do that sitting down.";
	public const string NothingToLay = "You are not holding anyone to lay in it.";
	public const string NoRoom = "There is no room left in it.";
	public const string NobodyIn = "There is nobody in it.";

	/// <summary>How far ahead of the mover's feet the dragging hand is: an arm's length.</summary>
	public const float HandReach = 40f;
	/// <summary>How high the dragging hand is above the mover's feet: at the hip.</summary>
	public const float HandHeight = 30f;
	/// <summary>How high above the mover's feet a carried body's middle is: its belly on the shoulder, a little lower.</summary>
	public const float ShoulderHeight = 64f;
	/// <summary>How far to the mover's right of its middle the shoulder is.</summary>
	public const float ShoulderSide = 8f;
	/// <summary>How far ahead of the one leading them someone led walks.</summary>
	public const float LeadAhead = 20f;
	/// <summary>How far to the left of the one leading them someone led walks, at the leading hand.</summary>
	public const float LeadSide = 24f;
	/// <summary>How far a body may be from where the hands are before they lose hold of it, caught on something.</summary>
	public const float Stray = 120f;
	/// <summary>How far someone led may be from where they are led to before they are pulled there.</summary>
	public const float Slack = 96f;
	/// <summary>Seconds the one moving a body has to slow to the pace it is moved at before a faster claim is disbelieved.</summary>
	public const float Grace = 1.5f;

	/// <summary>
	/// Whether a mover may take hold of a body this way: the dead and the down are dragged or carried,
	/// someone standing bound is led, nobody has hold of two, nobody takes hold of a body someone else
	/// has, and carrying takes both hands. Whether the mover is able and near enough is judged before.
	/// </summary>
	public static Result ToTake( HoldKind kind, HoldKind holding, bool seated, bool handFull, bool limp, bool bound, bool heldByAnother )
	{
		if ( kind == HoldKind.None ) return Result.Fail( ErrorCode.Invalid, "That is no way to hold someone." );
		if ( seated ) return Result.Fail( ErrorCode.Denied, Sitting );
		if ( holding != HoldKind.None ) return Result.Fail( ErrorCode.Conflict, AlreadyHolding );
		if ( heldByAnother ) return Result.Fail( ErrorCode.Conflict, Taken );
		if ( kind == HoldKind.Lead )
		{
			if ( limp ) return Result.Fail( ErrorCode.Conflict, "They cannot stand." );
			if ( !bound ) return Result.Fail( ErrorCode.Conflict, NotBound );
		}
		else if ( !limp ) return Result.Fail( ErrorCode.Conflict, OnTheirFeet );
		if ( kind == HoldKind.Carry && handFull ) return Result.Fail( ErrorCode.Denied, BothHands );
		return Result.Success();
	}

	/// <summary>Whether a hold leaves a hand free, for a weapon, a door, or anything else held in one.</summary>
	public static bool LeavesAHand( HoldKind kind ) => kind != HoldKind.Carry;

	/// <summary>
	/// The fastest someone holding a body goes: dragging, at the pace of a limp; carrying or leading, at a
	/// walk. Infinite for no hold, which slows nobody.
	/// </summary>
	public static float Pace( HoldKind kind, float walk, float slow ) => kind switch
	{
		HoldKind.Drag => MathF.Min( walk, slow ),
		HoldKind.Carry or HoldKind.Lead => walk,
		_ => float.PositiveInfinity
	};

	/// <summary>Whether a body can be laid in a bed that has <paramref name="lying"/> in it already and room for <paramref name="room"/>.</summary>
	public static Result ToLayIn( HoldKind holding, int lying, int room )
	{
		if ( holding is not (HoldKind.Drag or HoldKind.Carry) ) return Result.Fail( ErrorCode.Invalid, NothingToLay );
		if ( lying >= room ) return Result.Fail( ErrorCode.Conflict, NoRoom );
		return Result.Success();
	}

	/// <summary>Where a dragging hand is, from the mover's feet and the way it faces: an arm's length ahead of it, at the hip.</summary>
	public static Vec3 Hand( Vec3 feet, float yaw ) => feet + Forward( yaw ) * HandReach + Vec3.UnitZ * HandHeight;

	/// <summary>Where a carried body's middle rests: on the mover's right shoulder.</summary>
	public static Vec3 Shoulder( Vec3 feet, float yaw ) => feet - Left( yaw ) * ShoulderSide + Vec3.UnitZ * ShoulderHeight;

	/// <summary>Where someone led is walked to: a little ahead of the one leading them, at their left hand.</summary>
	public static Vec3 Beside( Vec3 feet, float yaw ) => feet + Forward( yaw ) * LeadAhead + Left( yaw ) * LeadSide;

	/// <summary>Whether a body is too far from the hands holding it to be held any longer.</summary>
	public static bool Strays( Vec3 part, Vec3 hand ) => Vec3.Distance( part, hand ) > Stray;

	/// <summary>Level, the way something faces at a yaw in degrees: x ahead at 0, y ahead at 90.</summary>
	public static Vec3 Forward( float yaw ) => new( MathF.Cos( yaw * Degrees ), MathF.Sin( yaw * Degrees ), 0f );

	/// <summary>Level, to the left of the way something faces at a yaw in degrees.</summary>
	public static Vec3 Left( float yaw ) => new( -MathF.Sin( yaw * Degrees ), MathF.Cos( yaw * Degrees ), 0f );

	private const float Degrees = MathF.PI / 180f;
}
