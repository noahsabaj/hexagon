using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class InventoryAccessServiceTests
{
	[TestMethod]
	public void GrantIsBoundToConnectionCharacterAndCapabilities()
	{
		var service = new InventoryAccessService();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var inventory = InventoryId.New();
		service.Grant( new InventoryGrant
		{
			ConnectionId = connection,
			CharacterId = character,
			InventoryId = inventory,
			Capabilities = InventoryCapability.View | InventoryCapability.Move,
			Kind = InventoryGrantKind.Character
		} );

		Assert.IsTrue( service.Has( connection, character, inventory, InventoryCapability.View ) );
		Assert.IsFalse( service.Has( connection, character, inventory, InventoryCapability.Drop ) );
		Assert.IsFalse( service.Has( ConnectionId.New(), character, inventory, InventoryCapability.View ) );
		Assert.IsFalse( service.Has( connection, CharacterId.New(), inventory, InventoryCapability.View ) );
	}

	[TestMethod]
	public void CharacterUnloadRevokesAllCharacterGrants()
	{
		var service = new InventoryAccessService();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		for ( var i = 0; i < 2; i++ )
		{
			service.Grant( new InventoryGrant
			{
				ConnectionId = connection,
				CharacterId = character,
				InventoryId = InventoryId.New(),
				Capabilities = InventoryCapability.View,
				Kind = InventoryGrantKind.Character
			} );
		}

		Assert.HasCount( 2, service.RevokeCharacter( connection, character ) );
	}
}
