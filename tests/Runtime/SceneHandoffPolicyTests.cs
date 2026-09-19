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
	}

	[TestMethod]
	public void FaultedDrainTaskProceedsWithDiagnostic()
	{
		var verdict = SceneHandoffPolicy.EvaluatePredecessor(
			OperationOutcome<OperationResult>.Failure( new InvalidOperationException( "teardown NRE" ) ) );

		Assert.AreEqual( SceneHandoffPredecessorState.DrainFaulted, verdict.State );
		Assert.IsTrue( verdict.ShouldWarn );
		StringAssert.Contains( verdict.Diagnostic, "teardown NRE" );
	}
}
