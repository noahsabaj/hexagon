#nullable enable

using System;
using Hexagon.V2.Composition;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Composition;

[TestClass]
public sealed class HostBootstrapResolutionTests
{
	[TestMethod]
	public void ProbeWithoutAnIsolatedPersistenceRootFailsTheBootstrapClosed()
	{
		var fromOverride = HostBootstrapResolution.Resolve(
			overrideRoot: "", sceneRoot: "", overrideProbe: "commerce", sceneProbe: "", Normalize );
		var fromScene = HostBootstrapResolution.Resolve(
			overrideRoot: "", sceneRoot: "", overrideProbe: "", sceneProbe: "commerce", Normalize );

		Assert.AreEqual( ErrorCode.ConfigurationInvalid, fromOverride.Error!.Code );
		Assert.AreEqual( ErrorCode.ConfigurationInvalid, fromScene.Error!.Code );
	}

	[TestMethod]
	public void ProbePairedWithAnIsolatedRootResolves()
	{
		var resolved = HostBootstrapResolution.Resolve(
			overrideRoot: "verify/run-1", sceneRoot: "", overrideProbe: " commerce ", sceneProbe: "", Normalize );

		Assert.IsTrue( resolved.Succeeded, resolved.Error?.Message );
		Assert.AreEqual( "normalized:verify/run-1", resolved.Value.PersistenceRootOverride );
		Assert.AreEqual( "commerce", resolved.Value.VerificationProbe, "The probe value is trimmed." );
	}

	[TestMethod]
	public void BlankProbeAndBlankRootResolveToProductionDefaults()
	{
		var resolved = HostBootstrapResolution.Resolve( "", "", "", "", Normalize );

		Assert.IsTrue( resolved.Succeeded );
		Assert.AreEqual( string.Empty, resolved.Value.PersistenceRootOverride );
		Assert.AreEqual( string.Empty, resolved.Value.VerificationProbe );
	}

	[TestMethod]
	public void LaunchOverridesTakePrecedenceOverSceneAuthoredValues()
	{
		var resolved = HostBootstrapResolution.Resolve(
			overrideRoot: "override-root",
			sceneRoot: "scene-root",
			overrideProbe: "override-probe",
			sceneProbe: "scene-probe",
			Normalize );

		Assert.IsTrue( resolved.Succeeded );
		Assert.AreEqual( "normalized:override-root", resolved.Value.PersistenceRootOverride );
		Assert.AreEqual( "override-probe", resolved.Value.VerificationProbe );
	}

	[TestMethod]
	public void OverlongProbeIsTruncatedToTheContractLength()
	{
		var resolved = HostBootstrapResolution.Resolve(
			"root", "", new string( 'p', HostBootstrapResolution.MaximumProbeLength + 40 ), "", Normalize );

		Assert.IsTrue( resolved.Succeeded );
		Assert.AreEqual( HostBootstrapResolution.MaximumProbeLength, resolved.Value.VerificationProbe.Length );
	}

	[TestMethod]
	public void ThrowingRootNormalizationFailsClosedAsConfigurationInvalid()
	{
		var resolved = HostBootstrapResolution.Resolve(
			"..\\escape", "", "", "",
			static _ => throw new ArgumentException( "Persistence root escapes the data directory." ) );

		Assert.AreEqual( ErrorCode.ConfigurationInvalid, resolved.Error!.Code );
		StringAssert.Contains( resolved.Error.Message, "escapes" );
	}

	private static string Normalize( string root ) => $"normalized:{root}";
}
