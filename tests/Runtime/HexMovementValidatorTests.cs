#nullable enable

using System;
using Hexagon.V2.Runtime;
using static Hexagon.V2.Tests.Foundation.SustainedEnvelope.EngineDefaults;

namespace Hexagon.V2.Tests.Runtime;

/// <summary>
/// Guards for the windowed movement audit.
/// <para>
/// The host observes an INTERPOLATED proxy transform, not what the client reported, so its per-tick
/// deltas are artifacts of network buffering. The audit's soundness rests on one property —
/// <b>summing deltas over a window is invariant to how the travel is chunked</b> — and
/// <see cref="AnIdenticalPathIsMeasuredTheSameHoweverItIsChunked"/> is the test that pins it. Everything
/// else here is a bound stated over a window; nothing is stated per tick, because per tick the signal
/// carries no truth.
/// </para>
/// </summary>
[TestClass]
public sealed class HexMovementValidatorTests
{
	private static readonly HexMovementEnvelope Envelope = HexMovementEnvelope.Default;

	private static MovementSample At( float x, float y, float z ) => new( x, y, z );

	/// <summary>
	/// Walks a straight horizontal path of <paramref name="totalDistance"/> over
	/// <paramref name="seconds"/>, delivered in <paramref name="samples"/> chunks, and returns the
	/// decision of the window that closes.
	/// </summary>
	private static HexMovementValidator.Decision WalkHorizontally(
		float totalDistance, double seconds, int samples, bool frozen = false )
	{
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var last = default( HexMovementValidator.Decision );
		for ( var i = 1; i <= samples; i++ )
		{
			var t = seconds * i / samples;
			var x = totalDistance * i / samples;
			(last, audit) = HexMovementValidator.Observe(
				audit, At( (float)x, 0, 0 ), t, RunSpeed, JumpSpeed, frozen, Envelope );
		}
		return last;
	}

	[TestMethod]
	public void AnIdenticalPathIsMeasuredTheSameHoweverItIsChunked()
	{
		// THE property the whole design rests on. The interpolation buffer decides how travel is sliced
		// — smoothly, in bursts of three ticks, or irregularly — and the audit must be blind to that.
		// Per-tick checking is not: measured live, an honest sprint arrived as 19.2-unit bursts against
		// a ~15-unit per-tick budget and was refused twelve times.
		var smooth = WalkHorizontally( 300f, 1.0, samples: 60 );
		var bursty = WalkHorizontally( 300f, 1.0, samples: 20 );
		var coarse = WalkHorizontally( 300f, 1.0, samples: 5 );

		Assert.AreEqual( smooth.HorizontalPath, bursty.HorizontalPath, 0.01f,
			"Chunking changed the measured path; the audit is not sampling-invariant." );
		Assert.AreEqual( smooth.HorizontalPath, coarse.HorizontalPath, 0.01f,
			"Chunking changed the measured path; the audit is not sampling-invariant." );
	}

	[TestMethod]
	public void AnHonestSprintIsAcceptedHoweverItIsSampled()
	{
		// A player at exactly run speed for a full window, delivered every way the network might.
		foreach ( var samples in new[] { 60, 20, 12, 5 } )
		{
			var decision = WalkHorizontally( RunSpeed, 1.0, samples );
			Assert.IsTrue( decision.WindowClosed, $"Window did not close for {samples} samples." );
			Assert.IsFalse( decision.Corrected,
				$"Honest sprint flagged at {samples} samples/window: " +
				$"path {decision.HorizontalPath:F1} vs budget {decision.HorizontalBudget:F1}." );
		}
	}

	[TestMethod]
	public void ASpeedCheatIsFlagged()
	{
		// Twice run speed for a window: 640 units against a 496-unit budget.
		var decision = WalkHorizontally( RunSpeed * 2f, 1.0, samples: 60 );
		Assert.AreEqual( HexMovementValidator.Verdict.WindowExceeded, decision.Verdict,
			$"path {decision.HorizontalPath:F1} vs budget {decision.HorizontalBudget:F1}" );
	}

