#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class ClientBootstrapDiagnosticTests
{
	[TestMethod]
	public void ContractAcceptsOnlyTheThreeExactPhaseCodePairs()
	{
		var allowed = new[]
		{
			(ClientBootstrapDiagnosticPhase.Bootstrap,
				ClientBootstrapDiagnosticCode.BootstrapUnavailable),
			(ClientBootstrapDiagnosticPhase.SchemaDiscovery,
				ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable),
			(ClientBootstrapDiagnosticPhase.ClientConfiguration,
				ClientBootstrapDiagnosticCode.ClientConfigurationFailed)
		};
		foreach ( var pair in allowed )
			Assert.IsTrue( ClientBootstrapDiagnosticContract.Validate(
				pair.Item1, pair.Item2, "bounded detail" ).Succeeded );

		var crossed = ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			"forged pair" );
		var undefined = ClientBootstrapDiagnosticContract.Validate(
			(ClientBootstrapDiagnosticPhase)999,
			(ClientBootstrapDiagnosticCode)999,
			"forged values" );

		Assert.AreEqual( ErrorCode.InvalidArgument, crossed.Error!.Code );
		Assert.AreEqual( ErrorCode.InvalidArgument, undefined.Error!.Code );
	}

	[TestMethod]
	public void DetailIsBoundedNormalizedAndEscapedAsOneLogValue()
	{
		const string raw = "  first\r\nsecond\t\0 \"quoted\" \\ path  ";
		var prepared = ClientBootstrapDiagnosticContract.PrepareDetail(
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			raw );
		var validated = ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.SchemaDiscovery,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			prepared );

		Assert.AreEqual( "first second \"quoted\" \\ path", prepared );
		Assert.IsTrue( validated.Succeeded );
		Assert.AreEqual( prepared, validated.Value.Detail );
		Assert.AreEqual(
			"first second \\\"quoted\\\" \\\\ path",
			ClientBootstrapDiagnosticContract.EscapeLogValue( validated.Value.Detail ) );
		Assert.DoesNotContain( "\r", validated.Value.Detail );
		Assert.DoesNotContain( "\n", validated.Value.Detail );
	}

	[TestMethod]
	public void HostValidationRejectsOversizeOrInvalidUnicodeWhileClientPreparationClamps()
	{
		var tooManyCharacters = new string( 'x', ClientBootstrapDiagnosticContract.MaximumDetailCharacters + 1 );
		var tooManyBytes = new string( '\u20ac', 200 );
		var invalidUnicode = "bad\ud800";

		Assert.IsTrue( ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			tooManyCharacters ).Failed );
		Assert.IsTrue( ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			tooManyBytes ).Failed );
		Assert.IsTrue( ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			invalidUnicode ).Failed );

		var prepared = ClientBootstrapDiagnosticContract.PrepareDetail(
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			new string( 'x', 4_096 ) );
		Assert.AreEqual( ClientBootstrapDiagnosticContract.MaximumDetailCharacters, prepared.Length );
		Assert.IsTrue( ClientBootstrapDiagnosticContract.Validate(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			prepared ).Succeeded );
	}

	[TestMethod]
	public void EveryConnectedAttemptIsChargedAndPublishedPairsAreDeduplicated()
	{
		var admission = new ClientBootstrapDiagnosticAdmissionController();
		var malformed = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			"crossed pair" );
		var accepted = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.SchemaDiscovery,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			"missing schema" );
		var duplicate = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.SchemaDiscovery,
			ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
			"repeat" );
		var second = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.ClientConfiguration,
			ClientBootstrapDiagnosticCode.ClientConfigurationFailed,
			"configuration failed" );
		var overLimit = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			"fifth attempt" );

		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionFailure.Malformed, malformed.Failure );
		Assert.IsTrue( accepted.Accepted );
		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionFailure.Duplicate, duplicate.Failure );
		Assert.IsTrue( second.Accepted );
		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionFailure.LimitReached, overLimit.Failure );
		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionController.MaximumAttempts, admission.AttemptCount );
		Assert.AreEqual( 2, admission.PublishedCount );
	}

	[TestMethod]
	public async Task ConcurrentAttemptsCannotExceedTheLifetimeConnectionBound()
	{
		var admission = new ClientBootstrapDiagnosticAdmissionController();
		var results = await Task.WhenAll( Enumerable.Range( 0, 64 ).Select( _ => Task.Run( () =>
			admission.TryAccept(
				ClientBootstrapDiagnosticPhase.SchemaDiscovery,
				ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
				"missing schema" ) ) ) );

		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionController.MaximumAttempts, admission.AttemptCount );
		Assert.AreEqual( 1, results.Count( result => result.Accepted ) );
		Assert.AreEqual( 1, admission.PublishedCount );
		Assert.AreEqual(
			64 - ClientBootstrapDiagnosticAdmissionController.MaximumAttempts,
			results.Count( result => result.Failure == ClientBootstrapDiagnosticAdmissionFailure.LimitReached ) );

		admission.Disconnect();
		var disconnected = admission.TryAccept(
			ClientBootstrapDiagnosticPhase.Bootstrap,
			ClientBootstrapDiagnosticCode.BootstrapUnavailable,
			"after disconnect" );
		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionFailure.Disconnected, disconnected.Failure );
		Assert.AreEqual( ClientBootstrapDiagnosticAdmissionController.MaximumAttempts, admission.AttemptCount );
	}
}
