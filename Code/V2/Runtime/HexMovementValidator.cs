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
/// The movement envelope a host validates against, and the same envelope the owning client's
/// <c>HexMoveModeWalk</c> configures its controller from — so client physics and host validation
/// cannot drift apart. Operator-tunable per game; see <c>docs/security.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Defaults mirror the shipped s&amp;box <c>PlayerController</c> and <c>MoveModeWalk</c> so a game that
/// does not tune anything is validated against the movement its clients actually perform:
/// <c>StepHeight</c> is <c>MoveModeWalk.StepUpHeight</c> (18) and <c>GroundAngleDegrees</c> is
/// <c>MoveModeWalk.GroundAngle</c> (45). Those two numbers previously existed only as a hand-picked
/// approximation on the host, which is how the host came to permit climbs no client could perform.
/// </para>
/// </remarks>
public readonly record struct HexMovementEnvelope(
	float HorizontalTolerance,
	float SkinReservoir,
	float StepHeight,
	float GroundAngleDegrees,
	float TeleportGuard,
	float TerminalFall,
	float Gravity,
	float CorrectionCooldownSeconds,
	float ViolationKickSeconds )
{
	// Declared as const, not just as the Default values, because the operator ConVars in
	// HexagonRuntimeOverrides initialise from them and s&box's ConVar source generator copies a
	// property initialiser into a generated attribute argument — which must be a compile-time
	// constant. Keeping the numbers here means the ConVar defaults and Default stay one source of
	// truth rather than two lists that must be kept equal.
	public const float DefaultHorizontalTolerance = 1.25f;
	public const float DefaultSkinReservoir = 8f;
	/// <summary>Matches the shipped <c>MoveModeWalk.StepUpHeight</c>.</summary>
	public const float DefaultStepHeight = 18f;
	/// <summary>Matches the shipped <c>MoveModeWalk.GroundAngle</c>.</summary>
	public const float DefaultGroundAngleDegrees = 45f;
	public const float DefaultTeleportGuard = 384f;
	public const float DefaultTerminalFall = 1800f;
	public const float DefaultGravity = 800f;
	public const float DefaultCorrectionCooldownSeconds = 0.35f;
	public const float DefaultViolationKickSeconds = 3.5f;

	public static HexMovementEnvelope Default => new(
		HorizontalTolerance: DefaultHorizontalTolerance,
		SkinReservoir: DefaultSkinReservoir,
		StepHeight: DefaultStepHeight,
		GroundAngleDegrees: DefaultGroundAngleDegrees,
		TeleportGuard: DefaultTeleportGuard,
		TerminalFall: DefaultTerminalFall,
		Gravity: DefaultGravity,
		CorrectionCooldownSeconds: DefaultCorrectionCooldownSeconds,
		ViolationKickSeconds: DefaultViolationKickSeconds );

	/// <summary>
	/// Steepest sustained rise a grounded player can achieve, as rise per unit of horizontal travel.
	/// Falls straight out of the ground angle: you cannot climb faster than the steepest surface you
	/// are allowed to stand on.
	/// </summary>
	public float MaximumRisePerHorizontalUnit =>
		MathF.Tan( Math.Clamp( GroundAngleDegrees, 0f, 89f ) * (MathF.PI / 180f) );

	/// <summary>
	/// Rejects an envelope an operator could set to something that validates nothing. A tunable
	/// envelope means a mis-set value is a security hole, so the bounds are enforced rather than
	/// documented.
	/// </summary>
	public bool IsWellFormed( out string error )
	{
		error = string.Empty;
		if ( !(HorizontalTolerance >= 1f && HorizontalTolerance <= 2f) )
			error = "HorizontalTolerance must be between 1 and 2.";
		else if ( !(SkinReservoir >= 0f && SkinReservoir <= 64f) )
			error = "SkinReservoir must be between 0 and 64 units.";
		else if ( !(StepHeight >= 0f && StepHeight <= 64f) )
			error = "StepHeight must be between 0 and 64 units.";
		else if ( !(GroundAngleDegrees > 0f && GroundAngleDegrees <= 89f) )
			error = "GroundAngleDegrees must be above 0 and at most 89.";
		else if ( !(TeleportGuard > 0f && TeleportGuard <= 4096f) )
			error = "TeleportGuard must be above 0 and at most 4096 units.";
		else if ( !(TerminalFall > 0f && TerminalFall <= 8192f) )
			error = "TerminalFall must be above 0 and at most 8192 units per second.";
		else if ( !(Gravity > 0f && Gravity <= 4096f) )
			error = "Gravity must be above 0 and at most 4096 units per second squared.";
		else if ( !(CorrectionCooldownSeconds >= 0f && CorrectionCooldownSeconds <= 5f) )
			error = "CorrectionCooldownSeconds must be between 0 and 5.";
		else if ( !(ViolationKickSeconds > 0f && ViolationKickSeconds <= 120f) )
			error = "ViolationKickSeconds must be above 0 and at most 120.";
		return error.Length == 0;
	}
}

