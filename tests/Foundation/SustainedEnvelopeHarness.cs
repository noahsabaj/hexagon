#nullable enable

using System;
using System.Collections.Generic;

namespace Hexagon.V2.Tests.Foundation;

/// <summary>
/// Drives a rate-limited or envelope-bounded rule over many ticks and reports the TOTAL work it
/// admitted, rather than whether one step was allowed.
/// <para>
/// This exists because a per-tick check cannot be judged one tick at a time. A rule that clamps each
/// step but carries no state re-grants its full allowance every tick, so the per-tick maximum silently
/// becomes a sustainable rate — and a single-step test not only misses that, it blesses it: the step
/// it declares legitimate is exactly the step the exploit repeats. Every guard here is therefore
/// stated as a bound on accumulated work over a run, never on one step.
/// </para>
/// </summary>
public static class SustainedEnvelope
{
	/// <summary>One tick's outcome: whether the rule admitted it, and how much work it let through.</summary>
	public readonly record struct StepOutcome( bool Admitted, double Work )
	{
		public static StepOutcome Rejected => new( false, 0 );
		public static StepOutcome Allowed( double work ) => new( true, work );
	}

	/// <summary>Accumulated result of a sustained run.</summary>
	public readonly record struct SustainedResult(
		int Ticks,
		int Admitted,
		int Rejected,
		double TotalWork,
		double ElapsedSeconds )
	{
		/// <summary>Admitted work per second across the whole run — the rate the rule actually permits.</summary>
		public double WorkPerSecond => ElapsedSeconds > 0 ? TotalWork / ElapsedSeconds : 0;

		public override string ToString() =>
			$"{Admitted}/{Ticks} ticks admitted, {TotalWork:F1} total work over {ElapsedSeconds:F2}s " +
			$"= {WorkPerSecond:F1}/s";
	}

	/// <summary>
	/// Runs <paramref name="step"/> for <paramref name="ticks"/> iterations at a fixed timestep and
	/// accumulates what it admitted. The caller owns whatever state the rule needs, so the same harness
	/// drives a movement validator, a token bucket, or an expiry sweep.
	/// </summary>
	public static SustainedResult Measure( int ticks, double deltaSeconds, Func<int, StepOutcome> step )
	{
		ArgumentNullException.ThrowIfNull( step );
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero( ticks );

		var admitted = 0;
		var rejected = 0;
		var total = 0.0;
		for ( var tick = 0; tick < ticks; tick++ )
		{
			var outcome = step( tick );
			if ( outcome.Admitted ) { admitted++; total += outcome.Work; }
			else rejected++;
		}

		return new SustainedResult( ticks, admitted, rejected, total, ticks * deltaSeconds );
	}

	/// <summary>
	/// Convenience for burst-then-refill rules: how much work a rule admits over a window, given a
	/// constant demand every tick.
	/// </summary>
	public static SustainedResult MeasureConstantDemand(
		int ticks, double deltaSeconds, double demandPerTick, Func<double, bool> admit )
	{
		ArgumentNullException.ThrowIfNull( admit );
		return Measure( ticks, deltaSeconds, _ =>
			admit( demandPerTick ) ? StepOutcome.Allowed( demandPerTick ) : StepOutcome.Rejected );
	}

	/// <summary>
	/// Engine ground truth used by the movement guards, read from the s&amp;box source at
	/// <c>D:\Code\Games\sbox-public</c> rather than assumed. Kept here so every sustained movement
	/// assertion is stated against the same numbers the shipped controller uses.
	/// </summary>
	public static class EngineDefaults
	{
		/// <summary>PhysicsSettings.FixedUpdateFrequency (PhysicsSettings.cs).</summary>
		public const float FixedUpdateHz = 50f;
		public const float FixedDelta = 1f / FixedUpdateHz;

		/// <summary>PlayerController.Input.cs.</summary>
		public const float RunSpeed = 320f;
		public const float JumpSpeed = 300f;
		public const float WalkSpeed = 110f;

		/// <summary>MoveModeWalk.GroundAngle — the steepest surface a player may stand on.</summary>
		public const float GroundAngleDegrees = 45f;

		/// <summary>MoveModeWalk.StepUpHeight.</summary>
		public const float StepUpHeight = 18f;

		/// <summary>
		/// The steepest sustained rise a legitimate player can achieve: on a 45-degree surface, rise
		/// equals horizontal travel. This is the invariant the sustained climb guards are stated against
		/// — an absolute altitude cap would be arbitrary, but "you cannot climb faster than you walk"
		/// falls straight out of the engine's own ground angle.
		/// </summary>
		public static double MaximumRisePerHorizontalUnit =>
			Math.Tan( GroundAngleDegrees * Math.PI / 180.0 );

		/// <summary>
		/// Apex of a single unassisted jump (v^2 / 2g) with the engine's default gravity, plus the one
		/// discrete step-up a player may gain on landing. The slack a climb bound must tolerate.
		/// </summary>
		public const double JumpApexSlack = (JumpSpeed * JumpSpeed / (2 * 800.0)) + StepUpHeight;
	}

	/// <summary>Formats a failure so the number that matters is the first thing read.</summary>
	public static string Describe( string what, double actual, double bound, string unit ) =>
		$"{what}: {actual:F1}{unit} admitted, bound is {bound:F1}{unit} ({actual / Math.Max( bound, 0.0001 ):F1}x).";

	/// <summary>Records a per-tick position walk so a movement run can be replayed in a failure message.</summary>
	public sealed class PositionTrack
	{
		private readonly List<(double X, double Y, double Z)> _accepted = new();

		public double X { get; private set; }
		public double Y { get; private set; }
		public double Z { get; private set; }

		public void Accept( double x, double y, double z )
		{
			X = x; Y = y; Z = z;
			_accepted.Add( (x, y, z) );
		}

		public double HorizontalDistance => Math.Sqrt( (X * X) + (Y * Y) );
		public double Rise => Z;
		public int AcceptedSteps => _accepted.Count;
	}
}
