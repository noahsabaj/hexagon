#nullable enable

using Sandbox;
using Sandbox.Movement;

namespace Hexagon.V2.Runtime;

/// <summary>
/// The owning client's walk mode, configured from the same <see cref="HexMovementEnvelope"/> the host
/// validates against.
/// <para>
/// This exists to remove a source-of-truth split, not to add security. The host used to validate step-ups
/// against a hand-picked constant (16 units) while clients actually stepped by
/// <c>MoveModeWalk.StepUpHeight</c> (18), and it modelled no ground-angle limit at all — two numbers that
/// must agree, declared independently, disagreeing. Binding both ends to one configured envelope is what
/// stops them drifting again when an operator tunes either.
/// </para>
/// <para>
/// It binds honest clients only: a cheat client does not run this component. The enforcement lives in
/// <see cref="HexMovementValidator"/> on the host. What this buys is that an honest client cannot be
/// corrected for performing movement the framework told it was legal.
/// </para>
/// <para>
/// Extension is by composition because <c>PlayerController</c> is <c>sealed</c>; <c>MoveModeWalk</c> is
/// not, and <c>PlayerController</c> selects its mode by <see cref="MoveModeWalk.Priority"/> through
/// <c>ChooseBestMoveMode</c>. The priority below is above the default so selection is deterministic even
/// if the engine's own <c>GetOrAddComponent&lt;MoveModeWalk&gt;()</c> also creates a plain one.
/// </para>
/// </summary>
[Icon( "hexagon" ), Group( "Hexagon" ), Title( "Hexagon - Walk" )]
public sealed class HexMoveModeWalk : MoveModeWalk
{
	/// <summary>Above the stock <c>MoveModeWalk</c> default of 0 so this mode always wins selection.</summary>
	public const int HexagonWalkPriority = 10;

	private HexPlayerBody? _player;

	public HexMoveModeWalk() => Priority = HexagonWalkPriority;

	protected override void OnAwake()
	{
		base.OnAwake();
		Priority = HexagonWalkPriority;
		ApplyEnvelope();
	}

	protected override void OnUpdate()
	{
		// The envelope arrives from the host over [Sync(FromHost)], so it may land after this component
		// awakes. Reapplying is a few float compares and keeps a mid-session config change honoured.
		ApplyEnvelope();
	}

	private void ApplyEnvelope()
	{
		if ( !GameObject.IsValid() ) return;
		if ( !_player.IsValid() ) _player = GameObject.Components.Get<HexPlayerBody>();
		if ( _player is not { } player ) return;

		var stepHeight = player.MovementStepHeight;
		var groundAngle = player.MovementGroundAngle;
		if ( stepHeight <= 0f || groundAngle <= 0f ) return;   // envelope not published yet

		if ( !StepUpHeight.AlmostEqual( stepHeight ) ) StepUpHeight = stepHeight;
		if ( !StepDownHeight.AlmostEqual( stepHeight ) ) StepDownHeight = stepHeight;
		if ( !GroundAngle.AlmostEqual( groundAngle ) ) GroundAngle = groundAngle;
	}
}
