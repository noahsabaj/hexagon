#nullable enable

using System;
using Hexagon.V2.Kernel;
using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class SceneHandoffPolicyTests
{
	[TestMethod]
	public void CleanPredecessorProceedsWithoutWarning()
	{
		var verdict = SceneHandoffPolicy.EvaluatePredecessor(
			OperationOutcome<OperationResult>.Success( OperationResult.Success() ) );

		Assert.AreEqual( SceneHandoffPredecessorState.Clean, verdict.State );
		Assert.IsFalse( verdict.ShouldWarn );
		Assert.IsNull( verdict.Diagnostic );
		Assert.IsTrue( verdict.ProceedsToRecovery );
	}

	[TestMethod]
	public void FailedDrainResultProceedsWithDiagnostic()
	{
		var verdict = SceneHandoffPolicy.EvaluatePredecessor(
			OperationOutcome<OperationResult>.Success(
				OperationResult.Failure( ErrorCode.InternalError, "host resources did not close cleanly" ) ) );

		Assert.AreEqual( SceneHandoffPredecessorState.DrainReportedFailure, verdict.State );
		Assert.IsTrue( verdict.ShouldWarn );
		StringAssert.Contains( verdict.Diagnostic, "did not close cleanly" );
		Assert.IsTrue( verdict.ProceedsToRecovery );
	}

	[TestMethod]
	public void FaultedDrainTaskProceedsWithDiagnostic()
	{
		var verdict = SceneHandoffPolicy.EvaluatePredecessor(
			OperationOutcome<OperationResult>.Failure( new InvalidOperationException( "teardown NRE" ) ) );

		Assert.AreEqual( SceneHandoffPredecessorState.DrainFaulted, verdict.State );
		Assert.IsTrue( verdict.ShouldWarn );
		StringAssert.Contains( verdict.Diagnostic, "teardown NRE" );
		Assert.IsTrue( verdict.ProceedsToRecovery );
	}

	[TestMethod]
	public void NoPredecessorOutcomeEverBlocksTheSuccessor()
	{
		// The load-bearing invariant of the self-heal: whatever the predecessor's drain did, the
		// successor proceeds to the exclusive lease and WAL recovery, which are the sole authorities
		// on ownership and integrity. A regression back to a fail-closed handoff must trip here.
		var outcomes = new[]
		{
			OperationOutcome<OperationResult>.Success( OperationResult.Success() ),
			OperationOutcome<OperationResult>.Success(
				OperationResult.Failure( ErrorCode.InternalError, "drain failed" ) ),
			OperationOutcome<OperationResult>.Failure( new InvalidOperationException( "faulted" ) )
		};

		foreach ( var outcome in outcomes )
			Assert.IsTrue( SceneHandoffPolicy.EvaluatePredecessor( outcome ).ProceedsToRecovery );
	}
}
