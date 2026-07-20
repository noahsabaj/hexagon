#nullable enable

using Hexagon.V2.Kernel;

namespace Hexagon.V2.Runtime;

/// <summary>
/// How the previous host's settled drain outcome is classified when a new runtime takes over the
/// same persistence root.
/// </summary>
internal enum SceneHandoffPredecessorState
{
	/// <summary>The predecessor drained and reported success.</summary>
	Clean,

	/// <summary>The predecessor drain completed but reported a failure result.</summary>
	DrainReportedFailure,

	/// <summary>The predecessor drain task faulted or was cancelled.</summary>
	DrainFaulted
}

/// <summary>
/// The successor's decision for a settled predecessor. It always proceeds — see
/// <see cref="SceneHandoffPolicy"/> — carrying only a diagnostic for a non-clean handoff.
/// </summary>
internal readonly record struct SceneHandoffVerdict(
	SceneHandoffPredecessorState State,
	string? Diagnostic )
{
	/// <summary>
	/// The successor always continues to lease acquisition and WAL recovery; the predecessor's
	/// drain outcome never blocks it.
	/// </summary>
	public bool ProceedsToRecovery => true;

	/// <summary>A non-clean predecessor is worth a breadcrumb, never a refusal.</summary>
	public bool ShouldWarn => State != SceneHandoffPredecessorState.Clean;
}

/// <summary>
/// Classifies the previous host's settled drain when a new runtime takes over the same persistence
/// root. The predecessor's in-process drain conflates benign engine-teardown faults (for example a
/// destroyed <c>GameObject</c> during disembody) with real durability faults, so it is NEVER an
/// authority on whether the store may be opened: ownership is decided immediately afterwards by the
/// exclusive lease (a live owner fails with <c>LeaseUnavailable</c>) and on-disk integrity by WAL
/// recovery (genuine corruption fails closed there). This seam therefore always proceeds, surfacing
/// a diagnostic when the predecessor did not report a clean drain. Using the drain outcome as a gate
/// is what produced the false-positive wedge that only an editor restart could clear.
/// </summary>
internal static class SceneHandoffPolicy
{
	public static SceneHandoffVerdict EvaluatePredecessor( OperationOutcome<OperationResult> predecessor )
	{
		if ( !predecessor.Succeeded )
			return new SceneHandoffVerdict(
				SceneHandoffPredecessorState.DrainFaulted,
				predecessor.Exception?.Message );

		if ( predecessor.Value.Failed )
			return new SceneHandoffVerdict(
				SceneHandoffPredecessorState.DrainReportedFailure,
				predecessor.Value.Error?.Message );

		return new SceneHandoffVerdict( SceneHandoffPredecessorState.Clean, null );
	}
}
