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
/// A world-space position sample, engine-free so the auditor can be unit-tested without the Sandbox
/// runtime.
/// </summary>
public readonly record struct MovementSample( float X, float Y, float Z );

/// <summary>
/// The movement envelope the host audits against, and the same envelope the owning client's
/// <c>HexMoveModeWalk</c> configures its controller from, so client physics and host validation cannot
/// drift apart. Operator-tunable; see <c>docs/security.md</c>.
/// </summary>
public readonly record struct HexMovementEnvelope(
	float HorizontalTolerance,
	float AuditWindowSeconds,
	float InterpolationSlack,
	float StepHeight,
	float GroundAngleDegrees,
	float TeleportGuard,
	float Gravity,
	float FrozenDriftAllowance,
	float ViolationKickSeconds )
{
	// Declared as const because s&box's ConVar source generator copies a property initialiser into a
	// generated attribute argument, which must be a compile-time constant. Keeping the numbers here
	// means the ConVar defaults in HexagonRuntimeOverrides and Default stay one source of truth.
	public const float DefaultHorizontalTolerance = 1.25f;
	/// <summary>Seconds of travel each audit covers. Long enough that interpolation timing is noise.</summary>
	public const float DefaultAuditWindowSeconds = 1f;
	/// <summary>
	/// Endpoint slack for the interpolation buffer's delay. A proxy's position lags the client's by
	/// roughly the buffer depth, so a window's measured path can differ from the true path by about
	/// that much travel. Generous: it costs a little detection sensitivity, never a false positive.
	/// </summary>
	public const float DefaultInterpolationSlack = 96f;
	/// <summary>Matches the shipped <c>MoveModeWalk.StepUpHeight</c>.</summary>
	public const float DefaultStepHeight = 18f;
	/// <summary>Matches the shipped <c>MoveModeWalk.GroundAngle</c>.</summary>
	public const float DefaultGroundAngleDegrees = 45f;
	/// <summary>Instantaneous jump that no interpolation artifact can reach.</summary>
	public const float DefaultTeleportGuard = 384f;
	public const float DefaultGravity = 800f;
	/// <summary>Total travel tolerated per window while dead, locked, or without a character.</summary>
	public const float DefaultFrozenDriftAllowance = 24f;
	public const float DefaultViolationKickSeconds = 10f;

	public static HexMovementEnvelope Default => new(
		HorizontalTolerance: DefaultHorizontalTolerance,
		AuditWindowSeconds: DefaultAuditWindowSeconds,
		InterpolationSlack: DefaultInterpolationSlack,
		StepHeight: DefaultStepHeight,
		GroundAngleDegrees: DefaultGroundAngleDegrees,
		TeleportGuard: DefaultTeleportGuard,
		Gravity: DefaultGravity,
		FrozenDriftAllowance: DefaultFrozenDriftAllowance,
		ViolationKickSeconds: DefaultViolationKickSeconds );

	/// <summary>
	/// Steepest sustained rise a grounded player can achieve, as rise per unit of horizontal travel:
	/// you cannot climb faster than the steepest surface you are allowed to stand on.
	/// </summary>
	public float MaximumRisePerHorizontalUnit =>
		MathF.Tan( Math.Clamp( GroundAngleDegrees, 0f, 89f ) * (MathF.PI / 180f) );

	/// <summary>Apex of one unassisted jump, the slack a net-rise bound must tolerate.</summary>
	public float JumpApex( float jumpSpeed ) =>
		(jumpSpeed * jumpSpeed) / (2f * MathF.Max( Gravity, 1f ));

	/// <summary>
	/// Rejects an envelope an operator could set to something that audits nothing. A tunable envelope
	/// means a mis-set value is a hole, so the bounds are enforced rather than documented.
	/// </summary>
	public bool IsWellFormed( out string error )
	{
		error = string.Empty;
		if ( !(HorizontalTolerance >= 1f && HorizontalTolerance <= 2f) )
			error = "HorizontalTolerance must be between 1 and 2.";
		else if ( !(AuditWindowSeconds >= 0.25f && AuditWindowSeconds <= 10f) )
			error = "AuditWindowSeconds must be between 0.25 and 10.";
		else if ( !(InterpolationSlack >= 0f && InterpolationSlack <= 512f) )
			error = "InterpolationSlack must be between 0 and 512 units.";
		else if ( !(StepHeight >= 0f && StepHeight <= 64f) )
			error = "StepHeight must be between 0 and 64 units.";
		else if ( !(GroundAngleDegrees > 0f && GroundAngleDegrees <= 89f) )
			error = "GroundAngleDegrees must be above 0 and at most 89.";
		else if ( !(TeleportGuard > 0f && TeleportGuard <= 4096f) )
			error = "TeleportGuard must be above 0 and at most 4096 units.";
		else if ( !(Gravity > 0f && Gravity <= 4096f) )
			error = "Gravity must be above 0 and at most 4096 units per second squared.";
		else if ( !(FrozenDriftAllowance >= 0f && FrozenDriftAllowance <= 256f) )
			error = "FrozenDriftAllowance must be between 0 and 256 units.";
		else if ( !(ViolationKickSeconds > 0f && ViolationKickSeconds <= 300f) )
			error = "ViolationKickSeconds must be above 0 and at most 300.";
		return error.Length == 0;
	}
}

