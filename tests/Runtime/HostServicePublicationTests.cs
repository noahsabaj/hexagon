#nullable enable

using Hexagon.V2.Kernel;
using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class HostServicePublicationTests
{
	[TestMethod]
	public void FalsePublicationDestroysThePartialServiceExactlyOnce()
	{
		var cleanupCalls = 0;
		var result = HostServicePublication.RequirePublished(
			new object(),
			() => false,
			() => cleanupCalls++ );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( ErrorCode.InternalError, result.Error!.Code );
		Assert.AreEqual( 1, cleanupCalls );
	}

	[TestMethod]
	public void PublicationExceptionStillDestroysThePartialServiceExactlyOnce()
	{
		var cleanupCalls = 0;
		var result = HostServicePublication.RequirePublished(
			new object(),
			() => throw new InvalidOperationException( "network unavailable" ),
			() => cleanupCalls++ );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( 1, cleanupCalls );
		StringAssert.Contains( result.Error!.Message, "network unavailable" );
	}

	[TestMethod]
	public void SuccessfulPublicationRetainsTheServiceWithoutCleanup()
	{
		var service = new object();
		var cleanupCalls = 0;
		var result = HostServicePublication.RequirePublished(
			service,
			() => true,
			() => cleanupCalls++ );

		Assert.IsTrue( result.Succeeded );
		Assert.AreSame( service, result.Value );
		Assert.AreEqual( 0, cleanupCalls );
	}
}