/// <summary>
/// Carry-over state between validated ticks. The validator is stateful by design: a per-tick delta
/// clamp that keeps no state re-grants its whole allowance every tick, which turns every per-tick
/// maximum into a rate a client can sustain indefinitely.
/// </summary>
public readonly record struct MovementState(
	MovementSample LastGood,
	float VerticalSpeed,
	float StepBudget,
	float SkinBudget,
	bool Primed )
{
	public static MovementState Unprimed => new( default, 0f, 0f, 0f, false );

	public static MovementState PrimedAt( MovementSample position, HexMovementEnvelope envelope ) =>
		new( position, 0f, envelope.StepHeight, envelope.SkinReservoir, true );

	public MovementState At( MovementSample position ) => this with { LastGood = position };
}

/// <summary>
/// Pure, engine-independent decision for host-side validation of a client-owned player's reported
/// movement. The owning client simulates its <c>PlayerController</c> and networks its transform; the
/// host integrates each reported delta against a movement model and corrects anything the model cannot
/// produce.
/// <para>
/// Position is authority-bearing, not cosmetic: interaction reach, line-of-sight, combat traces, and
/// proximity chat all resolve against the player's position, so gameplay reads the host's last-validated
/// position (see <c>HexPlayerBody.AuthoritativeWorldPosition</c>), never the raw client transform.
/// </para>
/// <para>
/// <b>What this enforces, precisely.</b> Horizontal travel is bounded to the run envelope as a RATE over
/// any window, not per tick — the jitter allowance is a reservoir that refills only from unused budget,
/// so it absorbs a burst without raising the sustained ceiling. Vertical travel is integrated: rise is
/// paid for out of an inferred vertical speed that only a host-observed ground contact re-arms and that
/// gravity decays every airborne tick, so an airborne client's climb is bounded by one jump's apex. A
/// grounded client's rise is bounded by its horizontal travel through the ground angle — you cannot
/// climb faster than the steepest surface you may stand on.
/// </para>
/// <para>
/// <b>What survives.</b> A client that moves at exactly the permitted rate at all times is
/// indistinguishable from a fast honest player without host-side movement simulation, which is not
/// reintroduced here (host-simulating a client-owned body is what pinned players to the world origin).
/// That residual is bounded by the envelope above — roughly a 25% speed margin — and is a
/// movement-quality residual, not a spatial-authority breach.
/// </para>
/// </summary>
public static class HexMovementValidator
{
	public readonly record struct Decision( bool Corrected );

