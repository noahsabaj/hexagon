#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Client;

[TestClass]
public sealed class HexClientStoreTests
{
	[TestMethod]
	public void ThrowingSubscriberDoesNotSkipLaterSubscribersOrEscapeThePublish()
	{
		var store = new HexClientStore();
		var diagnostics = new List<Exception>();
		store.SubscriberFailureDiagnostic = diagnostics.Add;
		var received = new List<ClientStoreChange>();
		store.Changed += _ => throw new InvalidOperationException( "first subscriber threw" );
		store.Changed += received.Add;

		store.PrepareSession( ClientSessionNonce.New() );

		Assert.HasCount( 1, received, "The second subscriber must still observe the change." );
		Assert.AreEqual( ClientStoreChangeKind.SessionStarted, received[0].Kind );
		Assert.AreEqual( 1, store.SubscriberFailureCount );
		Assert.HasCount( 1, diagnostics );
		Assert.IsInstanceOfType<InvalidOperationException>( diagnostics[0] );
	}

	[TestMethod]
	public void CompleteStatePublicationAtomicallyTransitionsAndUnloadsCharacter()
	{
		var store = new HexClientStore();
		var changes = new List<ClientStoreChange>();
		store.Changed += changes.Add;
		var connection = ConnectionEpoch.New();
		var characterId = CharacterId.New();
		var characterList = CharacterList( characterId );
		var chat = Chat( new ChatDeliveryEpoch( connection, 1 ) );

		var scope = Establish( store, connection );
		store.ReplaceCharacterList( scope, characterList );
		Assert.IsTrue( store.ApplyState( scope, State( connection, 1, 1, characterId, new[] { Inventory() }, Action() ) ) );
		store.ReplaceChat( scope, chat );

		Assert.AreEqual( ClientLifecycleState.CharacterActive, store.Lifecycle );
		Assert.IsNotNull( store.PublicPlayer );
		Assert.IsNotNull( store.PrivatePlayer );
		Assert.HasCount( 1, store.Inventories );
		Assert.IsNotNull( store.ActiveAction );
		Assert.AreSame( chat, store.Chat );

		Assert.IsTrue( store.ApplyState( scope, State( connection, 2, 2, null ) ) );

		Assert.AreEqual( ClientLifecycleState.Connected, store.Lifecycle );
		Assert.IsNotNull( store.PublicPlayer );
		Assert.IsNull( store.PublicPlayer.CharacterId );
		Assert.IsNull( store.PrivatePlayer );
		Assert.IsEmpty( store.Inventories );
		Assert.IsNull( store.ActiveAction );
		Assert.AreSame( characterList, store.CharacterList );
		Assert.IsNull( store.Chat );
		Assert.AreEqual( ClientStoreChangeKind.StateApplied, changes[^1].Kind );

		store.ClearSession();
		Assert.AreEqual( ClientLifecycleState.Disconnected, store.Lifecycle );
		Assert.IsNull( store.State );
		Assert.IsNull( store.CharacterList );
		Assert.IsNull( store.Chat );
	}

	[TestMethod]
	public void CharacterSwitchReplacesEveryCharacterScopedViewInOnePublication()
	{
		var store = new HexClientStore();
		var connection = ConnectionEpoch.New();
		var scope = Establish( store, connection );
		var first = CharacterId.New();
		var second = CharacterId.New();
		store.ApplyState( scope, State( connection, 1, 1, first, new[] { Inventory( "First" ) }, Action() ) );
		store.ReplaceChat( scope, Chat( new ChatDeliveryEpoch( connection, 1 ) ) );

		var secondInventory = Inventory( "Second" );
		Assert.IsTrue( store.ApplyState( scope, State( connection, 2, 2, second, new[] { secondInventory } ) ) );

		Assert.AreEqual( second, store.PublicPlayer!.CharacterId );
		Assert.AreEqual( second, store.PrivatePlayer!.CharacterId );
		Assert.HasCount( 1, store.Inventories );
		Assert.AreEqual( "Second", store.Inventories[0].Title );
		Assert.IsNull( store.ActiveAction );
		Assert.IsNull( store.Chat, "Character-epoch changes must clear receiver-scoped chat history." );
	}

