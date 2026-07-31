#nullable enable

using Hexagon.V2.Client;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Client;

[TestClass]
public sealed class ClientControllerTests
{
	[TestMethod]
	public async Task ControllerMapsEveryUiIntentToAnExplicitCommand()
	{
		var transport = new RecordingTransport();
		var controller = new HexClientController(transport);
		var characterId = CharacterId.New();
		var sourceId = InventoryId.New();
		var targetId = InventoryId.New();
		var itemId = ItemId.New();
		var actionId = new ActionId("use");
		var actionInstance = Guid.NewGuid();
		var actionArguments = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["amount"] = SnapshotValue.Integer( 3 )
		};
		var creation = new CharacterCreationInput(
			"Alyx", "Description", new DefinitionId("citizen_model"),
			new FactionId("citizen"), null);

		await controller.RequestCharactersAsync();
		await controller.CreateCharacterAsync(creation);
		await controller.LoadCharacterAsync(characterId);
		await controller.DeleteCharacterAsync(characterId);
		await controller.UnloadCharacterAsync();
		await controller.MoveItemAsync(sourceId, targetId, itemId, 2, 3);
		await controller.RunItemActionAsync(sourceId, itemId, actionId, actionArguments);
		actionArguments["amount"] = SnapshotValue.Integer( 99 );
		await controller.DropItemAsync(sourceId, itemId);
		await controller.PickUpItemAsync(itemId, targetId);
		await controller.SendChatAsync("ic", "Hello");
		await controller.CancelActionAsync(actionInstance);

		Assert.HasCount(11, transport.Commands);
		Assert.IsInstanceOfType<RequestCharacterListCommand>(transport.Commands[0]);
		Assert.AreSame(creation, ((CreateCharacterCommand)transport.Commands[1]).Input);
		Assert.AreEqual(characterId, ((LoadCharacterCommand)transport.Commands[2]).CharacterId);
		Assert.IsInstanceOfType<DeleteCharacterCommand>(transport.Commands[3]);
		Assert.IsInstanceOfType<UnloadCharacterCommand>(transport.Commands[4]);
		Assert.AreEqual((2, 3),
			(((MoveInventoryItemCommand)transport.Commands[5]).X,
				((MoveInventoryItemCommand)transport.Commands[5]).Y));
		Assert.AreEqual(actionId, ((RunItemActionCommand)transport.Commands[6]).ActionId);
		Assert.AreEqual( 3L, ((RunItemActionCommand)transport.Commands[6]).Arguments["amount"].IntegerValue );
		Assert.IsInstanceOfType<DropItemCommand>(transport.Commands[7]);
		Assert.IsInstanceOfType<PickUpItemCommand>(transport.Commands[8]);
		Assert.AreEqual("Hello", ((SendChatCommand)transport.Commands[9]).Text);
		Assert.AreEqual(actionInstance, ((CancelActionCommand)transport.Commands[10]).InstanceId);
	}

	[TestMethod]
	public async Task TransportFailureAndCancellationTokenArePropagated()
	{
		var expected = OperationResult.Failure(ErrorCode.Unauthorized, "Denied.");
		var transport = new RecordingTransport(expected);
		var controller = new HexClientController(transport);
		using var source = new CancellationTokenSource();

		var result = await controller.SendChatAsync("ic", "Hello", source.Token);

		Assert.AreEqual(ErrorCode.Unauthorized, result.Error!.Code);
		Assert.AreEqual(source.Token, transport.LastCancellationToken);
	}

	private sealed class RecordingTransport : IClientCommandTransport
	{
		private readonly OperationResult _result;

		public RecordingTransport(OperationResult? result = null) =>
			_result = result ?? OperationResult.Success();

		public List<ClientCommand> Commands { get; } = new();
		public CancellationToken LastCancellationToken { get; private set; }

		public ValueTask<OperationResult> SendAsync(
			ClientCommand command,
			CancellationToken cancellationToken = default)
		{
			Commands.Add(command);
			LastCancellationToken = cancellationToken;
			return ValueTask.FromResult(_result);
		}
	}
}