/// <summary>
/// Accumulated observation of one player across the current audit window.
/// </summary>
public readonly record struct MovementAudit(
	MovementSample Anchor,
	MovementSample Last,
	double WindowStartSeconds,
	float HorizontalPath,
	bool Primed )
{
	public static MovementAudit Unprimed => new( default, default, 0, 0f, false );

	public static MovementAudit OpenAt( MovementSample position, double nowSeconds ) =>
		new( position, position, nowSeconds, 0f, true );
}

/// <summary>
/// Host-side audit of a client-owned player's movement.
/// <para>
/// <b>Why this is a window and not a per-tick check.</b> The host cannot see what the client reported.
/// For a proxy, <c>GameObject.WorldPosition</c> is the INTERPOLATED transform — a reconstruction the
/// engine rebuilds from a network buffer; the raw received value lives in <c>GameTransform.TargetLocal</c>,
/// which is <c>internal</c> and reachable only by <c>NetworkObject</c>'s own send/receive paths. Its
/// per-tick deltas are artifacts of buffer depth and packet timing, not of player movement: measured
/// live on 2026-07-29, honest sprinting produced deltas quantised at 19.2 units against a per-tick
/// budget of ~15, twelve times identically. Per-tick validation is the industry idiom, but every engine
/// that uses it validates the client's CLAIMED position from a movement packet. We do not have that
/// input, and copying the idiom without it is what produced the defect.
/// </para>
/// <para>
/// Summing deltas over a window is immune to how the interpolator chunks the travel: however the path
/// is sliced, the sum over a window is the path. That is the property per-tick checking lacks, and it
/// is what makes this sound against the only signal the host can observe. It also audits the transform
/// every other player actually sees, rather than a side-channel claim a cheat could keep honest while
/// flying.
/// </para>
/// <para>
/// <b>What is enforced.</b> Instantaneously: a teleport guard, whose margin no interpolation artifact
/// approaches. Per window: horizontal path against the run envelope, and net rise against horizontal
/// path through the ground angle plus one jump's apex. A correction is issued at most once per window,
/// so a correction can no longer provoke the next one — the per-tick corrective loop was self-feeding
/// and is what players experienced as rubber-banding.
/// </para>
/// <para>
/// <b>What this costs.</b> Detection latency of about one window: gameplay resolves against the client's
/// position until an audit fails, so sub-teleport abuse is visible for up to that long. The teleport
/// guard still catches the gross case immediately.
/// </para>
/// </summary>
public static class HexMovementValidator
{
	public enum Verdict
	{
		/// <summary>Sample accepted; no action.</summary>
		Accepted,
		/// <summary>A single step no movement could produce. Corrected immediately.</summary>
		Teleport,
		/// <summary>The completed window exceeded the envelope. Corrected once, then the window reopens.</summary>
		WindowExceeded
	}