	[TestMethod]
	public void LateRevisionWrongConnectionAndUnversionedCharacterSwitchAreRejected()
	{
		var store = new HexClientStore();
		var firstConnection = ConnectionEpoch.New();
		var scope = Establish( store, firstConnection );
		var character = CharacterId.New();
		var accepted = State( firstConnection, 4, 10, character, new[] { Inventory( "Accepted" ) } );
		Assert.IsTrue( store.ApplyState( scope, accepted ) );

		Assert.IsFalse( store.ApplyState( scope, State( firstConnection, 4, 9, character ) ) );
		Assert.IsFalse( store.ApplyState( scope, State( ConnectionEpoch.New(), 5, 11, CharacterId.New() ) ) );
		Assert.IsFalse( store.ApplyState( scope, State( firstConnection, 4, 11, CharacterId.New() ) ) );

		Assert.AreSame( accepted, store.State );
		Assert.AreEqual( "Accepted", store.Inventories[0].Title );
	}

	[TestMethod]
	public void SnapshotRejectsMismatchedPrivateCharacterAndDuplicateInventories()
	{
		var connection = ConnectionEpoch.New();
		var character = CharacterId.New();
		Assert.ThrowsExactly<ArgumentException>( () => new ClientStateSnapshot(
			new ClientStateEpoch( connection, 1, 1 ),
			Public( character ),
			Private( CharacterId.New() ),
			PlayerRosterSnapshot.Empty,
			null,
			Array.Empty<InventorySnapshot>(),
			null ) );

		var inventory = Inventory();
		Assert.ThrowsExactly<ArgumentException>( () => new ClientStateSnapshot(
			new ClientStateEpoch( connection, 1, 1 ),
			Public( character ),
			Private( character ),
			PlayerRosterSnapshot.Empty,
			null,
			new[] { inventory, Inventory( "Duplicate", inventory.InventoryId ) },
			null ) );
	}

