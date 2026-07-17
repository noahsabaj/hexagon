#nullable enable

using System.Text.Json;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using KernelClassDefinition = Hexagon.V2.Kernel.Definitions.ClassDefinition;
using KernelFactionDefinition = Hexagon.V2.Kernel.Definitions.FactionDefinition;
using KernelItemDefinition = Hexagon.V2.Kernel.Definitions.ItemDefinition;

namespace Hexagon.V2.Tests.Application;

internal sealed class ApplicationServiceTestEnvironment : IAsyncDisposable
{
	private readonly HashSet<ConnectionId> _openConnections = new();
	public const string StateTypeId = "test.character-state";
	public const string ItemDefinitionId = "test.item";
	public const string BagDefinitionId = "test.bag";
	public const string WorldModel = "models/test/item.vmdl";

	private ApplicationServiceTestEnvironment(
		FaultInjectingPersistenceProvider provider,
		CompiledSchema schema,
		DomainRepositories repositories)
	{
		Provider = provider;
		Schema = schema;
		Repositories = repositories;
		PersistenceProfile = new SchemaPersistenceInvariantProfile(
			new PersistedTypeId( StateTypeId ),
			new[]
			{
				ItemPersistenceContract.WithoutTraits( ItemDefinitionId ),
				ItemPersistenceContract.WithoutTraits( BagDefinitionId )
			},
			Array.Empty<KeyValuePair<string, PersistedTypeId>>(),
			Array.Empty<KeyValuePair<string, PersistedTypeId>>() );
		Access = new InventoryAccessService();
		Layout = new InventoryLayoutService(new SchemaItemShapeCatalog(schema, repositories));
	}

	public FaultInjectingPersistenceProvider Provider { get; }
	public CompiledSchema Schema { get; }
	public DomainRepositories Repositories { get; }
	public SchemaPersistenceInvariantProfile PersistenceProfile { get; }
	public InventoryAccessService Access { get; }
	public InventoryLayoutService Layout { get; }

	public static async Task<ApplicationServiceTestEnvironment> CreateAsync(int? classCapacity = null)
	{
		var types = new PersistedTypeRegistry()
			.RegisterHexagonDomainTypes()
			.Register<TestCharacterState>(new PersistedTypeKey(StateTypeId), 1,
				PersistedValuePublication.Immutable);
		var provider = new FaultInjectingPersistenceProvider(new InMemoryPersistenceProvider(types));
		await provider.InitializeAsync();

		var compiled = SchemaCompiler.Compile(new TestSchema(classCapacity));
		if (compiled.Failed)
			throw new InvalidOperationException(compiled.Error!.Message);

		return new ApplicationServiceTestEnvironment(
			provider,
			compiled.Value,
			new DomainRepositories(provider));
	}

	public CharacterService CreateCharacterService(
		IEnumerable<ICharacterInitializer>? initializers = null,
		int inventoryWidth = 4,
		int inventoryHeight = 4,
		ICharacterStateFactory? stateFactory = null)
	{
		return new CharacterService(
			Repositories,
			Schema,
			new AllowAllModels(),
			stateFactory ?? new TestStateFactory(),
			initializers ?? Array.Empty<ICharacterInitializer>(),
			new TestIdGenerator(),
			new FixedClock(),
			Layout,
			AllowPolicy<CharacterCreationContext>(),
			AllowPolicy<CharacterDeletionContext>(),
			inventoryWidth: inventoryWidth,
			inventoryHeight: inventoryHeight);
	}

	public InventoryMutationService CreateInventoryMutationService() => new(
		Repositories,
		Access,
		Layout,
		AllowPolicy<InventoryTransferContext>());

	public WorldItemService CreateWorldItemService(bool modelIsValid = true) => new(
		Repositories,
		Schema,
		Access,
		Layout,
		new TestWorldModelCatalog(modelIsValid),
		AllowPolicy<WorldDropContext>(),
		AllowPolicy<WorldPickupContext>());

