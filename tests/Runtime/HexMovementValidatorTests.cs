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
		// Horizontal envelope ≈ 320 * 1.25 * (1/30) + 16 ≈ 29 units per tick.
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
		// Falling envelope ≈ 1800 * (1/30) + 16 ≈ 76 units per tick.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, -40 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsFalse( decision.Corrected );
	}

	[TestMethod]
	public void CorrectsImpossibleVerticalRise()
	{
		// Rise envelope ≈ 300 * 1.5 * (1/30) + 16 ≈ 31 units per tick.
		var decision = HexMovementValidator.Evaluate(
			At( 0, 0, 0 ), At( 0, 0, 200 ), Dt, RunSpeed, JumpSpeed, frozen: false );
		Assert.IsTrue( decision.Corrected );
	}
}