	[TestMethod]
	public void RosterAndSchemaViewsAreImmutableAtomicAndProtectedByTheSameEpoch()
	{
		var connection = ConnectionEpoch.New();
		var character = CharacterId.New();
		var rosterFields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["name"] = SnapshotValue.String( "Alyx" )
		};
		var rosterRows = new List<PlayerRosterRowSnapshot>
		{
			new( ConnectionId.New(), character, rosterFields )
		};
		var roster = new PlayerRosterSnapshot( 4, rosterRows );
		var panelFields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["title"] = SnapshotValue.String( "Permit Vendor" )
		};
		var row = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["stock"] = SnapshotValue.Integer( 3 )
		};
		var schemaView = new SchemaViewSnapshot( "vendor", 8, panelFields, new[] { row } );
		var accepted = State(
			connection, 1, 1, character,
			roster: roster,
			schemaViews: new[] { schemaView } );

		rosterFields["name"] = SnapshotValue.String( "Mutated" );
		rosterRows.Clear();
		panelFields["title"] = SnapshotValue.String( "Mutated" );
		row["stock"] = SnapshotValue.Integer( 0 );
		var store = new HexClientStore();
		var scope = Establish( store, connection );
		Assert.IsTrue( store.ApplyState( scope, accepted ) );

		Assert.AreEqual( "Alyx", store.Roster!.Rows[0].Fields["name"].StringValue );
		Assert.AreEqual( "Permit Vendor", store.SchemaViews!["vendor"].Fields["title"].StringValue );
		Assert.AreEqual( 3L, store.SchemaViews["vendor"].Rows[0]["stock"].IntegerValue );

		var lateRoster = new PlayerRosterSnapshot( 5 );
		Assert.IsFalse( store.ApplyState( scope, State(
			connection, 1, 1, character,
			roster: lateRoster,
			schemaViews: Array.Empty<SchemaViewSnapshot>() ) ) );
		Assert.AreSame( roster, store.Roster );
		Assert.IsTrue( store.SchemaViews.ContainsKey( "vendor" ) );
	}

	[TestMethod]
	public void AtomicStateRejectsDuplicatePanelIds()
	{
		var character = CharacterId.New();
		var view = new SchemaViewSnapshot( "civic", 1 );
		Assert.ThrowsExactly<ArgumentException>( () => State(
			ConnectionEpoch.New(), 1, 1, character,
			roster: PlayerRosterSnapshot.Empty,
			schemaViews: new[] { view, new SchemaViewSnapshot( "civic", 2 ) } ) );
	}

	[TestMethod]
	public void BeginSessionIsInstanceScopedAndIndependentOfListenHostState()
	{
		var hostProcessStore = new HexClientStore();
		var clientScopeStore = new HexClientStore();
		var hostCharacter = CharacterId.New();
		var hostConnection = ConnectionEpoch.New();
		var hostScope = Establish( hostProcessStore, hostConnection );
		hostProcessStore.ApplyState( hostScope, State(
			hostConnection, 1, 1, hostCharacter, new[] { Inventory( "Host inventory" ) } ) );

		var clientNonce = ClientSessionNonce.New();
		clientScopeStore.PrepareSession( clientNonce );

		Assert.AreEqual( ClientLifecycleState.CharacterActive, hostProcessStore.Lifecycle );
		Assert.HasCount( 1, hostProcessStore.Inventories );
		Assert.AreEqual( ClientLifecycleState.Disconnected, clientScopeStore.Lifecycle );
		Assert.IsNull( clientScopeStore.State );
		Assert.AreEqual( 1L, clientScopeStore.Version );
		Assert.IsEmpty( typeof(HexClientStore).GetFields(
			System.Reflection.BindingFlags.Static |
			System.Reflection.BindingFlags.Public |
			System.Reflection.BindingFlags.NonPublic) );
	}

	[TestMethod]
	public void OutOfOrderUniqueChatDeliveriesMergeWithoutLoweringRevisionOrDuplicatingMessages()
	{
		var store = new HexClientStore();
		var connection = ConnectionEpoch.New();
		var scope = Establish( store, connection );
		var epoch = new ChatDeliveryEpoch( connection, 1 );
		Assert.IsTrue( store.ApplyState( scope, State( connection, 1, 1, CharacterId.New() ) ) );
		var first = Message( "first", DateTimeOffset.UnixEpoch );
		var second = Message( "second", DateTimeOffset.UnixEpoch );
		var third = Message( "third", DateTimeOffset.UnixEpoch );
		var version = store.Version;

		store.ReplaceChat( scope, new ChatSnapshot( epoch, 4, new[] { first } ) );
		store.ReplaceChat( scope, new ChatSnapshot( epoch, 6, new[] { third } ) );
		store.ReplaceChat( scope, new ChatSnapshot( epoch, 5, new[] { second } ) );
		store.ReplaceChat( scope, new ChatSnapshot( epoch, 3, new[] { first } ) );

		Assert.AreEqual( 6L, store.Chat!.Revision );
		CollectionAssert.AreEqual( new[] { "first", "second", "third" },
			store.Chat.Messages.Select( message => message.Text ).ToArray() );
		Assert.AreEqual( version + 3, store.Version,
			"A delayed unique delivery must append; a delayed duplicate must not publish." );
	}

	[TestMethod]
	public void SameRevisionChunksMergeAndRetentionKeepsNewestBoundedHistory()
	{
		var store = new HexClientStore();
		var connection = ConnectionEpoch.New();
		var scope = Establish( store, connection );
		var epoch = new ChatDeliveryEpoch( connection, 1 );
		Assert.IsTrue( store.ApplyState( scope, State( connection, 1, 1, CharacterId.New() ) ) );
		var messages = Enumerable.Range( 0, HexClientStore.MaximumRetainedChatMessages + 25 )
			.Select( index => Message( index.ToString(), DateTimeOffset.UnixEpoch.AddSeconds( index ) ) )
			.ToArray();

		store.ReplaceChat( scope, new ChatSnapshot( epoch, 10, messages.Take( 150 ) ) );
		store.ReplaceChat( scope, new ChatSnapshot( epoch, 10, messages.Skip( 150 ) ) );

		Assert.AreEqual( 10L, store.Chat!.Revision );
		Assert.HasCount( HexClientStore.MaximumRetainedChatMessages, store.Chat.Messages );
		Assert.AreEqual( "25", store.Chat.Messages[0].Text );
		Assert.AreEqual( "224", store.Chat.Messages[^1].Text );
	}

	[TestMethod]
	public void ReceiverStoresRemainIsolatedWhenHostPublishesDifferentRecipientSnapshots()
	{
		var firstReceiver = new HexClientStore();
		var secondReceiver = new HexClientStore();
		var firstConnection = ConnectionEpoch.New();
		var secondConnection = ConnectionEpoch.New();
		var firstScope = Establish( firstReceiver, firstConnection );
		var secondScope = Establish( secondReceiver, secondConnection );
		var firstEpoch = new ChatDeliveryEpoch( firstConnection, 1 );
		var secondEpoch = new ChatDeliveryEpoch( secondConnection, 1 );
		Assert.IsTrue( firstReceiver.ApplyState( firstScope, State( firstConnection, 1, 1, CharacterId.New() ) ) );
		Assert.IsTrue( secondReceiver.ApplyState( secondScope, State( secondConnection, 1, 1, CharacterId.New() ) ) );
		firstReceiver.ReplaceChat( firstScope, new ChatSnapshot( firstEpoch, 1, new[] { Message( "first-only", DateTimeOffset.UnixEpoch ) } ) );
		secondReceiver.ReplaceChat( secondScope, new ChatSnapshot( secondEpoch, 1, new[] { Message( "second-only", DateTimeOffset.UnixEpoch ) } ) );
		firstReceiver.ReplaceChat( firstScope, new ChatSnapshot( firstEpoch, 2, new[] { Message( "first-next", DateTimeOffset.UnixEpoch.AddSeconds( 1 ) ) } ) );

		CollectionAssert.AreEqual( new[] { "first-only", "first-next" },
			firstReceiver.Chat!.Messages.Select( message => message.Text ).ToArray() );
		CollectionAssert.AreEqual( new[] { "second-only" },
			secondReceiver.Chat!.Messages.Select( message => message.Text ).ToArray() );
	}

	[TestMethod]
	public void PriorCharacterAndConnectionEpochDeliveriesCannotEnterTheCurrentView()
	{
		var store = new HexClientStore();
		var connection = ConnectionEpoch.New();
		var scope = Establish( store, connection );
		var firstCharacter = CharacterId.New();
		var secondCharacter = CharacterId.New();
		Assert.IsTrue( store.ApplyState( scope, State( connection, 1, 1, firstCharacter ) ) );
		store.ReplaceChat( scope, new ChatSnapshot(
			new ChatDeliveryEpoch( connection, 1 ), 8,
			new[] { Message( "first-character", DateTimeOffset.UnixEpoch ) } ) );
		Assert.IsTrue( store.ApplyState( scope, State( connection, 2, 2, secondCharacter ) ) );
		var version = store.Version;

		store.ReplaceChat( scope, new ChatSnapshot(
			new ChatDeliveryEpoch( connection, 1 ), 100,
			new[] { Message( "delayed-character", DateTimeOffset.UnixEpoch ) } ) );
		store.ReplaceChat( scope, new ChatSnapshot(
			new ChatDeliveryEpoch( ConnectionEpoch.New(), 2 ), 101,
			new[] { Message( "delayed-connection", DateTimeOffset.UnixEpoch ) } ) );

		Assert.IsNull( store.Chat );
		Assert.AreEqual( version, store.Version );

		var current = new ChatSnapshot(
			new ChatDeliveryEpoch( connection, 2 ), 1,
			new[] { Message( "current", DateTimeOffset.UnixEpoch ) } );
		store.ReplaceChat( scope, current );
		Assert.AreSame( current, store.Chat );
		store.ClearSession();
		var clearedVersion = store.Version;
		store.ReplaceChat( scope, current );
		Assert.IsNull( store.Chat );
		Assert.AreEqual( clearedVersion, store.Version );
	}

	[TestMethod]
	public void ClearAndReconnectRejectEveryPacketFromThePriorNonceAndHello()
	{
		var store = new HexClientStore();
		var oldConnection = ConnectionEpoch.New();
		var oldScope = Establish( store, oldConnection );
		Assert.IsTrue( store.ApplyState( oldScope, State( oldConnection, 1, 1, CharacterId.New() ) ) );
		store.ClearSession();

		var newNonce = ClientSessionNonce.New();
		var newScope = new ClientSessionScope( newNonce, ConnectionEpoch.New() );
		store.PrepareSession( newNonce );
		Assert.IsFalse( store.AcceptHello( new ClientSessionHello( oldScope ) ) );
		Assert.IsTrue( store.AcceptHello( new ClientSessionHello( newScope ) ) );
		Assert.IsFalse( store.Accepts( oldScope ), "Delayed command results are gated by the same exact scope." );

		Assert.IsFalse( store.ApplyState( oldScope, State( oldConnection, 2, 2, CharacterId.New() ) ) );
		Assert.IsFalse( store.ReplaceCharacterList( oldScope, new CharacterListSnapshot( 99, Array.Empty<CharacterSummarySnapshot>() ) ) );
		Assert.IsFalse( store.ReplaceChat( oldScope, new ChatSnapshot(
			new ChatDeliveryEpoch( oldConnection, 1 ), 99, Array.Empty<ChatMessageSnapshot>() ) ) );
		Assert.IsNull( store.State );
		Assert.IsNull( store.CharacterList );
		Assert.IsNull( store.Chat );
	}

	[TestMethod]
	public void ExactDuplicateHelloIsAnIdempotentNoOpButOtherScopesAreRejected()
	{
		var store = new HexClientStore();
		var scope = Establish( store, ConnectionEpoch.New() );
		var version = store.Version;

		Assert.IsTrue( store.AcceptHello( new ClientSessionHello( scope ) ) );
		Assert.AreEqual( version, store.Version );
		Assert.AreEqual( scope, store.Scope );

		Assert.IsFalse( store.AcceptHello( new ClientSessionHello(
			new ClientSessionScope( scope.Nonce, ConnectionEpoch.New() ) ) ) );
		Assert.IsFalse( store.AcceptHello( new ClientSessionHello(
			new ClientSessionScope( ClientSessionNonce.New(), scope.Connection ) ) ) );
		Assert.AreEqual( version + 2, store.Version );
		Assert.AreEqual( scope, store.Scope );
	}

	[TestMethod]
	public void SessionRevocationIsExactScopedAndClearsAllPublishedState()
	{
		var store = new HexClientStore();
		var connection = ConnectionEpoch.New();
		var scope = Establish( store, connection );
		var character = CharacterId.New();
		Assert.IsTrue( store.ApplyState( scope, State( connection, 1, 1, character ) ) );
		Assert.IsTrue( store.ReplaceCharacterList(
			scope,
			new CharacterListSnapshot( 1, Array.Empty<CharacterSummarySnapshot>() ) ) );

		var stale = new ClientSessionScope( ClientSessionNonce.New(), scope.Connection );
		Assert.IsFalse( store.RevokeSession( stale ) );
		Assert.AreEqual( scope, store.Scope );
		Assert.IsNotNull( store.State );

		Assert.IsTrue( store.RevokeSession( scope ) );
		Assert.AreEqual( ClientLifecycleState.Disconnected, store.Lifecycle );
		Assert.IsNull( store.Scope );
		Assert.IsNull( store.State );
		Assert.IsNull( store.CharacterList );
		Assert.IsNull( store.Chat );
		Assert.IsFalse( store.RevokeSession( scope ) );
	}

	private static ClientSessionScope Establish( HexClientStore store, ConnectionEpoch connection )
	{
		var nonce = ClientSessionNonce.New();
		var scope = new ClientSessionScope( nonce, connection );
		store.PrepareSession( nonce );
		Assert.IsTrue( store.AcceptHello( new ClientSessionHello( scope ) ) );
		return scope;
	}

	private static ClientStateSnapshot State(
		ConnectionEpoch connection,
		long characterEpoch,
		long revision,
		CharacterId? characterId,
		IEnumerable<InventorySnapshot>? inventories = null,
		ActionProgressSnapshot? action = null,
		PlayerRosterSnapshot? roster = null,
		IEnumerable<SchemaViewSnapshot>? schemaViews = null ) => new(
		new ClientStateEpoch( connection, characterEpoch, revision ),
		Public( characterId ),
		characterId is null ? null : Private( characterId.Value ),
		roster ?? PlayerRosterSnapshot.Empty,
		schemaViews,
		inventories,
		action );

	private static PlayerPublicSnapshot Public( CharacterId? characterId ) => new(
		ConnectionId.New(),
		7656119,
		"Player",
		characterId,
		characterId is null ? string.Empty : "Alyx Vance",
		characterId is null ? string.Empty : "Description",
		characterId is null ? null : new DefinitionId( "citizen_model" ),
		characterId is null ? null : new FactionId( "citizen" ),
		null,
		false,
		false );

	[TestMethod]
	public void OwnerFacingCharacterNameDoesNotBecomeTheDefaultReplicatedLabel()
	{
		var snapshot = Public( CharacterId.New() );

		Assert.AreEqual( "Alyx Vance", snapshot.CharacterName );
		Assert.AreEqual( "Unknown citizen", snapshot.ReplicatedCharacterName );
	}

	private static PlayerPrivateSnapshot Private( CharacterId characterId ) => new(
		characterId,
		100,
		InventoryId.New(),
		new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["cid"] = SnapshotValue.String( "12345" )
		} );

	private static CharacterListSnapshot CharacterList( CharacterId characterId ) => new(
		1,
		new[]
		{
			new CharacterSummarySnapshot(
				characterId,
				0,
				"Alyx Vance",
				"Description",
				new DefinitionId( "citizen_model" ),
				new FactionId( "citizen" ),
				null,
				DateTimeOffset.UnixEpoch,
				false )
		} );

	private static InventorySnapshot Inventory( string title = "Inventory", InventoryId? id = null ) => new(
		id ?? InventoryId.New(),
		1,
		InventoryViewKind.Main,
		title,
		4,
		4,
		Array.Empty<InventoryItemSnapshot>() );

	private static ChatSnapshot Chat( ChatDeliveryEpoch epoch ) => new(
		epoch,
		1,
		new[]
		{
			new ChatMessageSnapshot(
				Guid.NewGuid(), "ic", CharacterId.New(), "Speaker", "Hello",
				DateTimeOffset.UnixEpoch )
		} );

	private static ChatMessageSnapshot Message( string text, DateTimeOffset sentAt ) => new(
		Guid.NewGuid(), "ic", CharacterId.New(), "Speaker", text, sentAt );

	private static ActionProgressSnapshot Action() => new(
		Guid.NewGuid(),
		new ActionId( "use" ),
		"Using",
		DateTimeOffset.UnixEpoch,
		TimeSpan.FromSeconds( 3 ),
		true );
}
