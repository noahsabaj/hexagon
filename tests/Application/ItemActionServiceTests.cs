#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class ItemActionServiceTests
{
	private const string ExecuteAction = "execute";
	private const string MissingHandlerAction = "missing_handler";

	[TestMethod]
	public async Task TypedArgumentsAreCopiedIntoTheValidatedPlannerContext()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var observed = 0L;
		var handler = new RecordingActionHandler(
			ExecuteAction,
			context =>
			{
				observed = context.Arguments["amount"].IntegerValue;
				return OperationResult<ItemActionPlan>.Success( new ItemActionPlan() );
			} );
		var service = CreateService( environment, CompileSchema(), handler );
		var arguments = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["amount"] = SnapshotValue.Integer( 7 )
		};

		var result = await service.ExecuteCommittedAsync(
			seeded.Actor,
			seeded.Inventory.Id,
			seeded.Item.Id,
			new ActionId( ExecuteAction ),
			arguments );
		arguments["amount"] = SnapshotValue.Integer( 100 );

		Assert.IsTrue( result.Succeeded );
		Assert.IsNotNull( result.Value );
		Assert.IsEmpty( result.Value.Documents,
			"A successful read-only action still returns the exact provider-issued empty receipt." );
		Assert.AreEqual( 7L, observed );
	}

	[TestMethod]
	public async Task ForgedInventoryMembershipIsRejectedBeforeHandlerExecution()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var actor = ApplicationServiceTestEnvironment.Actor();
		var character = ApplicationServiceTestEnvironment.Character( actor.AccountId, 1, actor.CharacterId );
		var item = ApplicationServiceTestEnvironment.Item();
		var actual = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character( actor.CharacterId ),
			new[] { new InventoryPlacement( item.Id, 0, 0 ) } );
		var forged = ApplicationServiceTestEnvironment.Inventory( InventoryOwner.Character( actor.CharacterId ) );
		await SeedAsync( environment, character, new[] { item }, new[] { actual, forged } );
		environment.Grant( actor, forged.Id, InventoryCapability.View | InventoryCapability.Use );
		var handler = SuccessfulNoOpHandler();
		var service = CreateService( environment, CompileSchema(), handler );

		var result = await service.ExecuteAsync(
			actor, forged.Id, item.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.NotFound, result.Error!.Code );
		Assert.AreEqual( 0, handler.CallCount );
		Assert.IsNotNull( FindInventory( environment, actual.Id ).Find( item.Id ) );
	}

	[TestMethod]
	public async Task MissingUseCapabilityIsRejectedBeforeHandlerExecution()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		environment.Grant( seeded.Actor, seeded.Inventory.Id, InventoryCapability.View );
		var handler = SuccessfulNoOpHandler();
		var service = CreateService( environment, CompileSchema(), handler );

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.Unauthorized, result.Error!.Code );
		Assert.AreEqual( 0, handler.CallCount );
	}

	[TestMethod]
	public async Task UnknownDefinitionAndMissingHandlerFailClosed()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		environment.Grant(
			seeded.Actor,
			seeded.Inventory.Id,
			InventoryCapability.View | InventoryCapability.Use );
		var handler = SuccessfulNoOpHandler();
		var service = CreateService( environment, CompileSchema(), handler );

		var unknown = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( "unknown" ) );
		var missingHandler = await service.ExecuteAsync(
			seeded.Actor,
			seeded.Inventory.Id,
			seeded.Item.Id,
			new ActionId( MissingHandlerAction ) );

		Assert.AreEqual( ErrorCode.UnknownDefinition, unknown.Error!.Code );
		Assert.AreEqual( ErrorCode.UnknownDefinition, missingHandler.Error!.Code );
		Assert.AreEqual( 0, handler.CallCount );
	}

	[TestMethod]
	public async Task HandlerExceptionFailsClosedWithoutPublishingOrMutation()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var events = new RecordingCommittedHandler();
		var diagnostics = new List<Exception>();
		var handler = new RecordingActionHandler(
			ExecuteAction,
			_ => throw new InvalidOperationException( "planner failed" ) );
		var service = CreateService( environment, CompileSchema(), handler, events, diagnostics.Add );

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.InternalError, result.Error!.Code );
		Assert.HasCount( 1, diagnostics );
		Assert.AreEqual( 0, events.Count );
		Assert.IsEmpty( FindItem( environment, seeded.Item.Id ).Traits );
	}

	[TestMethod]
	public async Task RevokedCapabilityInvalidatesEvenZeroMutationActionProof()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var events = new RecordingCommittedHandler();
		var handler = new RecordingActionHandler(
			ExecuteAction,
			_ =>
			{
				environment.Access.RevokeCharacter( seeded.Actor.ConnectionId, seeded.Actor.CharacterId );
				return OperationResult<ItemActionPlan>.Success( new ItemActionPlan() );
			} );
		var service = CreateService( environment, CompileSchema(), handler, events );

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.Conflict, result.Error!.Code );
		Assert.AreEqual( 0, events.Count );
	}

	[TestMethod]
	public async Task PlanCannotMutateItemOutsideProvenInventory()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		var outsider = ApplicationServiceTestEnvironment.Item();
		await environment.SeedAsync( unitOfWork =>
			unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( outsider.Id ), outsider ) );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var handler = new RecordingActionHandler(
			ExecuteAction,
			_ => OperationResult<ItemActionPlan>.Success( new ItemActionPlan
			{
				UpdatedItems = new Dictionary<ItemId, ItemRecord>
				{
					[outsider.Id] = WithTrait( outsider, "forged" )
				}
			} ) );
		var service = CreateService( environment, CompileSchema(), handler );

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.InvalidArgument, result.Error!.Code );
		Assert.IsEmpty( FindItem( environment, outsider.Id ).Traits );
		Assert.IsEmpty( FindItem( environment, seeded.Item.Id ).Traits );
	}

	[TestMethod]
	public async Task MultiItemPlanCommitsItemsInventoryAndCharacterAtomically()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedPairAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var events = new RecordingCommittedHandler();
		var handler = MultiAggregateHandler( seeded.First.Id, seeded.Second.Id, 25 );
		var service = CreateService( environment, CompileSchema(), handler, events );

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.First.Id, new ActionId( ExecuteAction ) );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( "first_updated", TraitName( FindItem( environment, seeded.First.Id ) ) );
		Assert.AreEqual( "second_updated", TraitName( FindItem( environment, seeded.Second.Id ) ) );
		Assert.AreEqual( 25L, FindCharacter( environment, seeded.Character.Id ).Balance );
		Assert.AreEqual(
			new InventoryPlacement( seeded.First.Id, 0, 1 ),
			FindInventory( environment, seeded.Inventory.Id ).Find( seeded.First.Id ) );
		Assert.AreEqual(
			new InventoryPlacement( seeded.Second.Id, 1, 1 ),
			FindInventory( environment, seeded.Inventory.Id ).Find( seeded.Second.Id ) );
		Assert.AreEqual( 1, events.Count );
	}

	[TestMethod]
	public async Task CommittedEventCarriesAnImmutablePresentationReceipt()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedSingleAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var sourceFields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal )
		{
			["body"] = SnapshotValue.String( "Host-authored document text." )
		};
		var receipt = new ItemActionPresentationReceipt(
			ItemActionPresentationKind.ReferenceDocument,
			"Reference document",
			sourceFields );
		var handler = new RecordingActionHandler(
			ExecuteAction,
			_ => OperationResult<ItemActionPlan>.Success( new ItemActionPlan
			{
				Presentation = receipt
			} ) );
		var events = new RecordingCommittedHandler();
		var service = CreateService( environment, CompileSchema(), handler, events );

		sourceFields["body"] = SnapshotValue.String( "Forged after construction." );
		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.Item.Id, new ActionId( ExecuteAction ) );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 1, events.Count );
		Assert.IsNotNull( events.Last );
		Assert.AreSame( receipt, events.Last.Presentation );
		Assert.AreEqual( ItemActionPresentationKind.ReferenceDocument, events.Last.Presentation!.Kind );
		Assert.AreEqual( "Host-authored document text.", events.Last.Presentation.Fields["body"].StringValue );
	}

	[TestMethod]
	public async Task InjectedCommitFailureLeavesEveryPlannedAggregateUnchanged()
	{
		await using var environment = await ApplicationServiceTestEnvironment.CreateAsync();
		var seeded = await SeedPairAsync( environment );
		GrantUse( environment, seeded.Actor, seeded.Inventory.Id );
		var events = new RecordingCommittedHandler();
		var service = CreateService(
			environment,
			CompileSchema(),
			MultiAggregateHandler( seeded.First.Id, seeded.Second.Id, 25 ),
			events );
		environment.Provider.FailNextCommit();

		var result = await service.ExecuteAsync(
			seeded.Actor, seeded.Inventory.Id, seeded.First.Id, new ActionId( ExecuteAction ) );

		Assert.AreEqual( ErrorCode.InternalError, result.Error!.Code );
		Assert.IsEmpty( FindItem( environment, seeded.First.Id ).Traits );
		Assert.IsEmpty( FindItem( environment, seeded.Second.Id ).Traits );
		Assert.AreEqual( 0L, FindCharacter( environment, seeded.Character.Id ).Balance );
		Assert.AreEqual(
			new InventoryPlacement( seeded.First.Id, 0, 0 ),
			FindInventory( environment, seeded.Inventory.Id ).Find( seeded.First.Id ) );
		Assert.AreEqual(
			new InventoryPlacement( seeded.Second.Id, 1, 0 ),
			FindInventory( environment, seeded.Inventory.Id ).Find( seeded.Second.Id ) );
		Assert.AreEqual( 0, events.Count );
	}

	private static ItemActionService CreateService(
		ApplicationServiceTestEnvironment environment,
		CompiledSchema schema,
		IItemActionHandler handler,
		RecordingCommittedHandler? events = null,
		Action<Exception>? diagnostics = null )
	{
		var eventBus = events is null
			? new PostCommitEventBus<ItemActionCommittedEvent>()
			: new PostCommitEventBus<ItemActionCommittedEvent>( new[]
			{
				new EventHandlerRegistration<ItemActionCommittedEvent>( "recorder", events )
			} );
		return new ItemActionService(
			environment.Repositories,
			schema,
			environment.Access,
			new SchemaItemShapeCatalog( schema, environment.Repositories ),
			new ItemActionRegistry( new[] { handler } ),
			ApplicationServiceTestEnvironment.AllowPolicy<ItemActionContext>(),
			eventBus,
			diagnostics );
	}

	private static RecordingActionHandler SuccessfulNoOpHandler() => new(
		ExecuteAction,
		_ => OperationResult<ItemActionPlan>.Success( new ItemActionPlan() ) );

	private static RecordingActionHandler MultiAggregateHandler(
		ItemId first,
		ItemId second,
		long balance ) => new(
		ExecuteAction,
		context => OperationResult<ItemActionPlan>.Success( new ItemActionPlan
		{
			UpdatedItems = new Dictionary<ItemId, ItemRecord>
			{
				[first] = WithTrait( context.InventoryItems[first], "first_updated" ),
				[second] = WithTrait( context.InventoryItems[second], "second_updated" )
			},
			UpdatedCharacter = context.Character with { Balance = balance },
			UpdatedInventory = context.Inventory with
			{
				Placements = new[]
				{
					new InventoryPlacement( first, 0, 1 ),
					new InventoryPlacement( second, 1, 1 )
				}
			}
		} ) );

	private static CompiledSchema CompileSchema()
	{
		var result = SchemaCompiler.Compile( new ActionSchema() );
		if ( result.Failed ) throw new InvalidOperationException( result.Error!.Message );
		return result.Value;
	}

	private static async Task<SingleSeed> SeedSingleAsync( ApplicationServiceTestEnvironment environment )
	{
		var actor = ApplicationServiceTestEnvironment.Actor();
		var character = ApplicationServiceTestEnvironment.Character( actor.AccountId, 1, actor.CharacterId );
		var item = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character( actor.CharacterId ),
			new[] { new InventoryPlacement( item.Id, 0, 0 ) } );
		await SeedAsync( environment, character, new[] { item }, new[] { inventory } );
		return new SingleSeed( actor, character, inventory, item );
	}

	private static async Task<PairSeed> SeedPairAsync( ApplicationServiceTestEnvironment environment )
	{
		var actor = ApplicationServiceTestEnvironment.Actor();
		var character = ApplicationServiceTestEnvironment.Character( actor.AccountId, 1, actor.CharacterId );
		var first = ApplicationServiceTestEnvironment.Item();
		var second = ApplicationServiceTestEnvironment.Item();
		var inventory = ApplicationServiceTestEnvironment.Inventory(
			InventoryOwner.Character( actor.CharacterId ),
			new[]
			{
				new InventoryPlacement( first.Id, 0, 0 ),
				new InventoryPlacement( second.Id, 1, 0 )
			} );
		await SeedAsync( environment, character, new[] { first, second }, new[] { inventory } );
		return new PairSeed( actor, character, inventory, first, second );
	}

	private static async Task SeedAsync(
		ApplicationServiceTestEnvironment environment,
		CharacterRecord character,
		IEnumerable<ItemRecord> items,
		IEnumerable<InventoryRecord> inventories )
	{
		await environment.SeedAsync( unitOfWork =>
		{
			unitOfWork.Create(
				environment.Repositories.Characters,
				DomainKeys.Character( character.Id ),
				character );
			unitOfWork.Create(
				environment.Repositories.CharacterLifecycleGuards,
				DomainKeys.CharacterLifecycleGuard( character.Id ),
				new CharacterLifecycleGuardRecord { CharacterId = character.Id, ReferenceRevision = 0 } );
			foreach ( var item in items )
				unitOfWork.Create( environment.Repositories.Items, DomainKeys.Item( item.Id ), item );
			foreach ( var inventory in inventories )
				unitOfWork.Create(
					environment.Repositories.Inventories,
					DomainKeys.Inventory( inventory.Id ),
					inventory );
		} );
	}

	private static void GrantUse(
		ApplicationServiceTestEnvironment environment,
		InventoryActor actor,
		InventoryId inventoryId ) =>
		environment.Grant( actor, inventoryId, InventoryCapability.View | InventoryCapability.Use );

	private static ItemRecord WithTrait( ItemRecord item, string name ) => item with
	{
		Traits = new Dictionary<string, TypedPayload>( StringComparer.Ordinal )
		{
			["state"] = ApplicationServiceTestEnvironment.StatePayload( name )
		}
	};

	private static string? TraitName( ItemRecord item ) =>
		item.Traits["state"].Data.GetProperty( "name" ).GetString();

	private static ItemRecord FindItem(
		ApplicationServiceTestEnvironment environment,
		ItemId id ) => environment.Repositories.Items.Find( DomainKeys.Item( id ) )!.Value;

	private static InventoryRecord FindInventory(
		ApplicationServiceTestEnvironment environment,
		InventoryId id ) => environment.Repositories.Inventories.Find( DomainKeys.Inventory( id ) )!.Value;

	private static CharacterRecord FindCharacter(
		ApplicationServiceTestEnvironment environment,
		CharacterId id ) => environment.Repositories.Characters.Find( DomainKeys.Character( id ) )!.Value;

	private sealed class ActionSchema : IHexSchema
	{
		public string Id => "item_action_test";

		public void Configure( SchemaBuilder builder )
		{
			builder.RegisterAction( new ActionDefinition( ExecuteAction ) );
			builder.RegisterAction( new ActionDefinition( MissingHandlerAction ) );
			builder.RegisterItem( new ItemDefinition(
				ApplicationServiceTestEnvironment.ItemDefinitionId,
				new[] { ExecuteAction, MissingHandlerAction } ) );
			builder.RegisterPersistedType( new PersistedTypeRegistration(
				ApplicationServiceTestEnvironment.StateTypeId,
				typeof(TestCharacterState),
				1 ) );
		}
	}

	private sealed class RecordingActionHandler : IItemActionHandler
	{
		private readonly Func<ItemActionContext, OperationResult<ItemActionPlan>> _plan;

		public RecordingActionHandler(
			string id,
			Func<ItemActionContext, OperationResult<ItemActionPlan>> plan )
		{
			Id = new ActionId( id );
			_plan = plan;
		}

		public ActionId Id { get; }
		public int CallCount { get; private set; }

		public OperationResult<ItemActionPlan> Plan( ItemActionContext context )
		{
			CallCount++;
			return _plan( context );
		}
	}

	private sealed class RecordingCommittedHandler : IEventHandler<ItemActionCommittedEvent>
	{
		public int Count { get; private set; }
		public ItemActionCommittedEvent? Last { get; private set; }

		public void Handle( ItemActionCommittedEvent @event )
		{
			Count++;
			Last = @event;
		}
	}

	private sealed record SingleSeed(
		InventoryActor Actor,
		CharacterRecord Character,
		InventoryRecord Inventory,
		ItemRecord Item );

	private sealed record PairSeed(
		InventoryActor Actor,
		CharacterRecord Character,
		InventoryRecord Inventory,
		ItemRecord First,
		ItemRecord Second );
}
