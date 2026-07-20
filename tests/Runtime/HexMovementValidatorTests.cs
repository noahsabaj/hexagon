#nullable enable

using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class HexMovementValidatorTests
{
	private const float RunSpeed = 320f;
	private const float JumpSpeed = 300f;
	private const float Dt = 1f / 30f;

	private static MovementSample At( float x, float y, float z ) => new( x, y, z );

	[TestMethod]
	public void AcceptsMovementWithinTheRunSpeedEnvelope()
	{
		// Horizontal envelope ≈ 320 * 1.25 * (1/30) + 4 ≈ 17 units per tick.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 9, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsFalse( decision.Corrected );
	}

	[TestMethod]
	public void CorrectsHorizontalMovementBeyondTheEnvelope()
	{
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 200, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void CorrectsHardTeleportEvenAcrossALargeStep()
	{
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 1000, 0, 0 ), 1f, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void FreezeCorrectsMovementButToleratesSkinJitter()
	{
		var moved = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 40, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: true );
		Assert.IsTrue( moved.Corrected );

		var jitter = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 2, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: true );
		Assert.IsFalse( jitter.Corrected );
	}

	[TestMethod]
	public void CorrectsNonFinitePosition()
	{
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( float.NaN, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void AcceptsAFallWithinTheTerminalEnvelope()
	{
		// Falling envelope ≈ 1800 * (1/30) + 4 ≈ 64 units per tick.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, -40 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsFalse( decision.Corrected );
	}

	[TestMethod]
	public void CorrectsImpossibleVerticalRise()
	{
		// Rise envelope ≈ 300 * 1.5 * (1/30) + 4 ≈ 19 units per tick (no step-rise without motion).
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, 200 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void RejectsHorizontalSpeedTheLooseSkinWouldHaveAllowed()
	{
		// 24 units/tick: within the old +16 skin (≈29), beyond the tightened +4 skin (≈17).
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 24, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void FrozenPlayerIsCorrectedBeyondTheTightFrozenSkin()
	{
		// 10 units/tick while frozen: the loose skin tolerated it; the frozen skin (2) does not.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 10, 0, 0 ), Dt, RunSpeed, JumpSpeed, frozen: true );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void RejectsStraightUpFlightWithoutHorizontalMotion()
	{
		// 30 units of pure vertical rise: with no horizontal motion there is no step allowance, so
		// only jump physics apply (≈19/tick) and this is corrected — a client cannot fly straight up.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, 30 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}

	[TestMethod]
	public void AllowsAStepUpWhileMovingHorizontally()
	{
		// An 18-unit rise is legitimate when paired with horizontal movement (a stair/slope), so the
		// discrete step allowance keeps it smooth.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 6, 0, 18 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsFalse( decision.Corrected );
	}

	[TestMethod]
	public void AcceptsANormalJumpRise()
	{
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, 10 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsFalse( decision.Corrected );
	}
}
