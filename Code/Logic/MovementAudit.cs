#nullable enable

using System;
// Named apart from the engine's own Vector3, which is global in a game project.
using Vec3 = System.Numerics.Vector3;

namespace Hexagon.Logic;

/// <summary>
/// Movement is simulated by the owning client, so the position it reports is a claim. This keeps the
/// last position the host found believable, and every host rule that asks where a character is
/// reads <see cref="Position"/>, never the claim. A client that reports itself somewhere it could
/// not have reached gains nothing: the host still has it where it was, and sends it back there.
/// Travelling and rising are limited by speed. Falling is limited by gravity: a character may drop
/// as fast as something dropped would, and no faster, or a claim could pass through a floor to
/// whatever is underneath, which a play test once did from a roof to a door.
/// </summary>
public sealed class MovementAudit
{
	/// <summary>Claims are judged over windows at least this long, so network jitter averages out.</summary>
	public const double Window = 0.25;

	/// <summary>Downward acceleration and the speed it stops at, in the engine's units.</summary>
	public const float Gravity = 850f;
	public const float TerminalSpeed = 3500f;

	private readonly float _slack;
	private bool _started;
	private double _at;
	private double _settleUntil;
	private bool _awaitingArrival;
	/// <summary>How fast the host reckons the character is already falling.</summary>
	private float _fallSpeed;

	/// <param name="slack">Distance forgiven per window, for jitter and being pushed by physics.</param>
	public MovementAudit( float slack = 48f ) => _slack = slack;

	/// <summary>Where the host believes the character is.</summary>
	public Vec3 Position { get; private set; }

	/// <summary>
	/// The host moved the character itself. The owner applies that a moment later, so until it does,
	/// or until <paramref name="settleSeconds"/> pass, claims from the old place are ignored rather
	/// than judged. They are never accepted: a host teleport is not a free one for the client.
	/// </summary>
	public void Reset( Vec3 position, double now, double settleSeconds = 2.0 )
	{
		Position = position;
		_started = true;
		_at = now;
		_settleUntil = now + settleSeconds;
		_awaitingArrival = settleSeconds > 0;
		_fallSpeed = 0;
	}

	/// <summary>False when the claim is not believable. The believed position is then unchanged.</summary>
	public bool Observe( Vec3 claimed, double now, float maximumSpeed )
	{
		// Only the host says where a character starts. Until it has, a claim is neither believed nor
		// punished: adopting the first one would let a client choose where the host thinks it is.
		if ( !_started ) return true;
		var elapsed = now - _at;
		if ( elapsed < Window ) return true;

		var delta = claimed - Position;
		var travelled = MathF.Sqrt( delta.X * delta.X + delta.Y * delta.Y );

		if ( _awaitingArrival )
		{
			// The owner has not yet shown up where the host put it. Only showing up counts: anything
			// else is ignored while there is still time, and refused once there is not. It is never
			// accepted, however long the wait has made the window.
			var arrival = maximumSpeed * (float)Window + _slack;
			if ( travelled > arrival || MathF.Abs( delta.Z ) > arrival + 256f ) return now < _settleUntil;
			_awaitingArrival = false;
		}
		else
		{
			var seconds = (float)Math.Min( elapsed, 1.0 );
			var allowed = maximumSpeed * seconds + _slack;
			var drop = -delta.Z;
			var fall = _fallSpeed * seconds + 0.5f * Gravity * seconds * seconds + _slack;
			if ( travelled > allowed || delta.Z > allowed || drop > fall ) return false;
			_fallSpeed = drop > 1f ? MathF.Min( _fallSpeed + Gravity * seconds, TerminalSpeed ) : 0f;
		}

		Position = claimed;
		_at = now;
		return true;
	}
}
