#nullable enable

using Hexagon.V2.Runtime;
using static Hexagon.V2.Tests.Foundation.SustainedEnvelope.EngineDefaults;

namespace Hexagon.V2.Tests.Runtime;

/// <summary>
/// Single-step behaviour of the movement validator.
/// <para>
/// These bound one tick. They cannot bound a RATE, and must never be read as doing so — a step this
/// file blesses is a step a client can repeat fifty times a second. Every claim about what a client can
/// achieve over time lives in <see cref="HexMovementValidatorSustainedTests"/>, and any new allowance
/// added here needs a sustained guard there before it means anything.
/// </para>
/// </summary>
[TestClass]
public sealed class HexMovementValidatorTests
{
	private const float Dt = FixedDelta;
	private static readonly HexMovementEnvelope Envelope = HexMovementEnvelope.Default;

	private static MovementSample At( float x, float y, float z ) => new( x, y, z );

	private static bool Corrected(
		MovementSample from, MovementSample to, float dt, bool frozen, bool grounded ) =>
		HexMovementValidator.Evaluate(
			MovementState.PrimedAt( from, Envelope ), to, dt, RunSpeed, JumpSpeed, frozen, grounded, Envelope )
			.Decision.Corrected;

	[TestMethod]
	public void AcceptsMovementWithinTheRunSpeedEnvelope()
	{
		// Horizontal budget = 320 * 1.25 * (1/50) = 8u, plus the jitter reservoir.
		Assert.IsFalse( Corrected( At( 0, 0, 0 ), At( 9, 0, 0 ), Dt, frozen: false, grounded: true ) );
	}

	[TestMethod]
	public void CorrectsHorizontalMovementBeyondTheEnvelope() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 200, 0, 0 ), Dt, frozen: false, grounded: true ) );

	[TestMethod]
	public void CorrectsHardTeleportEvenAcrossALargeStep() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 1000, 0, 0 ), 1f, frozen: false, grounded: true ) );

	[TestMethod]
	public void FreezeCorrectsMovementButToleratesSkinJitter()
	{
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 40, 0, 0 ), Dt, frozen: true, grounded: true ) );
		Assert.IsFalse( Corrected( At( 0, 0, 0 ), At( 2, 0, 0 ), Dt, frozen: true, grounded: true ) );
	}

	[TestMethod]
	public void CorrectsNonFinitePosition() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( float.NaN, 0, 0 ), Dt, frozen: false, grounded: true ) );

	[TestMethod]
	public void AcceptsAFallWithinTheTerminalEnvelope() =>
		Assert.IsFalse( Corrected( At( 0, 0, 0 ), At( 0, 0, -40 ), Dt, frozen: false, grounded: false ) );

	[TestMethod]
	public void CorrectsImpossibleVerticalRise() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 0, 0, 200 ), Dt, frozen: false, grounded: false ) );

	[TestMethod]
	public void RejectsHorizontalSpeedTheLooseSkinWouldHaveAllowed() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 24, 0, 0 ), Dt, frozen: false, grounded: true ) );

	[TestMethod]
	public void FrozenPlayerIsCorrectedBeyondTheTightFrozenSkin() =>
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 10, 0, 0 ), Dt, frozen: true, grounded: true ) );

	[TestMethod]
	public void AirborneRiseWithoutGroundContactGetsNoStepAllowance()
	{
		// Previously named RejectsStraightUpFlightWithoutHorizontalMotion, which described a property it
		// did not test: it rejected one oversized tick while the validator happily accepted 650 u/s of
		// sustained straight-up flight. The step allowance now requires ground to step from, so a rise
		// that would have been covered by it in mid-air is refused.
		Assert.IsTrue( Corrected( At( 0, 0, 0 ), At( 0, 0, StepUpHeight ), Dt, frozen: false, grounded: false ) );
	}

	[TestMethod]
	public void AStepUpIsAffordableOnceFromGroundButIsNotARate()
	{
		// A discrete lip is legitimate and stays smooth. The important half of this test is the second
		// assertion: the same step repeated immediately is NOT affordable, because the reservoir it came
		// from refills only from rise the player did not take. The old version of this test asserted
		// only the first half, which made it a blessing of the exploit vector rather than a guard.
		var state = MovementState.PrimedAt( At( 0, 0, 0 ), Envelope );

		var (first, afterFirst) = HexMovementValidator.Evaluate(
			state, At( 6, 0, 18 ), Dt, RunSpeed, JumpSpeed, frozen: false, grounded: true, Envelope );
		Assert.IsFalse( first.Corrected, "A single step-up while walking should be affordable." );

		var (second, _) = HexMovementValidator.Evaluate(
			afterFirst, At( 12, 0, 36 ), Dt, RunSpeed, JumpSpeed, frozen: false, grounded: true, Envelope );
		Assert.IsTrue( second.Corrected,
			"Repeating the step immediately is a 900 u/s climb; the reservoir must not have refilled." );
	}

	[TestMethod]
	public void AcceptsANormalJumpRise() =>
		Assert.IsFalse( Corrected( At( 0, 0, 0 ), At( 0, 0, 10 ), Dt, frozen: false, grounded: true ) );

	[TestMethod]
	public void AnUnprimedStateAdoptsTheFirstSampleWithoutCorrecting()
	{
		var (decision, state) = HexMovementValidator.Evaluate(
			MovementState.Unprimed, At( 500, 500, 500 ), Dt, RunSpeed, JumpSpeed,
			frozen: false, grounded: true, Envelope );
		Assert.IsFalse( decision.Corrected );
		Assert.IsTrue( state.Primed );
		Assert.AreEqual( 500f, state.LastGood.X );
	}

	[TestMethod]
	public void AMalformedEnvelopeIsRejectedRatherThanValidatingNothing()
	{
		Assert.IsTrue( HexMovementEnvelope.Default.IsWellFormed( out _ ) );

		var permissive = HexMovementEnvelope.Default with { HorizontalTolerance = 50f };
		Assert.IsFalse( permissive.IsWellFormed( out var error ) );
		Assert.IsNotEmpty( error );
	}
}
