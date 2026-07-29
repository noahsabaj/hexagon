#nullable enable

using System;
using Hexagon.V2.Runtime;
using Hexagon.V2.Tests.Foundation;
using static Hexagon.V2.Tests.Foundation.SustainedEnvelope.EngineDefaults;

namespace Hexagon.V2.Tests.Runtime;

/// <summary>
/// Sustained-run guards for the movement validator.
/// <para>
/// The single-step tests in <see cref="HexMovementValidatorTests"/> cannot express these: a validator
/// that clamps each step but carries no state re-grants its whole allowance every tick, so the per-tick
/// maximum is a sustainable rate — and a single-step test not only misses that, it blesses it, because
/// the step it declares legitimate is exactly the step the exploit repeats. Each test here drives a
/// GREEDY ADVERSARY — every tick it takes the largest step the validator will accept — and bounds the
/// accumulated result. That makes the guard independent of how the validator is implemented: it
/// measures what a cheat client can actually extract, not whether one hand-picked vector is refused.
/// </para>
/// </summary>
[TestClass]
public sealed class HexMovementValidatorSustainedTests
{
	private const int Ticks = (int)(10 * FixedUpdateHz);   // ten seconds
	private const float Dt = FixedDelta;
	private const double Elapsed = 10.0;

	private static readonly HexMovementEnvelope Envelope = HexMovementEnvelope.Default;

	/// <summary>
	/// The one seam between these assertions and the validator's signature. The assertions below are
	/// the contract and did not change when the validator gained state; only this did.
	/// </summary>
	private sealed class Adversary
	{
		private MovementState _state;
		private readonly bool _grounded;

		public Adversary( bool grounded )
		{
			_grounded = grounded;
			_state = MovementState.PrimedAt( new MovementSample( 0, 0, 0 ), Envelope );
		}

		public SustainedEnvelope.PositionTrack Track { get; } = new();

		private MovementSample Candidate( float dx, float dz ) =>
			new( _state.LastGood.X + dx, _state.LastGood.Y, _state.LastGood.Z + dz );

		/// <summary>Pure probe — the validator returns new state which is discarded, so bisecting is free.</summary>
		public bool Accepts( float dx, float dz, bool frozen ) =>
			!HexMovementValidator.Evaluate(
				_state, Candidate( dx, dz ), Dt, RunSpeed, JumpSpeed, frozen, _grounded, Envelope )
				.Decision.Corrected;

		public void Commit( float dx, float dz, bool frozen )
		{
			var (decision, next) = HexMovementValidator.Evaluate(
				_state, Candidate( dx, dz ), Dt, RunSpeed, JumpSpeed, frozen, _grounded, Envelope );
			if ( decision.Corrected ) return;   // the host snaps back; the baseline does not advance
			_state = next;
			Track.Accept( _state.LastGood.X, _state.LastGood.Y, _state.LastGood.Z );
		}
	}

	/// <summary>Largest value in [0, hi] the predicate still accepts, by bisection.</summary>
	private static float LargestAccepted( Func<float, bool> accepts, float hi )
	{
		if ( !accepts( 0f ) ) return 0f;
		var lo = 0f;
		for ( var i = 0; i < 40; i++ )
		{
			var mid = (lo + hi) / 2f;
			if ( accepts( mid ) ) lo = mid; else hi = mid;
		}
		return lo;
	}

	private static SustainedEnvelope.PositionTrack GreedyClimb( float horizontalPerTick, bool grounded )
	{
		var adversary = new Adversary( grounded );
		for ( var tick = 0; tick < Ticks; tick++ )
		{
			var dz = LargestAccepted( value => adversary.Accepts( horizontalPerTick, value, false ), 512f );
			adversary.Commit( horizontalPerTick, dz, false );
		}
		return adversary.Track;
	}