	public async Task SeedAsync(Action<IUnitOfWork> stage)
	{
		await using var unitOfWork = Provider.BeginUnitOfWork();
		stage(unitOfWork);
		var committed = await unitOfWork.CommitAsync();
		if (!committed.Succeeded)
			throw new InvalidOperationException(committed.Error!.Message);
	}

	public void Grant(
		InventoryActor actor,
		InventoryId inventoryId,
		InventoryCapability capabilities)
	{
		OpenConnection(actor.ConnectionId);
		Access.Grant(new InventoryGrant
		{
			ConnectionId = actor.ConnectionId,
			CharacterId = actor.CharacterId,
			InventoryId = inventoryId,
			Capabilities = capabilities,
			Kind = InventoryGrantKind.Character
		});
	}

	public void OpenConnection(ConnectionId connectionId)
	{
		if (_openConnections.Add(connectionId)) Access.OpenConnection(connectionId);
	}

	public static InventoryActor Actor() => new(ConnectionId.New(), new AccountId(101), CharacterId.New());

	public static CharacterCreationRequest Request(
		IReadOnlyDictionary<string, CreationValue>? fields = null) => new()
	{
		Name = "  Alyx Vance  ",
		Description = "  A sufficiently detailed character description.  ",
		Model = new DefinitionId("citizen_model"),
		Faction = new FactionId("citizen"),
		Fields = fields ?? new Dictionary<string, CreationValue>(StringComparer.Ordinal)
		{
			["nickname"] = CreationValue.String("alyx")
		}
	};

	public static CharacterRecord Character(AccountId account, int slot, CharacterId? id = null) => new()
	{
		Id = id ?? CharacterId.New(),
		AccountId = account,
		Slot = slot,
		Name = $"Character {slot}",
		Description = "A sufficiently detailed seeded description.",
		Model = new DefinitionId("citizen_model"),
		Faction = new FactionId("citizen"),
		Class = new ClassId("worker"),
		Balance = 0,
		CreatedAt = DateTimeOffset.UnixEpoch,
		LastPlayedAt = DateTimeOffset.UnixEpoch,
		SchemaState = StatePayload()
	};

	public static InventoryRecord Inventory(
		InventoryOwner owner,
		IEnumerable<InventoryPlacement>? placements = null,
		int width = 4,
		int height = 4,
		InventoryId? id = null) => new()
	{
		Id = id ?? InventoryId.New(),
		Owner = owner,
		Width = width,
		Height = height,
		Placements = placements?.ToArray() ?? Array.Empty<InventoryPlacement>()
	};

	public static ItemRecord Item(string definition = ItemDefinitionId, ItemId? id = null) => new()
	{
		Id = id ?? ItemId.New(),
		Definition = new DefinitionId(definition)
	};

	/// <summary>
	/// Canonical owner index record for an inventory, matching the commit-time invariant
	/// production enforces (one record per inventory under the owner-kind's canonical role).
	/// </summary>
	public static OwnerInventoryRecord OwnerIndex(InventoryRecord inventory, string role) => new()
	{
		Role = role,
		Owner = inventory.Owner,
		InventoryId = inventory.Id
	};

	public static WorldTransformRecord Transform() => new()
	{
		PositionX = 10,
		PositionY = 20,
		PositionZ = 30,
		RotationX = 0,
		RotationY = 0,
		RotationZ = 0,
		RotationW = 1
	};