	[TestMethod]
	public void SustainedClimbBeyondTheGroundAngleIsFlagged()
	{
		// Rising far faster than the steepest standable surface would permit for the distance walked.
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var decision = default( HexMovementValidator.Decision );
		for ( var i = 1; i <= 60; i++ )
		{
			// 100 units of horizontal travel, 900 units of rise, in one window.
			(decision, audit) = HexMovementValidator.Observe(
				audit, At( 100f * i / 60f, 0, 900f * i / 60f ), i / 60.0,
				RunSpeed, JumpSpeed, false, Envelope );
		}
		Assert.AreEqual( HexMovementValidator.Verdict.WindowExceeded, decision.Verdict,
			$"rise {decision.NetRise:F1} vs budget {decision.RiseBudget:F1}" );
	}

	[TestMethod]
	public void AJumpAndLandIsNotAClimb()
	{
		// Net rise over a window is what "climbing" means. Jumping repeatedly nets nothing, and must
		// not accumulate into a violation the way summed positive rise would.
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var decision = default( HexMovementValidator.Decision );
		for ( var i = 1; i <= 60; i++ )
		{
			var t = i / 60.0;
			var phase = Math.Sin( t * Math.PI * 4 );           // two full jump arcs in the window
			var z = (float)(Math.Max( phase, 0 ) * 56.0);      // apex ~ one jump
			(decision, audit) = HexMovementValidator.Observe(
				audit, At( 200f * i / 60f, 0, z ), t, RunSpeed, JumpSpeed, false, Envelope );
		}
		Assert.IsFalse( decision.Corrected,
			$"Jumping while running was flagged: rise {decision.NetRise:F1} vs {decision.RiseBudget:F1}" );
	}

	[TestMethod]
	public void ATeleportIsRefusedImmediatelyWithoutWaitingForTheWindow()
	{
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var (decision, _) = HexMovementValidator.Observe(
			audit, At( 5000, 0, 0 ), 0.016, RunSpeed, JumpSpeed, false, Envelope );

		Assert.AreEqual( HexMovementValidator.Verdict.Teleport, decision.Verdict );
		Assert.IsFalse( decision.WindowClosed, "A teleport is caught on the sample, not at window end." );
	}

	[TestMethod]
	public void AnInterpolationSizedBurstIsNotMistakenForATeleport()
	{
		// The bursts that broke per-tick checking (~19 units) must be nowhere near the teleport guard.
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var (decision, _) = HexMovementValidator.Observe(
			audit, At( 19.2f, 0, 0 ), 0.016, RunSpeed, JumpSpeed, false, Envelope );
		Assert.AreEqual( HexMovementValidator.Verdict.Accepted, decision.Verdict );
	}

	[TestMethod]
	public void FrozenPlayersAreHeldToTheDriftAllowance()
	{
		var moved = WalkHorizontally( 200f, 1.0, samples: 60, frozen: true );
		Assert.AreEqual( HexMovementValidator.Verdict.WindowExceeded, moved.Verdict );

		var jitter = WalkHorizontally( Envelope.FrozenDriftAllowance * 0.5f, 1.0, samples: 60, frozen: true );
		Assert.IsFalse( jitter.Corrected, "Jitter within the frozen allowance must not be flagged." );
	}

	[TestMethod]
	public void CorrectsNonFinitePosition()
	{
		var audit = MovementAudit.OpenAt( At( 0, 0, 0 ), 0 );
		var (decision, _) = HexMovementValidator.Observe(
			audit, At( float.NaN, 0, 0 ), 0.016, RunSpeed, JumpSpeed, false, Envelope );
		Assert.AreEqual( HexMovementValidator.Verdict.Teleport, decision.Verdict );
	}

	[TestMethod]
	public void AnUnprimedAuditAdoptsTheFirstSampleWithoutFlagging()
	{
		var (decision, audit) = HexMovementValidator.Observe(
			MovementAudit.Unprimed, At( 500, 500, 500 ), 0, RunSpeed, JumpSpeed, false, Envelope );
		Assert.IsFalse( decision.Corrected );
		Assert.IsTrue( audit.Primed );
		Assert.AreEqual( 500f, audit.Anchor.X );
	}

	[TestMethod]
	public void AMalformedEnvelopeIsRejectedRatherThanAuditingNothing()
	{
		Assert.IsTrue( HexMovementEnvelope.Default.IsWellFormed( out _ ) );

		var permissive = HexMovementEnvelope.Default with { HorizontalTolerance = 50f };
		Assert.IsFalse( permissive.IsWellFormed( out var error ) );
		Assert.IsNotEmpty( error );

		var blind = HexMovementEnvelope.Default with { AuditWindowSeconds = 999f };
		Assert.IsFalse( blind.IsWellFormed( out _ ) );
	}
}
