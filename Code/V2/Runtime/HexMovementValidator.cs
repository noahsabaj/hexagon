#nullable enable

using System;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Authority rules for whether a player body is usable by movement/interaction.
/// </summary>
public static class PlayerBodyAuthorityRules
{
	public static bool IsUsable(
		bool hasActiveCharacter,
		bool isDead,
		bool bodyIsValid,
		bool bodyIsEnabled ) =>
		hasActiveCharacter && !isDead && bodyIsValid && bodyIsEnabled;
}

/// <summary>
/// A world-space position sample, engine-free so the validator can be unit-tested
/// without the Sandbox runtime.
/// </summary>
public readonly record struct MovementSample( float X, float Y, float Z );

/// <summary>
/// Pure, engine-independent decision for host-side validation of a client-owned
/// player's reported movement. The owning client simulates its <c>PlayerController</c>
/// and networks its transform; the host bounds each reported position delta to the
/// controller's speed envelope and flags any excess for an authoritative correction.
/// <para>
/// Position is authority-bearing, not cosmetic: interaction reach, line-of-sight, combat
/// traces, and proximity chat all resolve against the player's position, so gameplay reads
/// the host's last-validated position (see <c>HexPlayerBody.AuthoritativeWorldPosition</c>),
/// never the raw client transform. The per-tick skin is a small jitter/impulse epsilon, not a
/// speed grant; a client that keeps reporting out-of-envelope deltas is corrected every tick
/// and, after a sustained run, kicked. Bounded within-envelope drift stays possible without
/// host-side ground-truth movement simulation — a movement-quality residual, not a
/// spatial-authority breach. The constants below are conservative framework defaults tunable
/// per game once the two-client run exercises real remote movement (jumps, slopes, impulses).
/// </para>
/// </summary>
public static class HexMovementValidator
{
	/// <summary>Slack multiplier on horizontal run speed (slopes, strafe, dt jitter).</summary>
	public const float HorizontalTolerance = 1.25f;
	/// <summary>Small absolute per-tick skin for float rounding, ground-snap, and modest impulses.</summary>
	public const float PositionSkin = 4f;
	/// <summary>Tighter skin for a player that must not move at all (dead / no character / locked).</summary>
	public const float FrozenSkin = 2f;
	/// <summary>Discrete vertical rise a legitimate step-up/steep slope adds in one tick — allowed
	/// only alongside horizontal motion, so a client cannot ascend straight up on it.</summary>
	public const float StepRise = 16f;
	/// <summary>Single-tick delta above which movement is treated as a hard teleport.</summary>
	public const float TeleportGuard = 384f;
	/// <summary>Upward slack multiplier over jump speed.</summary>
	public const float VerticalRiseTolerance = 1.5f;
	/// <summary>Downward (falling) envelope per second.</summary>
	public const float TerminalFall = 1800f;

	public readonly record struct Decision( bool Corrected );

	/// <summary>
	/// Evaluates one reported step. <paramref name="frozen"/> is true when the player
	/// must not move at all (dead, no character, or movement-locked); then any delta
	/// beyond the frozen skin is corrected.
	/// </summary>
	public static Decision Evaluate(
		MovementSample lastGood,
		MovementSample reported,
		float deltaSeconds,
		float runSpeed,
		float jumpSpeed,
		bool frozen )
	{
		if ( !(float.IsFinite( reported.X ) && float.IsFinite( reported.Y ) && float.IsFinite( reported.Z )) )
			return new Decision( true );

		var dx = reported.X - lastGood.X;
		var dy = reported.Y - lastGood.Y;
		var dz = reported.Z - lastGood.Z;
		var total = MathF.Sqrt( (dx * dx) + (dy * dy) + (dz * dz) );

		if ( frozen )
			return new Decision( total > FrozenSkin );

		if ( total > TeleportGuard )
			return new Decision( true );

		var dt = MathF.Max( deltaSeconds, 0f );
		var horizontal = MathF.Sqrt( (dx * dx) + (dy * dy) );
		var horizontalMax = (MathF.Max( runSpeed, 1f ) * HorizontalTolerance * dt) + PositionSkin;
		if ( horizontal > horizontalMax )
			return new Decision( true );

		// Vertical: jump/fall physics, plus a single discrete step/slope rise granted only when the
		// player is actually moving horizontally (a stationary player cannot step up). This keeps
		// stairs and steep slopes smooth while denying straight-up flight on the step allowance.
		var stepRise = horizontal > PositionSkin ? StepRise : 0f;
		var upMax = (MathF.Max( jumpSpeed, 1f ) * VerticalRiseTolerance * dt) + PositionSkin + stepRise;
		var downMax = (TerminalFall * dt) + PositionSkin;
		if ( dz > upMax || -dz > downMax )
			return new Decision( true );

		return new Decision( false );
	}
}
