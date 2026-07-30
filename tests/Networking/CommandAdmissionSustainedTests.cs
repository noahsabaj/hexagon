#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Hexagon.V2.Networking;
using Hexagon.V2.Tests.Foundation;

namespace Hexagon.V2.Tests.Networking;

/// <summary>
/// Sustained-run guards for the per-connection command budget, and a pin tying the numbers
/// <c>docs/security.md</c> publishes to the constants that enforce them.
/// <para>
/// The movement validator's failure was that a per-tick allowance was never checked as a rate, so prose
/// describing a bound and code granting one every tick could both read as true while disagreeing by
/// fifty times. Every other rate-limited surface therefore gets the same treatment: the published number
/// is read out of the document and asserted against the constant, and the constant is asserted against
/// what a client can actually extract over a run.
/// </para>
/// </summary>
[TestClass]
public sealed class CommandAdmissionSustainedTests
{
	private const long Frequency = 1000;          // timestamps in milliseconds
	private const int Hz = 50;
	private const int TickMilliseconds = 1000 / Hz;
	private const int Ticks = 10 * Hz;            // ten seconds
	private const double ElapsedSeconds = 10.0;

	[TestMethod]
	public void TheSecurityDocumentPublishesTheBudgetThatIsActuallyEnforced()
	{
		// docs/security.md: "a weighted per-connection token bucket before payload construction:
		// 16-unit burst, 8 units/second refill, and at most 16 active requests."
		// Reading the numbers out of the document makes the prose a claim this suite owns: editing
		// either side without the other fails here rather than shipping a document that describes a
		// system nobody built.
		var document = File.ReadAllText( Path.Combine( RepositoryRoots.FindHexagon(), "docs", "security.md" ) );

		var match = Regex.Match( document,
			@"(?<burst>\d+)-unit burst,\s*(?<refill>\d+)\s*units?/second refill,\s*and at most\s*(?<active>\d+)\s*active requests" );
		Assert.IsTrue( match.Success,
			"docs/security.md no longer states the command budget in the form this guard reads. " +
			"Update the guard deliberately rather than letting the published numbers go unchecked." );

		Assert.AreEqual( CommandAdmissionController.BurstUnits, int.Parse( match.Groups["burst"].Value ),
			"Documented burst does not match the enforced constant." );
		Assert.AreEqual( CommandAdmissionController.RefillUnitsPerSecond, int.Parse( match.Groups["refill"].Value ),
			"Documented refill rate does not match the enforced constant." );
		Assert.AreEqual( CommandAdmissionController.MaximumActiveRequests, int.Parse( match.Groups["active"].Value ),
			"Documented active-request cap does not match the enforced constant." );
	}

	[TestMethod]
	public void SustainedAdmissionConvergesOnTheRefillRateNotTheBurst()
	{
		// The burst is a one-off; the refill rate is the ceiling. A bucket that re-granted its burst
		// the way the movement validator re-granted its skin would admit 800 units here, not 96.
		var controller = new CommandAdmissionController( Frequency );
		var admittedCount = 0;

		var run = SustainedEnvelope.Measure( Ticks, 1.0 / Hz, tick =>
		{
			var timestamp = (long)tick * TickMilliseconds;
			var requestId = new CommandRequestId( Guid.NewGuid() );
			var result = controller.TryBegin( requestId, cost: 1, timestamp );
			if ( !result.Accepted ) return SustainedEnvelope.StepOutcome.Rejected;
			controller.Finish( requestId );   // keep the active-request bound out of the way
			admittedCount++;
			return SustainedEnvelope.StepOutcome.Allowed( 1 );
		} );

		var bound = (CommandAdmissionController.RefillUnitsPerSecond * ElapsedSeconds)
			+ CommandAdmissionController.BurstUnits;

		Assert.IsLessThanOrEqualTo( bound, run.TotalWork, SustainedEnvelope.Describe(
			$"Sustained command admission over 10s ({run.WorkPerSecond:F1}/s)",
			run.TotalWork, bound, " units" ) );
		Assert.IsGreaterThan( 0, admittedCount,
			"The run admitted nothing, so the bound above proves nothing about the bucket." );
	}

	[TestMethod]
	public void RejectedAttemptsAreNotRefundedAcrossASustainedRun()
	{
		// docs/security.md: "Rejected, duplicate, and malformed attempts are not refunded." A refund on
		// rejection would make the bucket a no-op under exactly the load it exists to bound.
		var controller = new CommandAdmissionController( Frequency );
		var duplicate = new CommandRequestId( Guid.NewGuid() );

		// Drain the burst with duplicates, which are charged and then refused.
		for ( var i = 0; i < CommandAdmissionController.BurstUnits; i++ )
			controller.TryBegin( duplicate, cost: 1, 0 );

		Assert.IsLessThanOrEqualTo( 0.001, controller.AvailableUnits,
			$"Duplicates were refunded: {controller.AvailableUnits:F2} units remain after draining the burst." );
	}

	[TestMethod]
	public void ACostlierCommandDrainsTheBudgetProportionally()
	{
		// The bucket is weighted, so an expensive command must consume its weight rather than one slot.
		var controller = new CommandAdmissionController( Frequency );
		var admitted = 0;
		for ( var i = 0; i < 100; i++ )
		{
			var requestId = new CommandRequestId( Guid.NewGuid() );
			if ( !controller.TryBegin( requestId, CommandAdmissionController.BurstUnits, 0 ).Accepted ) break;
			controller.Finish( requestId );
			admitted++;
		}

		Assert.AreEqual( 1, admitted,
			"A full-burst-cost command should be admissible exactly once from a full bucket." );
	}

}