	/// <summary>
	/// Evaluates one reported step and returns the carry-over state for the next.
	/// </summary>
	/// <param name="state">Carry-over state from the previous validated tick.</param>
	/// <param name="reported">The position the owning client authored this tick.</param>
	/// <param name="deltaSeconds">Wall-clock seconds since the previous validated tick.</param>
	/// <param name="runSpeed">The controller's run speed, read from the live component.</param>
	/// <param name="jumpSpeed">The controller's jump speed, read from the live component.</param>
	/// <param name="envelope">The operator-configured movement envelope.</param>
	/// <param name="grounded">
	/// Whether the HOST observed ground beneath the reported position, via the controller's own
	/// <c>TraceBody</c>. This is not client-reported: <c>PlayerController.GroundObject</c> carries no
	/// <c>[Sync]</c>, so a proxy's grounded state is invisible and must be traced by the host. It is
	/// what makes the climb bound real rather than advisory.
	/// </param>
	/// <param name="frozen">
	/// True when the player must not move at all (dead, no character, or movement-locked); then only the
	/// jitter reservoir is available, so drift is bounded for the whole duration rather than per tick.
	/// </param>
	public static (Decision Decision, MovementState State) Evaluate(
		MovementState state,
		MovementSample reported,
		float deltaSeconds,
		float runSpeed,
		float jumpSpeed,
		bool frozen,
		bool grounded,
		HexMovementEnvelope envelope )
	{
		if ( !(float.IsFinite( reported.X ) && float.IsFinite( reported.Y ) && float.IsFinite( reported.Z )) )
			return (new Decision( true ), state);

		if ( !state.Primed )
			return (new Decision( false ), MovementState.PrimedAt( reported, envelope ));

		var dx = reported.X - state.LastGood.X;
		var dy = reported.Y - state.LastGood.Y;
		var dz = reported.Z - state.LastGood.Z;
		var total = MathF.Sqrt( (dx * dx) + (dy * dy) + (dz * dz) );

		if ( total > envelope.TeleportGuard )
			return (new Decision( true ), state);

		var dt = MathF.Max( deltaSeconds, 0f );
		var horizontal = MathF.Sqrt( (dx * dx) + (dy * dy) );
		var skin = state.SkinBudget;

		// --- Frozen: the reservoir is the entire lifetime allowance, so a restrained player drifts a
		// few units once rather than a few units per tick for the duration of the restraint.
		if ( frozen )
		{
			if ( total > skin ) return (new Decision( true ), state);
			return (new Decision( false ), state.At( reported ) with { SkinBudget = skin - total } );
		}

		// --- Horizontal: a rate bound over any window. The reservoir refills ONLY from budget the
		// player did not use, so a burst is absorbed but the sustained ceiling stays at the envelope.
		var horizontalBudget = MathF.Max( runSpeed, 1f ) * envelope.HorizontalTolerance * dt;
		if ( horizontal <= horizontalBudget )
		{
			skin = MathF.Min( envelope.SkinReservoir, skin + (horizontalBudget - horizontal) );
		}
		else
		{
			var overdraft = horizontal - horizontalBudget;
			if ( overdraft > skin ) return (new Decision( true ), state);
			skin -= overdraft;
		}

		// --- Vertical: integrated, not re-granted.
		// A host-observed ground contact re-arms jump speed and the discrete step allowance. Airborne,
		// gravity decays the available rise every tick, so a climb terminates at one jump's apex.
		var verticalSpeed = state.VerticalSpeed;
		var stepBudget = state.StepBudget;
		if ( grounded )
		{
			// Standing on ground ARMS a jump; it does not spend one. Spending jump speed while grounded
			// would hand every grounded tick a fresh jump's worth of rise, which is the same
			// re-granting mistake in a different place.
			verticalSpeed = MathF.Max( jumpSpeed, 0f );
		}
		else
		{
			verticalSpeed -= envelope.Gravity * dt;
		}

		if ( dz > 0f )
		{
			// Grounded rise is capped by the ground angle against horizontal travel: on the steepest
			// standable surface, rise equals horizontal distance. Airborne rise is paid strictly from
			// the integrated vertical speed, which only a ground contact re-arms.
			var baseAllowance = grounded
				? horizontal * envelope.MaximumRisePerHorizontalUnit
				: MathF.Max( verticalSpeed, 0f ) * dt;

			if ( dz <= baseAllowance )
			{
				// The step reservoir refills only from rise the player did not take, so a discrete lip
				// stays affordable while a continuous climb settles at exactly the ground-angle rate.
				if ( grounded )
					stepBudget = MathF.Min( envelope.StepHeight, stepBudget + (baseAllowance - dz) );
			}
			else
			{
				var overdraft = dz - baseAllowance;
				// A step-up requires ground to step from; it is not available in mid-air.
				var stepUsed = grounded ? MathF.Min( overdraft, stepBudget ) : 0f;
				stepBudget -= stepUsed;
				overdraft -= stepUsed;
				// The jitter reservoir deliberately does NOT fund rise. It refills from unused
				// HORIZONTAL budget, so letting it buy altitude would mean standing still paid for a
				// climb — a client hovering on the spot would refill it every tick and ascend forever
				// on jitter allowance alone.
				if ( overdraft > 0f ) return (new Decision( true ), state);
			}

			// Airborne, the accepted rise defines the next tick's vertical speed, capped by what was
			// actually granted so borrowed step/jitter budget cannot inflate the model. Grounded, the
			// armed jump speed stands.
			if ( !grounded )
				verticalSpeed = dt > 0f ? MathF.Min( dz / dt, MathF.Max( verticalSpeed, 0f ) ) : verticalSpeed;
		}
		else
		{
			var fallAllowance = (envelope.TerminalFall * dt) + skin;
			if ( -dz > fallAllowance ) return (new Decision( true ), state);
			verticalSpeed = dt > 0f ? MathF.Max( dz / dt, -envelope.TerminalFall ) : verticalSpeed;
		}

		return (new Decision( false ), new MovementState(
			reported, verticalSpeed, stepBudget, skin, true ));
	}
}
