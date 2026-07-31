#nullable enable

using System.Text;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class SnapshotWireCodecTests
{
	[TestMethod]
	public void CharacterListRoundTripsPresentAndAbsentOptionalClass()
	{
		var snapshot = new CharacterListSnapshot( 17, new[]
		{
			new CharacterSummarySnapshot(
				CharacterId.New(),
				2,
				"Barney",
				"Second slot",
				new DefinitionId( "model.barney" ),
				new FactionId( "citizen" ),
				new ClassId( "worker" ),
				new DateTimeOffset( 2026, 7, 13, 12, 30, 0, TimeSpan.FromHours( -4 ) ),
				false ),
			new CharacterSummarySnapshot(
				CharacterId.New(),
				0,
				"Alyx",
				"First slot",
				new DefinitionId( "model.alyx" ),
				new FactionId( "resistance" ),
				null,
				DateTimeOffset.UnixEpoch,
				true )
		} );

		var encoded = SnapshotWireCodec.EncodeCharacterList( snapshot );
		var decoded = SnapshotWireCodec.DecodeCharacterList( encoded.Value );

		Assert.IsTrue( encoded.Succeeded );
		Assert.IsTrue( decoded.Succeeded );
		Assert.AreEqual( 17L, decoded.Value.Revision );
		CollectionAssert.AreEqual( new[] { 0, 2 }, decoded.Value.Characters.Select( value => value.Slot ).ToArray() );
		Assert.IsNull( decoded.Value.Characters[0].Class );
		Assert.AreEqual( "worker", decoded.Value.Characters[1].Class?.Value );
		AssertCanonical( encoded.Value, SnapshotWireCodec.EncodeCharacterList( decoded.Value ) );
	}

	[TestMethod]
	public void ClientStateRoundTripsFullImmutableGraph()
	{
		var connectionId = ConnectionId.New();
		var characterId = CharacterId.New();
		var inventoryId = InventoryId.New();
		var emptyInventoryId = InventoryId.New();
		var player = new PlayerPublicSnapshot(
			connectionId,
			76561198000000001UL,
			"Account Name",
			characterId,
			"Known Name",
			"Detailed description",
			new DefinitionId( "model.citizen" ),
			new FactionId( "citizen" ),
			new ClassId( "worker" ),
			false,
			true,
			"Citizen" );
		var privatePlayer = new PlayerPrivateSnapshot(
			characterId,
			125,
			inventoryId,
			new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
			{
				["bool"] = SnapshotValue.Boolean( true ),
				["choice"] = SnapshotValue.Choice( "blue" ),
				["integer"] = SnapshotValue.Integer( long.MinValue ),
				["string"] = SnapshotValue.String( "line one\nline two \U0001F680" )
			},
			new[] { "doors.open", "inventory.search" } );

		var roster = new PlayerRosterSnapshot( 4, new[]
		{
			new PlayerRosterRowSnapshot( connectionId, characterId,
				new Dictionary<string, SnapshotValue> { ["name"] = SnapshotValue.String( "Known Name" ) } ),
			new PlayerRosterRowSnapshot( ConnectionId.New(), null,
				new Dictionary<string, SnapshotValue>() )
		} );
		var views = new[]
		{
			new SchemaViewSnapshot(
				"objectives",
				9,
				new Dictionary<string, SnapshotValue> { ["title"] = SnapshotValue.String( "City Status" ) },
				new[]
				{
					(IReadOnlyDictionary<string, SnapshotValue>)new Dictionary<string, SnapshotValue>
					{
						["active"] = SnapshotValue.Boolean( true ),
						["count"] = SnapshotValue.Integer( 2 )
					}
				} ),
			new SchemaViewSnapshot( "empty_panel", 1 )
		};

		var items = new[]
		{
			new InventoryItemSnapshot(
				ItemId.New(),
				new DefinitionId( "item.radio" ),
				"Radio",
				"A handheld radio",
				"communication",
				0, 0, 1, 1, 1,
				new[]
				{
					new ItemActionSnapshot( new ActionId( "use" ), "Use", true,
						Invocation: ItemActionInvocationKind.DedicatedPanel ),
					new ItemActionSnapshot( new ActionId( "repair" ), "Repair", false, "Requires tools" )
				},
				new Dictionary<string, SnapshotValue> { ["frequency"] = SnapshotValue.Integer( 1011 ) },
				true ),
			new InventoryItemSnapshot(
				ItemId.New(),
				new DefinitionId( "item.crate" ),
				"Crate",
				"A small crate",
				"storage",
				1, 0, 2, 1, 3,
				state: new Dictionary<string, SnapshotValue>(),
				canDrop: false,
				dropDisabledReason: "Too heavy" )
		};
		var inventories = new[]
		{
			new InventorySnapshot( inventoryId, 8, InventoryViewKind.Main, "Inventory", 4, 4, items ),
			new InventorySnapshot( emptyInventoryId, 0, InventoryViewKind.Storage, "Empty", 2, 2,
				Array.Empty<InventoryItemSnapshot>() )
		};
		var action = new ActionProgressSnapshot(
			Guid.NewGuid(),
			new ActionId( "search" ),
			"Searching",
			new DateTimeOffset( 2026, 7, 13, 17, 0, 0, TimeSpan.Zero ),
			TimeSpan.FromMilliseconds( 1750 ),
			true );
		var snapshot = new ClientStateSnapshot(
			new ClientStateEpoch( ConnectionEpoch.New(), 3, 11 ),
			player,
			privatePlayer,
			roster,
			views,
			inventories,
			action );

		var encoded = SnapshotWireCodec.EncodeClientState( snapshot );
		var decoded = SnapshotWireCodec.DecodeClientState( encoded.Value );

		Assert.IsTrue( encoded.Succeeded );
		Assert.IsTrue( decoded.Succeeded );
		Assert.AreEqual( characterId, decoded.Value.Player.CharacterId );
		Assert.AreEqual( "Citizen", decoded.Value.Player.ReplicatedCharacterName );
		Assert.AreEqual( long.MinValue, decoded.Value.PrivatePlayer!.Values["integer"].IntegerValue );
		Assert.AreEqual( "line one\nline two \U0001F680", decoded.Value.PrivatePlayer.Values["string"].StringValue );
		CollectionAssert.AreEqual(
			new[] { "doors.open", "inventory.search" },
			decoded.Value.PrivatePlayer.Permissions.ToArray() );
		Assert.HasCount( 2, decoded.Value.Roster.Rows );
		Assert.HasCount( 2, decoded.Value.SchemaViews );
		Assert.HasCount( 2, decoded.Value.Inventories );
		var populatedInventory = decoded.Value.Inventories.Single( value => value.InventoryId == inventoryId );
		var emptyInventory = decoded.Value.Inventories.Single( value => value.InventoryId == emptyInventoryId );
		Assert.HasCount( 2, populatedInventory.Items );
		Assert.IsEmpty( emptyInventory.Items );
		Assert.AreEqual( ItemActionInvocationKind.DedicatedPanel,
			populatedInventory.Items[0].Actions[0].Invocation );
		Assert.AreEqual( "Requires tools", populatedInventory.Items[0].Actions[1].DisabledReason );
		Assert.AreEqual( "Too heavy", populatedInventory.Items[1].DropDisabledReason );
		Assert.AreEqual( TimeSpan.FromMilliseconds( 1750 ), decoded.Value.ActiveAction!.Duration );
		Assert.ThrowsExactly<NotSupportedException>( () =>
			((IDictionary<string, SnapshotValue>)decoded.Value.PrivatePlayer.Values)
				.Add( "mutated", SnapshotValue.Boolean( false ) ) );
		AssertCanonical( encoded.Value, SnapshotWireCodec.EncodeClientState( decoded.Value ) );
	}

	[TestMethod]
	public void ClientStateRoundTripsAbsentOptionalStateAndEmptyCollections()
	{
		var snapshot = new ClientStateSnapshot(
			new ClientStateEpoch( ConnectionEpoch.New(), 0, 1 ),
			new PlayerPublicSnapshot(
				ConnectionId.New(), 42, "Account", null, string.Empty, string.Empty,
				null, null, null, false, false ),
			null,
			PlayerRosterSnapshot.Empty,
			Array.Empty<SchemaViewSnapshot>(),
			Array.Empty<InventorySnapshot>(),
			null );

		var encoded = SnapshotWireCodec.EncodeClientState( snapshot );
		var decoded = SnapshotWireCodec.DecodeClientState( encoded.Value );

		Assert.IsTrue( decoded.Succeeded );
		Assert.IsNull( decoded.Value.Player.CharacterId );
		Assert.IsNull( decoded.Value.PrivatePlayer );
		Assert.IsNull( decoded.Value.ActiveAction );
		Assert.IsEmpty( decoded.Value.SchemaViews );
		Assert.IsEmpty( decoded.Value.Inventories );
		Assert.IsEmpty( decoded.Value.Roster.Rows );
		AssertCanonical( encoded.Value, SnapshotWireCodec.EncodeClientState( decoded.Value ) );
	}

	[TestMethod]
	public void ChatRoundTripsAuthoredAnonymousAndEmptyDeliveries()
	{
		var epoch = new ChatDeliveryEpoch( ConnectionEpoch.New(), 5 );
		var snapshot = new ChatSnapshot( epoch, 12, new[]
		{
			new ChatMessageSnapshot(
				Guid.NewGuid(), "ic", CharacterId.New(), "Alyx", "Hello", DateTimeOffset.UnixEpoch ),
			new ChatMessageSnapshot(
				Guid.NewGuid(), "system", null, string.Empty, "Welcome", DateTimeOffset.UnixEpoch.AddSeconds( 1 ) )
		} );

		var encoded = SnapshotWireCodec.EncodeChat( snapshot );
		var decoded = SnapshotWireCodec.DecodeChat( encoded.Value );

		Assert.IsTrue( decoded.Succeeded );
		Assert.AreEqual( epoch, decoded.Value.Epoch );
		Assert.HasCount( 2, decoded.Value.Messages );
		Assert.IsNotNull( decoded.Value.Messages[0].AuthorCharacterId );
		Assert.IsNull( decoded.Value.Messages[1].AuthorCharacterId );
		AssertCanonical( encoded.Value, SnapshotWireCodec.EncodeChat( decoded.Value ) );

		var empty = new ChatSnapshot( epoch, 13, Array.Empty<ChatMessageSnapshot>() );
		var emptyEncoded = SnapshotWireCodec.EncodeChat( empty );
		var emptyDecoded = SnapshotWireCodec.DecodeChat( emptyEncoded.Value );
		Assert.IsTrue( emptyDecoded.Succeeded );
		Assert.IsEmpty( emptyDecoded.Value.Messages );
		AssertCanonical( emptyEncoded.Value, SnapshotWireCodec.EncodeChat( emptyDecoded.Value ) );
	}

	[TestMethod]
	public void DecodeRejectsEmptyMalformedOversizedDeepDuplicateAndWrongContractPayloads()
	{
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( null! ) );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Array.Empty<byte>() ) );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes( "not-json" ) ) );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList(
			new byte[SnapshotWireCodec.MaximumPayloadBytes + 1] ) );

		var valid = SnapshotWireCodec.EncodeCharacterList(
			new CharacterListSnapshot( 0, Array.Empty<CharacterSummarySnapshot>() ) ).Value;
		var json = Encoding.UTF8.GetString( valid );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes(
			json.Replace( "\"version\":1", "\"version\":2", StringComparison.Ordinal ) ) ) );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes(
			json.Replace( "\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal ) ) ) );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes(
			json.Replace( "\"version\":1", "\"unknown\":true,\"version\":1", StringComparison.Ordinal ) ) ) );
		AssertRejected( SnapshotWireCodec.DecodeChat( valid ) );

		var nestedValid = SnapshotWireCodec.EncodeCharacterList(
			new CharacterListSnapshot( 1, new[]
			{
				new CharacterSummarySnapshot(
					CharacterId.New(), 0, "Name", "Description", new DefinitionId( "model" ),
					new FactionId( "citizen" ), null, DateTimeOffset.UnixEpoch, false )
			} ) ).Value;
		var nestedJson = Encoding.UTF8.GetString( nestedValid );
		var nestedUnknown = nestedJson.Replace(
			"\"slot\":0",
			"\"futureMember\":true,\"slot\":0",
			StringComparison.Ordinal );
		Assert.AreNotEqual( nestedJson, nestedUnknown );
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes( nestedUnknown ) ) );

		var deep = "{\"version\":1,\"kind\":\"character-list\",\"payload\":" +
			new string( '[', SnapshotWireCodec.MaximumJsonDepth + 1 ) + "0" +
			new string( ']', SnapshotWireCodec.MaximumJsonDepth + 1 ) + "}";
		AssertRejected( SnapshotWireCodec.DecodeCharacterList( Encoding.UTF8.GetBytes( deep ) ) );
	}

	[TestMethod]
	public void SnapshotValueConverterRejectsUnknownUnionCasesAndEncodeEnforcesBound()
	{
		var full = CreateSmallStateWithChoice();
		var encoded = SnapshotWireCodec.EncodeClientState( full );
		var json = Encoding.UTF8.GetString( encoded.Value );
		var unknownKind = json.Replace(
			"\"kind\":\"choice\"",
			"\"kind\":\"future\"",
			StringComparison.Ordinal );
		Assert.AreNotEqual( json, unknownKind );
		AssertRejected( SnapshotWireCodec.DecodeClientState( Encoding.UTF8.GetBytes( unknownKind ) ) );

		var oversized = new ChatSnapshot(
			new ChatDeliveryEpoch( ConnectionEpoch.New(), 0 ),
			0,
			new[]
			{
				new ChatMessageSnapshot(
					Guid.NewGuid(), "ic", null, string.Empty,
					new string( 'x', SnapshotWireCodec.MaximumPayloadBytes + 1 ), DateTimeOffset.UnixEpoch )
			} );
		var rejectedEncode = SnapshotWireCodec.EncodeChat( oversized );
		Assert.IsTrue( rejectedEncode.Failed );
		Assert.AreEqual( ErrorCode.InvalidArgument, rejectedEncode.Error?.Code );
	}

	private static ClientStateSnapshot CreateSmallStateWithChoice()
	{
		var characterId = CharacterId.New();
		return new ClientStateSnapshot(
			new ClientStateEpoch( ConnectionEpoch.New(), 1, 1 ),
			new PlayerPublicSnapshot(
				ConnectionId.New(), 1, "Account", characterId, "Name", "Description",
				new DefinitionId( "model" ), new FactionId( "citizen" ), null, false, false, "Citizen" ),
			new PlayerPrivateSnapshot(
				characterId, 0, null,
				new Dictionary<string, SnapshotValue> { ["choice"] = SnapshotValue.Choice( "one" ) } ),
			PlayerRosterSnapshot.Empty,
			Array.Empty<SchemaViewSnapshot>(),
			Array.Empty<InventorySnapshot>(),
			null );
	}

	private static void AssertCanonical( byte[] expected, OperationResult<byte[]> reencoded )
	{
		Assert.IsTrue( reencoded.Succeeded );
		CollectionAssert.AreEqual( expected, reencoded.Value );
	}

	private static void AssertRejected<T>( OperationResult<T> result )
	{
		Assert.IsTrue( result.Failed );
		Assert.AreEqual( ErrorCode.InvalidArgument, result.Error?.Code );
	}
}
