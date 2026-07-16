#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class AuthoritativeBodyReplacementTests
{
	[TestMethod]
	public void PublicationFailureRestoresPreviousAndDiscardsCandidate()
	{
		var canonical = "previous";
		var previousEnabled = true;
		var candidateEnabled = false;
		var candidateDiscarded = false;
		var result = AuthoritativeBodyReplacement.RequireActivated(
			"candidate",
			() => candidateEnabled = true,
			() => { previousEnabled = false; return true; },
			() => { canonical = "candidate"; throw new InvalidOperationException( "refresh failed" ); },
			() => canonical = "previous",
			() => previousEnabled = true,
			() => { candidateEnabled = false; candidateDiscarded = true; },
			() => throw new AssertFailedException( "A failed replacement must not retire the previous body." ) );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( "previous", canonical );
		Assert.IsTrue( previousEnabled );
		Assert.IsFalse( candidateEnabled );
		Assert.IsTrue( candidateDiscarded );
	}

	[TestMethod]
	public void ActivationFailureNeverQuiescesOrPublishes()
	{
		var laterStageCalls = 0;
		var candidateDiscarded = false;
		var result = AuthoritativeBodyReplacement.RequireActivated(
			new object(),
			() => false,
			() => { laterStageCalls++; return true; },
			() => { laterStageCalls++; return true; },
			() => laterStageCalls++,
			() => laterStageCalls++,
			() => candidateDiscarded = true,
			() => laterStageCalls++ );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( 0, laterStageCalls );
		Assert.IsTrue( candidateDiscarded );
	}

	[TestMethod]
	public void SuccessPublishesBeforeRetiringPrevious()
	{
		var stages = new List<string>();
		var result = AuthoritativeBodyReplacement.RequireActivated(
			"candidate",
			() => { stages.Add( "activate" ); return true; },
			() => { stages.Add( "quiesce" ); return true; },
			() => { stages.Add( "publish" ); return true; },
			() => stages.Add( "rollback" ),
			() => stages.Add( "restore" ),
			() => stages.Add( "discard" ),
			() => stages.Add( "retire" ) );

		Assert.IsTrue( result.Succeeded );
		CollectionAssert.AreEqual(
			new[] { "activate", "quiesce", "publish", "retire" },
			stages );
	}
}
