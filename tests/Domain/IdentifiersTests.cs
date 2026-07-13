using Hexagon.V2.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Domain;

[TestClass]
public sealed class IdentifiersTests
{
	[TestMethod]
	public void StableIdentifiersRejectUnsafeCharacters()
	{
		Assert.ThrowsExactly<ArgumentException>( () => new DefinitionId( "Unsafe Value" ) );
		Assert.ThrowsExactly<ArgumentException>( () => new DefinitionId( "../escape" ) );
	}

	[TestMethod]
	public void StableIdentifiersPreserveCanonicalValue()
	{
		var id = new DefinitionId( "hl2rp.pistol-ammo" );
		Assert.AreEqual( "hl2rp.pistol-ammo", id.Value );
	}

	[TestMethod]
	public void EntityIdentifiersRejectEmptyGuid()
	{
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => new CharacterId( Guid.Empty ) );
	}
}
