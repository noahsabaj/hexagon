using Hexagon.V2.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class AuthoritativeBodyRetirementTests
{
	[TestMethod]
	public void DestroyFailureQuiescesAndRetainsBodyForSuccessfulRetry()
	{
		var valid = true;
		var enabled = true;
		var destroyAttempts = 0;
		var retained = 0;

		AuthoritativeBodyRetirementResult Retire() => AuthoritativeBodyRetirement.Retire(
			() => valid,
			() => enabled = false,
			() =>
			{
				destroyAttempts++;
				if ( destroyAttempts == 1 ) throw new InvalidOperationException( "injected destroy failure" );
				valid = false;
			},
			() => retained++ );

		var first = Retire();
		Assert.IsFalse( first.IsClean );
		Assert.IsTrue( first.Retained );
		Assert.IsFalse( enabled );
		Assert.AreEqual( 1, retained );

		var retry = Retire();
		Assert.IsTrue( retry.IsClean );
		Assert.IsFalse( retry.Retained );
		Assert.AreEqual( 2, destroyAttempts );
		Assert.AreEqual( 1, retained );
	}

	[TestMethod]
	public void QuiesceFailureDoesNotPreventDestruction()
	{
		var valid = true;
		var result = AuthoritativeBodyRetirement.Retire(
			() => valid,
			() => throw new InvalidOperationException( "injected refresh failure" ),
			() => valid = false,
			() => Assert.Fail( "A destroyed body must not be retained." ) );

		Assert.IsTrue( result.IsClean );
		Assert.IsFalse( result.Retained );
		Assert.HasCount( 1, result.Failures );
	}
}
