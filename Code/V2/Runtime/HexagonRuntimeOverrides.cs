#nullable enable

using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Host-only launch overrides. s&amp;box initializes ConVars from matching +switches,
/// allowing verification to isolate data without rewriting the authored scene.
/// </summary>
internal static class HexagonRuntimeOverrides
{
	[ConVar( "hexagon-data-root", ConVarFlags.Server | ConVarFlags.Hidden,
		Help = "Relative FileSystem.Data prefix for an isolated Hexagon host run." )]
	public static string PersistenceRoot { get; set; } = string.Empty;

	[ConVar( "hexagon-verification-probe", ConVarFlags.Server | ConVarFlags.Hidden,
		Help = "Opaque verification scenario exposed to the schema host application." )]
	public static string VerificationProbe { get; set; } = string.Empty;

	[ConVar( "hexagon-persistence-quarantine", ConVarFlags.Server,
		Help = "Operator-armed one-shot recovery: on the next host start, if this store is genuinely " +
			"corrupt, archive it aside and rebuild an empty store instead of failing closed. Consumed " +
			"and reset once it takes effect." )]
	public static bool QuarantineCorruptStore { get; set; }

	// --- Movement envelope -------------------------------------------------------------------------
	// The host validates owner-reported movement against these and publishes the geometric ones to the
	// owning client's HexMoveModeWalk, so both ends stay on one envelope. Tunable because the values
	// that survive real remote proxies (jitter, slopes, impulses) cannot be known before the two-client
	// acceptance run; every value is bounds-checked before use, since a tunable envelope means a
	// mis-set value would otherwise be a hole rather than a rejected configuration.

	[ConVar( "hexagon-movement-horizontal-tolerance", ConVarFlags.Server,
		Help = "Slack multiplier on run speed for slopes, strafe, and timing jitter. 1 to 2." )]
	public static float MovementHorizontalTolerance { get; set; } =
		HexMovementEnvelope.DefaultHorizontalTolerance;

	[ConVar( "hexagon-movement-audit-window", ConVarFlags.Server,
		Help = "Seconds of travel each audit covers. Long enough that network interpolation timing is " +
			"noise; also the detection latency for speed and climb abuse." )]
	public static float MovementAuditWindow { get; set; } =
		HexMovementEnvelope.DefaultAuditWindowSeconds;

	[ConVar( "hexagon-movement-interpolation-slack", ConVarFlags.Server,
		Help = "Endpoint slack in units for the interpolation buffer's delay. Costs detection " +
			"sensitivity, never a false positive. Raise it if honest play is flagged." )]
	public static float MovementInterpolationSlack { get; set; } =
		HexMovementEnvelope.DefaultInterpolationSlack;

	[ConVar( "hexagon-movement-step-height", ConVarFlags.Server,
		Help = "Discrete step-up height in units. Published to clients as MoveModeWalk.StepUpHeight so " +
			"the height clients actually step is the height the host validates." )]
	public static float MovementStepHeight { get; set; } =
		HexMovementEnvelope.DefaultStepHeight;

	[ConVar( "hexagon-movement-ground-angle", ConVarFlags.Server,
		Help = "Steepest standable surface in degrees. Bounds a grounded player's climb against their " +
			"horizontal travel; published to clients as MoveModeWalk.GroundAngle." )]
	public static float MovementGroundAngle { get; set; } =
		HexMovementEnvelope.DefaultGroundAngleDegrees;

	[ConVar( "hexagon-movement-kick-seconds", ConVarFlags.Server,
		Help = "Seconds of sustained out-of-envelope reporting before a connection is dropped. Raise it " +
			"to make kicks effectively never fire; corrections still apply." )]
	public static float MovementKickSeconds { get; set; } =
		HexMovementEnvelope.DefaultViolationKickSeconds;

	/// <summary>
	/// Builds the configured envelope, falling back to the framework default if an operator set a value
	/// outside its permitted range. Fails loud and safe rather than validating against nothing.
	/// </summary>
	public static HexMovementEnvelope ResolveMovementEnvelope()
	{
		var configured = HexMovementEnvelope.Default with
		{
			HorizontalTolerance = MovementHorizontalTolerance,
			AuditWindowSeconds = MovementAuditWindow,
			InterpolationSlack = MovementInterpolationSlack,
			StepHeight = MovementStepHeight,
			GroundAngleDegrees = MovementGroundAngle,
			ViolationKickSeconds = MovementKickSeconds
		};

		if ( configured.IsWellFormed( out var error ) ) return configured;

		Log.Warning(
			$"HEXAGON_MOVEMENT_ENVELOPE_REJECTED {error} Falling back to the framework default envelope." );
		return HexMovementEnvelope.Default;
	}
}
