#nullable enable

using System;

namespace Hexagon.Logic;

/// <summary>
/// Trucks, and anything else driven: what its tank takes and what driving burns, what its driver's
/// hands can ask of it, how its wheels hold it up and hold the road, and what it does to whoever it
/// strikes. A truck is driven by whoever sits at its wheel with a key cut to its lock, on the fuel in
/// its tank, and nothing else.
/// </summary>
public static class Vehicles
{
	public const string NoKey = "It will not start without its key.";
	public const string Empty = "The tank is empty.";
	public const string Driven = "Someone is at the wheel.";
	public const string NoSeat = "There is no seat free.";
	public const string Seated = "You are already sitting in something.";
	public const string NoFuel = "You need fuel in hand to fill the tank.";
	public const string TankFull = "The tank cannot take a whole can.";

	/// <summary>Below this speed a truck only nudges whoever it meets, in units a second: a walk.</summary>
	public const float SafeSpeed = 150f;
	/// <summary>From this speed a truck that strikes someone breaks a leg.</summary>
	public const float WoundSpeed = 300f;
	/// <summary>Harm for each unit a second faster than <see cref="SafeSpeed"/>.</summary>
	public const float HarmPerSpeed = 0.2f;

	/// <summary>The fuel left after driving a distance, never below empty.</summary>
	public static double Burn( double fuel, double distance, double perUnit ) => Math.Max( 0d, fuel - Math.Max( 0d, distance ) * perUnit );

	/// <summary>The tank once a can is poured in. A can goes in whole or not at all, so nothing is spilled.</summary>
	public static Result<double> Fill( double fuel, double tank, double can )
	{
		if ( can <= 0 ) return Result<double>.Fail( ErrorCode.Invalid, NoFuel );
		if ( fuel + can > tank + 1e-6 ) return Result<double>.Fail( ErrorCode.Conflict, TankFull );
		return Result<double>.Success( fuel + can );
	}

	/// <summary>
	/// What a truck does to someone it strikes at a speed: nothing at a walk, harm for every bit faster
	/// than that, and a broken leg once it is fast. Harm only ever puts someone down; it never kills.
	/// </summary>
	public static Blow? Strike( float speed, string leg )
	{
		if ( speed <= SafeSpeed ) return null;
		var harm = (int)MathF.Ceiling( (speed - SafeSpeed) * HarmPerSpeed );
		return new Blow( harm, speed >= WoundSpeed ? leg : Places.Body, Wounding: speed >= WoundSpeed );
	}

	/// <summary>A driver's hand on a pedal or the wheel, in quarters from all the way back to all the way on, so a stick does not flood the host.</summary>
	public static float Control( float value ) => float.IsFinite( value ) ? Math.Clamp( MathF.Round( value * 4f ) / 4f, -1f, 1f ) : 0f;

	/// <summary>
	/// How hard a wheel's spring pushes up: by how far it is pressed in from its free length, less how fast
	/// it is springing out, and never pulling down.
	/// </summary>
	public static float Spring( float stiffness, float damping, float free, float length, float lengthening ) =>
		MathF.Max( 0f, stiffness * (free - length) - damping * lengthening );

	/// <summary>
	/// The force along one direction that would stop a wheel's share of the truck moving along it within a
	/// step, held to what the tyre can take before it slides.
	/// </summary>
	public static float Hold( float speed, float mass, float step, float limit ) =>
		step <= 0f ? 0f : Math.Clamp( -speed * mass / step, -limit, limit );
}