	public static TypedPayload StatePayload(
		string name = "citizen",
		string typeId = StateTypeId,
		int version = 1) => new()
	{
		TypeId = new PersistedTypeId(typeId),
		TypeVersion = version,
		Data = JsonSerializer.SerializeToElement(
			new TestCharacterState(name),
			new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
	};

	public static PolicyPipeline<TContext> AllowPolicy<TContext>() => new(
		new PolicyHandler<TContext>("built_in", new AllowPolicyHandler<TContext>()));

	public ValueTask DisposeAsync() => Provider.DisposeAsync();

	private sealed class TestSchema : IHexSchema
	{
		private readonly int? _classCapacity;
		public TestSchema(int? classCapacity) => _classCapacity = classCapacity;
		public string Id => "test_schema";

		public void Configure(SchemaBuilder builder)
		{
			builder.RegisterCharacterField(new CharacterFieldDefinition(
				"nickname", CharacterFieldValueKind.String, true, true));
			builder.RegisterFaction(new KernelFactionDefinition("citizen", true, "worker", "Citizen"));
			builder.RegisterClass(new KernelClassDefinition("worker", "citizen", "Worker", _classCapacity));
			builder.RegisterItem(new KernelItemDefinition(
				ItemDefinitionId,
				Array.Empty<string>(),
				true,
				WorldModel,
				"Test Item"));
			builder.RegisterItem(new KernelItemDefinition(
				BagDefinitionId,
				Array.Empty<string>(),
				true,
				WorldModel,
				"Test Bag"));
			builder.RegisterPersistedType(new PersistedTypeRegistration(
				StateTypeId, typeof(TestCharacterState), 1));
		}
	}

	private sealed class AllowAllModels : ICharacterModelCatalog
	{
		public bool IsAllowed(DefinitionId model, FactionId faction, ClassId? characterClass) => true;
	}

	private sealed class TestStateFactory : ICharacterStateFactory
	{
		public OperationResult<CharacterStatePlan> Create(CharacterCreationContext context) =>
			OperationResult<CharacterStatePlan>.Success(new CharacterStatePlan(StatePayload(), 25));
	}

	private sealed class FixedClock : IHexClock
	{
		public DateTimeOffset UtcNow => new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
	}

	private sealed class TestIdGenerator : IAggregateIdGenerator
	{
		public CharacterId NewCharacterId() => CharacterId.New();
		public InventoryId NewInventoryId() => InventoryId.New();
		public ItemId NewItemId() => ItemId.New();
		public InteractionSessionId NewInteractionSessionId() => InteractionSessionId.New();
	}

	private sealed class TestWorldModelCatalog : IWorldModelCatalog
	{
		private readonly bool _isValid;
		public TestWorldModelCatalog(bool isValid) => _isValid = isValid;
		public bool IsValidModel(string modelPath) => _isValid && modelPath == WorldModel;
	}

	private sealed class AllowPolicyHandler<TContext> : IPolicy<TContext>
	{
		public PolicyDecision Evaluate(TContext context) => PolicyDecision.Allow();
	}
}

internal sealed record TestCharacterState(string Name);

internal sealed class TestCharacterInitializer : ICharacterInitializer
{
	private readonly Func<CharacterCreationContext, CharacterStatePlan,
		OperationResult<CharacterInitializerContribution>> _build;

	public TestCharacterInitializer(
		string id,
		Func<CharacterCreationContext, CharacterStatePlan,
			OperationResult<CharacterInitializerContribution>> build,
		int order = 0)
	{
		Id = id;
		Order = order;
		_build = build;
	}

	public string Id { get; }
	public int Order { get; }
	public OperationResult<CharacterInitializerContribution> Build(
		CharacterCreationContext context,
		CharacterStatePlan state) => _build(context, state);
}

internal sealed class FaultInjectingPersistenceProvider : IPersistenceProvider
{
	private readonly IPersistenceProvider _inner;
	private readonly Dictionary<string, int> _allCalls = new(StringComparer.Ordinal);
	private bool _failNextCommit;
	private Action? _beforeNextCommit;