	public readonly record struct Decision(
		Verdict Verdict,
		bool WindowClosed,
		float HorizontalPath,
		float NetRise,
		float HorizontalBudget,
		float RiseBudget )
	{
		public bool Corrected => Verdict != Verdict.Accepted;
	}

	/// <summary>
	/// Observes one sampled position. Returns the verdict and the carried audit state.
	/// </summary>
	/// <param name="audit">Accumulated state for the current window.</param>
	/// <param name="reported">The position sampled from the proxy transform.</param>
	/// <param name="nowSeconds">Monotonic clock, used only for window length.</param>
	/// <param name="runSpeed">The controller's run speed, read from the live component.</param>
	/// <param name="jumpSpeed">The controller's jump speed, read from the live component.</param>
	/// <param name="frozen">Dead, movement-locked, or without a character: the player must not travel.</param>
	/// <param name="envelope">The operator-configured envelope.</param>
	public static (Decision Decision, MovementAudit Audit) Observe(
		MovementAudit audit,
		MovementSample reported,
		double nowSeconds,
		float runSpeed,
		float jumpSpeed,
		bool frozen,
		HexMovementEnvelope envelope )
	{
		if ( !(float.IsFinite( reported.X ) && float.IsFinite( reported.Y ) && float.IsFinite( reported.Z )) )
			return (new Decision( Verdict.Teleport, false, 0, 0, 0, 0 ), audit);

		if ( !audit.Primed )
			return (new Decision( Verdict.Accepted, false, 0, 0, 0, 0 ), MovementAudit.OpenAt( reported, nowSeconds ));

		var dx = reported.X - audit.Last.X;
		var dy = reported.Y - audit.Last.Y;
		var dz = reported.Z - audit.Last.Z;
		var step = MathF.Sqrt( (dx * dx) + (dy * dy) + (dz * dz) );

		// Instantaneous guard. Robust against this signal because the margin is far beyond anything the
		// interpolation buffer can emit in one sample.
		if ( step > envelope.TeleportGuard )
		{
			return (new Decision( Verdict.Teleport, false, audit.HorizontalPath, 0, 0, 0 ),
				MovementAudit.OpenAt( audit.Last, nowSeconds ));
		}

		// Accumulate the path. Chunking does not matter: the sum over the window is the path.
		var horizontalStep = MathF.Sqrt( (dx * dx) + (dy * dy) );
		var advanced = audit with
		{
			Last = reported,
			HorizontalPath = audit.HorizontalPath + horizontalStep
		};

		var elapsed = (float)Math.Max( nowSeconds - audit.WindowStartSeconds, 0.0 );
		if ( elapsed < envelope.AuditWindowSeconds )
			return (new Decision( Verdict.Accepted, false, advanced.HorizontalPath, 0, 0, 0 ), advanced);

		// --- Window closes: audit it. ---
		var netRise = advanced.Last.Z - advanced.Anchor.Z;

		var horizontalBudget = frozen
			? envelope.FrozenDriftAllowance
			: (MathF.Max( runSpeed, 1f ) * envelope.HorizontalTolerance * elapsed) + envelope.InterpolationSlack;

		var riseBudget = frozen
			? envelope.FrozenDriftAllowance
			: (advanced.HorizontalPath * envelope.MaximumRisePerHorizontalUnit)
				+ envelope.JumpApex( jumpSpeed ) + envelope.StepHeight;

		var exceeded = advanced.HorizontalPath > horizontalBudget || netRise > riseBudget;

		var decision = new Decision(
			exceeded ? Verdict.WindowExceeded : Verdict.Accepted,
			WindowClosed: true,
			advanced.HorizontalPath,
			netRise,
			horizontalBudget,
			riseBudget );

		// Always reopen at the current position, including on failure. A failed window is NOT snapped
		// back: yanking an honest player a full window's travel backwards is a worse experience than
		// the abuse it would prevent, and the per-tick snapping was what fed the rubber-banding. A
		// failed window escalates the violation clock instead, and sustained abuse ends in a kick.
		// Teleports remain corrected instantly above, where the snap is small and unambiguous.
		return (decision, MovementAudit.OpenAt( advanced.Last, nowSeconds ));
	}
}