	[TestMethod]
	public void SustainedClimbCannotOutpaceTheGroundAngle()
	{
		// A legitimate player climbs by walking up a surface, and MoveModeWalk.GroundAngle caps that
		// surface at 45 degrees — so rise can never exceed horizontal travel, plus one jump's worth of
		// slack. Anything above that is a client ascending through open air while claiming to walk.
		var track = GreedyClimb( horizontalPerTick: 6f, grounded: true );
		var bound = (track.HorizontalDistance * MaximumRisePerHorizontalUnit) + JumpApexSlack;

		Assert.IsLessThanOrEqualTo( bound, track.Rise, SustainedEnvelope.Describe(
			$"Sustained climb over 10s while travelling {track.HorizontalDistance:F0}u horizontally",
			track.Rise, bound, "u" ) );
	}

	[TestMethod]
	public void SustainedStraightUpClimbIsBoundedByASingleJump()
	{
		// Airborne with no ground contact, the only legitimate rise is the jump already in progress,
		// and gravity ends it. This is the property RejectsStraightUpFlightWithoutHorizontalMotion is
		// named for but cannot assert — it rejects one oversized tick, not sustained flight.
		var track = GreedyClimb( horizontalPerTick: 0f, grounded: false );

		Assert.IsLessThanOrEqualTo( JumpApexSlack, track.Rise, SustainedEnvelope.Describe(
			"Straight-up climb over 10s with zero horizontal motion", track.Rise, JumpApexSlack, "u" ) );
	}

	[TestMethod]
	public void AirborneClimbIsBoundedEvenWhileMovingHorizontally()
	{
		// The exploit the old validator permitted: pair a little horizontal motion with a large rise to
		// unlock the step allowance every tick. With no ground beneath, no step allowance exists.
		var track = GreedyClimb( horizontalPerTick: 6f, grounded: false );

		Assert.IsLessThanOrEqualTo( JumpApexSlack, track.Rise, SustainedEnvelope.Describe(
			$"Airborne climb over 10s while travelling {track.HorizontalDistance:F0}u horizontally",
			track.Rise, JumpApexSlack, "u" ) );
	}

	[TestMethod]
	public void SustainedHorizontalSpeedStaysWithinTheRunEnvelope()
	{
		// The per-tick skin was documented as a jitter epsilon rather than a speed grant. At 50Hz a flat
		// per-tick allowance is worth 50x its value per second, so this bounds total distance as
		// rate x time plus ONE reservoir's worth of burst — not a per-tick step.
		var adversary = new Adversary( grounded: true );
		var run = SustainedEnvelope.Measure( Ticks, Dt, _ =>
		{
			var dx = LargestAccepted( value => adversary.Accepts( value, 0f, false ), 512f );
			adversary.Commit( dx, 0f, false );
			return SustainedEnvelope.StepOutcome.Allowed( dx );
		} );

		// Rate x time, plus one reservoir's worth of burst, plus a unit of float slack: the greedy
		// bisection lands a hair above the exact bound on each of 500 ticks.
		var bound = (RunSpeed * Envelope.HorizontalTolerance * Elapsed) + Envelope.SkinReservoir + 1.0;
		Assert.IsLessThanOrEqualTo( bound, run.TotalWork, SustainedEnvelope.Describe(
			$"Sustained horizontal travel over 10s ({run.WorkPerSecond:F0} u/s)",
			run.TotalWork, bound, "u" ) );
	}

	[TestMethod]
	public void FrozenPlayerCannotDriftAcrossASustainedRun()
	{
		// Frozen means dead, movement-locked, or without a character — the player must not travel at
		// all. A per-tick jitter tolerance still adds up to real distance over a restraint that lasts
		// minutes, which is the scenario that matters: a tied player walking away slowly.
		var adversary = new Adversary( grounded: true );
		var run = SustainedEnvelope.Measure( Ticks, Dt, _ =>
		{
			var dx = LargestAccepted( value => adversary.Accepts( value, 0f, true ), 64f );
			adversary.Commit( dx, 0f, true );
			return SustainedEnvelope.StepOutcome.Allowed( dx );
		} );

		// The reservoir is the whole lifetime allowance while frozen, not a per-tick one (plus a unit
		// of slack for bisection float noise across 500 ticks).
		var bound = Envelope.SkinReservoir + 1.0;
		Assert.IsLessThanOrEqualTo( bound, run.TotalWork, SustainedEnvelope.Describe(
			"Frozen drift over 10s", run.TotalWork, bound, "u" ) );
	}
}
