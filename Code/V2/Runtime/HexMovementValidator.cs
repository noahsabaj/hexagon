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
/// controller's speed envelope and flags gross teleport/speed/noclip for an
/// authoritative correction. Sub-threshold drift is deliberately tolerated — position
/// is low-stakes; all stateful authority lives in the command/persistence layer.
/// </summary>
public static class HexMovementValidator
{
	/// <summary>Slack multiplier on horizontal run speed (slopes, strafe, rounding).</summary>
	public const float HorizontalTolerance = 1.25f;
	/// <summary>Per-tick positional skin absorbing step/ground snapping and jitter.</summary>
	public const float StepSkin = 16f;
	/// <summary>Single-tick delta above which movement is treated as a hard teleport.</summary>
	public const float TeleportGuard = 512f;
	/// <summary>Upward slack multiplier over jump speed.</summary>
	public const float VerticalRiseTolerance = 1.5f;
	/// <summary>Downward (falling) envelope per second.</summary>
	public const float TerminalFall = 1800f;

	public readonly record struct Decision( bool Corrected );

	/// <summary>
	/// Evaluates one reported step. <paramref name="frozen"/> is true when the player
	/// must not move at all (dead, no character, or movement-locked); then any delta
	/// beyond the skin is corrected.
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
			return new Decision( total > StepSkin );

		if ( total > TeleportGuard )
			return new Decision( true );

		var dt = MathF.Max( deltaSeconds, 0f );
		var horizontal = MathF.Sqrt( (dx * dx) + (dy * dy) );
		var horizontalMax = (MathF.Max( runSpeed, 1f ) * HorizontalTolerance * dt) + StepSkin;
		if ( horizontal > horizontalMax )
			return new Decision( true );

		var upMax = (MathF.Max( jumpSpeed, 1f ) * VerticalRiseTolerance * dt) + StepSkin;
		var downMax = (TerminalFall * dt) + StepSkin;
		if ( dz > upMax || -dz > downMax )
			return new Decision( true );

		return new Decision( false );
	}
}
