#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class SnapshotTests
{
	[TestMethod]
	public void PrivateAndCreationSnapshotsDefensivelyCopyDictionariesAndPermissions()
	{
		var characterId = CharacterId.New();
		var values = new Dictionary<string, SnapshotValue>(StringComparer.Ordinal)
		{
			["rank"] = SnapshotValue.Choice("unit")
		};
		var permissions = new List<string> { "doors.open", "admin", "admin" };
		var privateSnapshot = new PlayerPrivateSnapshot(
			characterId, 125, InventoryId.New(), values, permissions);
		var creationInput = new CharacterCreationInput(
			"Alyx", "Description", new DefinitionId("citizen_model"),
			new FactionId("citizen"), null, values);

		values["rank"] = SnapshotValue.Choice("commander");
		permissions.Clear();

		Assert.AreEqual("unit", privateSnapshot.Values["rank"].StringValue);
		CollectionAssert.AreEqual(new[] { "admin", "doors.open" }, privateSnapshot.Permissions.ToArray());
		Assert.AreEqual("unit", creationInput.Fields["rank"].StringValue);
	}

	[TestMethod]
	public void CharacterListCopiesAndOrdersItsInput()
	{
		var source = new List<CharacterSummarySnapshot>
		{
			Character(2, "Third"),
			Character(0, "First")
		};
		var snapshot = new CharacterListSnapshot(7, source);

		source.Clear();

		Assert.AreEqual(7L, snapshot.Revision);
		CollectionAssert.AreEqual(new[] { 0, 2 }, snapshot.Characters.Select(value => value.Slot).ToArray());
	}

	[TestMethod]
	public void RosterRevisionCanAdvanceWithoutRecopyingImmutableRows()
	{
		var source = new List<PlayerRosterRowSnapshot>
		{
			new( ConnectionId.New(), CharacterId.New() )
		};
		var snapshot = new PlayerRosterSnapshot( 1, source );
		source.Clear();

		var advanced = snapshot.WithRevision( 2 );

		Assert.AreEqual( 2L, advanced.Revision );
		Assert.HasCount( 1, advanced.Rows );
		Assert.AreSame( snapshot.Rows, advanced.Rows );
		Assert.AreSame( advanced, advanced.WithRevision( 2 ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => snapshot.WithRevision( -1 ) );
	}

	[TestMethod]
	public void InventorySnapshotsCopyNestedActionsAndItems()
	{
		var actions = new List<ItemActionSnapshot>
		{
			new(new ActionId("use"), "Use", true, Invocation: ItemActionInvocationKind.DedicatedPanel)
		};
		var state = new Dictionary<string, SnapshotValue>(StringComparer.Ordinal)
		{
			["powered"] = SnapshotValue.Boolean(true)
		};
		var item = new InventoryItemSnapshot(
			ItemId.New(), new DefinitionId("test.item"), "Item", "Description", "General",
			0, 0, 1, 1, 1, actions, state, true);
		var items = new List<InventoryItemSnapshot> { item };
		var inventory = new InventorySnapshot(
			InventoryId.New(), 3, InventoryViewKind.Main, "Inventory", 4, 4, items);

		actions.Clear();
		state["powered"] = SnapshotValue.Boolean(false);
		items.Clear();

		Assert.HasCount(1, item.Actions);
		Assert.HasCount(1, inventory.Items);
		Assert.AreEqual("Use", inventory.Items[0].Actions[0].Label);
		Assert.AreEqual(ItemActionInvocationKind.DedicatedPanel, item.Actions[0].Invocation);
		Assert.IsTrue(item.State["powered"].BooleanValue);
		Assert.IsTrue(item.CanDrop);
		Assert.IsNull(item.DropDisabledReason);
	}

	[TestMethod]
	public void ChatSnapshotCopiesHostOrderedMessages()
	{
		var later = new ChatMessageSnapshot(
			Guid.NewGuid(), "ic", CharacterId.New(), "Later", "Second",
			DateTimeOffset.UnixEpoch.AddSeconds(2));
		var earlier = new ChatMessageSnapshot(
			Guid.NewGuid(), "ic", CharacterId.New(), "Earlier", "First",
			DateTimeOffset.UnixEpoch.AddSeconds(1));
		var source = new List<ChatMessageSnapshot> { earlier, later };
		var snapshot = new ChatSnapshot(
			new ChatDeliveryEpoch(ConnectionEpoch.New(), 1), 2, source);

		source.Clear();

		Assert.HasCount(2, snapshot.Messages);
		Assert.AreEqual("First", snapshot.Messages[0].Text);
		Assert.AreEqual("Second", snapshot.Messages[1].Text);
	}

	private static CharacterSummarySnapshot Character(int slot, string name) => new(
		CharacterId.New(),
		slot,
		name,
		"A sufficiently detailed description.",
		new DefinitionId("citizen_model"),
		new FactionId("citizen"),
		null,
		DateTimeOffset.UnixEpoch,
		false);
}