	public FaultInjectingPersistenceProvider(IPersistenceProvider inner) =>
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));

	public PersistedTypeRegistry Types => _inner.Types;
	public PersistenceHealth Health => _inner.Health;
	public bool IsInitialized => _inner.IsInitialized;
	public PersistenceProviderState State => _inner.State;
	public Guid StoreId => _inner.StoreId;
	public Guid WriterEpoch => _inner.WriterEpoch;
	public long CompactionGeneration => _inner.CompactionGeneration;

	public void FailNextCommit() => _failNextCommit = true;
	public void BeforeNextCommit(Action callback)
	{
		ArgumentNullException.ThrowIfNull(callback);
		if (Interlocked.CompareExchange(ref _beforeNextCommit, callback, null) is not null)
			throw new InvalidOperationException("A before-commit callback is already pending.");
	}

	public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
		_inner.InitializeAsync(cancellationToken);

	public IPersistenceRepository<T> Repository<T>(string collection) where T : class =>
		new CountingRepository<T>(this, _inner.Repository<T>(collection));

	/// <summary>
	/// Number of full-collection All() enumerations issued so far, for asserting that
	/// keyed-probe code paths never fall back to store scans.
	/// </summary>
	public int AllCallCount(string collection)
	{
		lock (_allCalls) return _allCalls.GetValueOrDefault(collection);
	}

	private void RecordAllCall(string collection)
	{
		lock (_allCalls) _allCalls[collection] = _allCalls.GetValueOrDefault(collection) + 1;
	}

	public IUnitOfWork BeginUnitOfWork() => new FaultInjectingUnitOfWork(this, _inner.BeginUnitOfWork());

	public ValueTask<PersistenceResult<long>> CheckpointAsync(CancellationToken cancellationToken = default) =>
		_inner.CheckpointAsync(cancellationToken);

	public ValueTask<PersistenceShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default) =>
		_inner.ShutdownAsync(cancellationToken);

	public ValueTask DisposeAsync() => _inner.DisposeAsync();

	private bool ConsumeCommitFailure()
	{
		if (!_failNextCommit)
			return false;

		_failNextCommit = false;
		return true;
	}

	private Action? ConsumeBeforeCommit() => Interlocked.Exchange(ref _beforeNextCommit, null);

	private static IPersistenceRepository<T> Unwrap<T>(IPersistenceRepository<T> repository) where T : class =>
		repository is CountingRepository<T> counting ? counting.Inner : repository;

	private sealed class CountingRepository<T> : IPersistenceRepository<T> where T : class
	{
		private readonly FaultInjectingPersistenceProvider _provider;

		public CountingRepository(FaultInjectingPersistenceProvider provider, IPersistenceRepository<T> inner)
		{
			_provider = provider;
			Inner = inner;
		}

		public IPersistenceRepository<T> Inner { get; }
		public string Collection => Inner.Collection;
		public DocumentSnapshot<T>? Find(string key) => Inner.Find(key);

		public IReadOnlyList<DocumentSnapshot<T>> All()
		{
			_provider.RecordAllCall(Inner.Collection);
			return Inner.All();
		}
	}

	private sealed class FaultInjectingUnitOfWork : IUnitOfWork
	{
		private readonly FaultInjectingPersistenceProvider _provider;
		private readonly IUnitOfWork _inner;

		public FaultInjectingUnitOfWork(
			FaultInjectingPersistenceProvider provider,
			IUnitOfWork inner)
		{
			_provider = provider;
			_inner = inner;
		}

		public DocumentEditor<T>? Edit<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.Edit(Unwrap(repository), observed);

		public void RequireUnchanged<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.RequireUnchanged(Unwrap(repository), observed);

		public void Create<T>(IPersistenceRepository<T> repository, string key, T value) where T : class =>
			_inner.Create(Unwrap(repository), key, value);

		public void Put<T>(IPersistenceRepository<T> repository, string key, T value) where T : class =>
			_inner.Put(Unwrap(repository), key, value);

		public void Save<T>(DocumentEditor<T> editor) where T : class => _inner.Save(editor);

		public void Delete<T>(IPersistenceRepository<T> repository, DocumentSnapshot<T> observed) where T : class =>
			_inner.Delete(Unwrap(repository), observed);

		public void Require(ICommitPrecondition precondition) => _inner.Require(precondition);

		public ValueTask<PersistenceResult<CommitReceipt>> CommitAsync(
			CancellationToken cancellationToken = default)
		{
			_provider.ConsumeBeforeCommit()?.Invoke();
			if (!_provider.ConsumeCommitFailure())
				return _inner.CommitAsync(cancellationToken);

			return ValueTask.FromResult(PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError(
					PersistenceErrorCode.DurabilityFailed,
					"Injected commit failure.")));
		}

		public ValueTask DisposeAsync() => _inner.DisposeAsync();
	}
}
