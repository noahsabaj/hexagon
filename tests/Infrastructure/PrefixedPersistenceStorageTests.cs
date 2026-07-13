#nullable enable

using System;
using System.Linq;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Infrastructure;

[TestClass]
public sealed class PrefixedPersistenceStorageTests
{
	[TestMethod]
	public async Task EveryOperationIsMappedBelowTheIsolatedPrefix()
	{
		var physical = new InMemoryPersistenceStorage();
		var isolated = new PrefixedPersistenceStorage( physical, "verification/run-42" );

		Assert.IsTrue( await isolated.TryWriteImmutableAsync( "hexagon/v2/schema/format.json", new byte[] { 1, 2 } ) );
		Assert.IsTrue( await physical.ExistsAsync( "verification/run-42/hexagon/v2/schema/format.json" ) );
		Assert.IsFalse( await physical.ExistsAsync( "hexagon/v2/schema/format.json" ) );

		var listed = await isolated.ListAsync( "hexagon/v2/schema" );
		Assert.HasCount( 1, listed );
		Assert.AreEqual( "hexagon/v2/schema/format.json", listed.Single() );
	}

	[TestMethod]
	public void PrefixRejectsParentAndCurrentDirectorySegments()
	{
		var storage = new InMemoryPersistenceStorage();
		Assert.ThrowsExactly<ArgumentException>( () => new PrefixedPersistenceStorage( storage, "../escape" ) );
		Assert.ThrowsExactly<ArgumentException>( () => new PrefixedPersistenceStorage( storage, "safe/./escape" ) );
	}
}
