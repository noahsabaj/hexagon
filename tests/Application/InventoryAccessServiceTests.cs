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
		service.OpenConnection( connection );
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
		service.OpenConnection( connection );
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

	[TestMethod]
	public void ConnectionReuseChangesEpochAndRejectsPreDisconnectProof()
	{
		var service = new InventoryAccessService();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var inventory = InventoryId.New();
		service.OpenConnection( connection );
		service.Grant( CharacterGrant( connection, character, inventory ) );
		var stale = service.Prove( connection, character, inventory, InventoryCapability.View );
		Assert.IsNotNull( stale );

		Assert.HasCount( 1, service.RevokeConnection( connection ) );
		Assert.AreEqual( 0, service.RevisionEntryCount );
		Assert.AreEqual( 0, service.ConnectionEpochCount );

		Assert.ThrowsExactly<InvalidOperationException>( () =>
			service.Grant( CharacterGrant( connection, character, inventory ) ) );
		service.OpenConnection( connection );
		service.Grant( CharacterGrant( connection, character, inventory ) );
		var current = service.Prove( connection, character, inventory, InventoryCapability.View );
		Assert.IsNotNull( current );
		Assert.AreNotEqual( stale.ConnectionEpoch, current.ConnectionEpoch );
		Assert.AreNotEqual( stale.CapabilityRevision, current.CapabilityRevision );
		Assert.IsNotNull( service.ValidateProof( stale ) );
		Assert.IsNull( service.ValidateProof( current ) );
	}

	[TestMethod]
	public void CharacterChurnPrunesRevisionsWithoutWeakeningStaleProofRejection()
	{
		var service = new InventoryAccessService();
		var connection = ConnectionId.New();
		InventoryAccessProof? firstProof = null;
		service.OpenConnection( connection );
		for ( var index = 0; index < 10_000; index++ )
		{
			var character = CharacterId.New();
			var inventory = InventoryId.New();
			service.Grant( CharacterGrant( connection, character, inventory ) );
			var proof = service.Prove( connection, character, inventory, InventoryCapability.View );
			Assert.IsNotNull( proof );
			firstProof ??= proof;
			Assert.HasCount( 1, service.RevokeCharacter( connection, character ) );
			Assert.IsNotNull( service.ValidateProof( proof ) );
		}

		Assert.AreEqual( 0, service.RevisionEntryCount );
		Assert.AreEqual( 1, service.ConnectionEpochCount );
		Assert.IsNotNull( firstProof );
		Assert.IsNotNull( service.ValidateProof( firstProof ) );
		Assert.IsEmpty( service.RevokeConnection( connection ) );
		Assert.AreEqual( 0, service.ConnectionEpochCount );
	}

	[TestMethod]
	public void PartialRevocationRetainsOneRevisionAndInvalidatesPriorProofs()
	{
		var service = new InventoryAccessService();
		var connection = ConnectionId.New();
		var character = CharacterId.New();
		var inventory = InventoryId.New();
		var session = InteractionSessionId.New();
		service.OpenConnection( connection );
		service.Grant( CharacterGrant( connection, character, inventory ) );
		var beforeSession = service.Prove( connection, character, inventory, InventoryCapability.View );
		Assert.IsNotNull( beforeSession );
		service.Grant( new InventoryGrant
		{
			ConnectionId = connection,
			CharacterId = character,
			InventoryId = inventory,
			Capabilities = InventoryCapability.Move,
			Kind = InventoryGrantKind.InteractionSession,
			SessionId = session
		} );
		var duringSession = service.Prove( connection, character, inventory, InventoryCapability.View );
		Assert.IsNotNull( duringSession );
		Assert.IsNotNull( service.ValidateProof( beforeSession ) );

		Assert.HasCount( 1, service.RevokeSession( session ) );
		Assert.AreEqual( 1, service.RevisionEntryCount );
		Assert.IsNotNull( service.ValidateProof( duringSession ) );
		var afterSession = service.Prove( connection, character, inventory, InventoryCapability.View );
		Assert.IsNotNull( afterSession );
		Assert.IsNull( service.ValidateProof( afterSession ) );
	}

	private static InventoryGrant CharacterGrant(
		ConnectionId connection,
		CharacterId character,
		InventoryId inventory ) => new()
	{
		ConnectionId = connection,
		CharacterId = character,
		InventoryId = inventory,
		Capabilities = InventoryCapability.View,
		Kind = InventoryGrantKind.Character
	};
}
