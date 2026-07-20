#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Client;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using Sandbox;

namespace Hexagon.V2.Runtime;

public enum HexRuntimeReadiness
{
	Absent = 0,
	Initializing = 1,
	Ready = 2,
	Failed = 3,
	Disposing = 4,
	Disposed = 5
}

/// <summary>
/// Scene-scoped host/client composition root. Listen servers receive independent
/// scopes, while each host connection owns an ephemeral command and character epoch.
/// </summary>
public sealed class HexagonRuntimeSystem : GameObjectSystem<HexagonRuntimeSystem>, ISceneStartup, Component.INetworkListener
{
	private readonly Scene _runtimeScene;
	private readonly Dictionary<Guid, RuntimePlayerSession<HexPlayerBody>> _sessions = new();
	private readonly SpawnSlotAllocator _spawnSlots = new();
	private readonly CancellationTokenSource _hostLifetime = new();
	private readonly AsyncOperationRegistry _hostOperations = new();
	private Task? _hostInitialization;
	private Task<OperationResult>? _hostShutdown;
	private Task<OperationResult>? _resourceShutdown;
	private IPersistenceProvider? _persistence;
	private CompiledSchema? _hostSchema;
	private HexHostServicesComponent? _hostServices;
	private HexClientRootComponent? _clientRoot;
	private HexClientController? _clientController;
	private bool _hostFailureLogged;
	private bool _disposeRequested;
	private bool _hostLifetimeDisposed;
	private string _persistenceRoot = string.Empty;

	public HexagonRuntimeSystem( Scene scene ) : base( scene )
	{
		_runtimeScene = scene;
		Listen( Stage.FinishUpdate, 100, PollLifecycle, "Hexagon v2 lifecycle" );
	}

	public HexRuntimeReadiness HostReadiness { get; private set; } = HexRuntimeReadiness.Absent;
	public HexRuntimeReadiness ClientReadiness { get; private set; } = HexRuntimeReadiness.Absent;
	public IHexHostApplication? HostApplication { get; private set; }
	public HexClientStore? ClientStore { get; private set; }
	public IHexClientController? ClientController => _clientController;
	public Task<OperationResult>? HostShutdownCompletion => _hostShutdown ?? _resourceShutdown;
	internal SandboxClientCommandTransport? ClientTransport { get; private set; }

	void ISceneStartup.OnHostInitialize()
	{
		if ( HostReadiness != HexRuntimeReadiness.Absent ) return;
		var bootstrapResult = RequireBootstrap();
		if ( bootstrapResult.Failed )
		{
			FailHost( bootstrapResult.Error!.Message );
			return;
		}
		var bootstrap = bootstrapResult.Value;
		var hostOptions = ResolveHostOptions( bootstrap );
		if ( hostOptions.Failed )
		{
			FailHost( hostOptions.Error!.Message );
			return;
		}

		var collector = new SchemaSourceCollector();
		Scene.RunEvent<IHexSchemaSource>( source => source.CollectSchemas( collector ) );
		var descriptorResult = collector.Require( bootstrap.SchemaId );
		if ( descriptorResult.Failed )
		{
			FailHost( descriptorResult.Error!.Message );
			return;
		}

		var compiled = SchemaCompiler.Compile( descriptorResult.Value.Schema );
		if ( compiled.Failed )
		{
			FailHost( compiled.Error!.Message );
			return;
		}

		var types = new PersistedTypeRegistry().RegisterHexagonDomainTypes();
		var bindings = SchemaPersistenceAdapter.Bind(
			compiled.Value, types, descriptorResult.Value.PersistenceCodecs );
		if ( bindings.Failed )
		{
			FailHost( bindings.Error!.Message );
			return;
		}

		IPersistenceStorage storage;
		try
		{
			storage = descriptorResult.Value.CreatePersistenceStorage()
				?? throw new InvalidOperationException( "The schema persistence storage factory returned null." );
		}
		catch ( Exception exception )
		{
			FailHost( $"Persistence storage construction failed: {exception.Message}" );
			return;
		}
		var logicalRoot = $"hexagon/persistence/v3/{compiled.Value.Id}";
		if ( !string.IsNullOrWhiteSpace( hostOptions.Value.PersistenceRootOverride ) )
		{
			storage = new PrefixedPersistenceStorage( storage, hostOptions.Value.PersistenceRootOverride );
			_persistenceRoot = $"{hostOptions.Value.PersistenceRootOverride}/{logicalRoot}";
		}
		else
		{
			_persistenceRoot = logicalRoot;
		}

		var persistenceInvariants = new DomainInvariantValidator(
			bindings.Value.Types,
			compiled.Value,
			new SchemaItemShapeCatalog( compiled.Value ),
			descriptorResult.Value.PersistenceInvariants );
		// One-shot: consume the quarantine arming for THIS host start regardless of outcome, so a
		// start that does not quarantine (intact store, lease-unavailable, a different root) cannot
		// leave it armed to silently quarantine a later store. Re-arm to quarantine again.
		var quarantineCorruptStore = HexagonRuntimeOverrides.QuarantineCorruptStore;
		if ( quarantineCorruptStore ) HexagonRuntimeOverrides.QuarantineCorruptStore = false;
		_persistence = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( compiled.Value.Id )
			{
				Log = message => Log.Info( message ),
				// Operator-armed one-shot corruption recovery; the arming was consumed at read above.
				QuarantineCorruptStore = quarantineCorruptStore,
				// Threshold checkpoints must not stall the committing command or the main
				// thread; the registry-tracked task is drained by shutdown before the
				// provider's own final checkpoint runs.
				ScheduleBackgroundCheckpoint = work => TryStartHostOperation(
					"persistence:automatic-checkpoint",
					async () =>
					{
						await GameTask.WorkerThread();
						await work();
					} )
			},
			bindings.Value.Types,
			persistenceInvariants );
		HostReadiness = HexRuntimeReadiness.Initializing;
		_hostSchema = compiled.Value;
		var hostServices = CreateHostServices();
		if ( hostServices.Failed )
		{
			FailHost( hostServices.Error!.Message );
			_hostInitialization = ShutdownResourcesOnceAsync();
			return;
		}
		_hostServices = hostServices.Value;
		_hostInitialization = InitializeHostAsync(
			descriptorResult.Value,
			compiled.Value,
			bindings.Value,
			_persistence,
			_hostServices,
			_runtimeScene,
			_persistenceRoot,
			hostOptions.Value.VerificationProbe );
	}

	void ISceneStartup.OnClientInitialize()
	{
		if ( ClientReadiness != HexRuntimeReadiness.Absent ) return;
		var bootstrapResult = RequireBootstrap();
		if ( bootstrapResult.Failed )
		{
			ClientReadiness = HexRuntimeReadiness.Failed;
			Log.Error( $"HEXAGON_CLIENT_FAILED {bootstrapResult.Error!.Message}" );
			ReportClientBootstrapFailure(
				ClientBootstrapDiagnosticPhase.Bootstrap,
				ClientBootstrapDiagnosticCode.BootstrapUnavailable,
				bootstrapResult.Error.Message );
			return;
		}

		var collector = new SchemaSourceCollector();
		Scene.RunEvent<IHexSchemaSource>( source => source.CollectSchemas( collector ) );
		var descriptor = collector.Require( bootstrapResult.Value.SchemaId );
		if ( descriptor.Failed )
		{
			ClientReadiness = HexRuntimeReadiness.Failed;
			Log.Error( $"HEXAGON_CLIENT_FAILED {descriptor.Error!.Message}" );
			ReportClientBootstrapFailure(
				ClientBootstrapDiagnosticPhase.SchemaDiscovery,
				ClientBootstrapDiagnosticCode.SchemaRuntimeUnavailable,
				descriptor.Error.Message );
			return;
		}

		var clientObject = new GameObject( true, "Hexagon v2 Client" );
		_clientRoot = clientObject.AddComponent<HexClientRootComponent>();
		ClientStore = new HexClientStore
		{
			SubscriberFailureDiagnostic = exception => Log.Warning(
				exception, "HEXAGON_CLIENT_SUBSCRIBER_FAILED store change subscriber threw." )
		};
		var clientNonce = ClientSessionNonce.New();
		ClientStore.PrepareSession( clientNonce );
		ClientTransport = new SandboxClientCommandTransport( Scene, ClientStore, clientNonce );
		_clientController = new HexClientController( ClientTransport );
		_clientRoot.Store = ClientStore;
		_clientRoot.Controller = _clientController;
		try
		{
			descriptor.Value.ConfigureClient?.Invoke(
				new HexClientRuntimeContext( Scene, ClientStore, _clientController ) );
		}
		catch ( Exception exception )
		{
			_clientController.Dispose();
			ClientStore.ClearSession();
			ClientReadiness = HexRuntimeReadiness.Failed;
			Log.Error( exception, "HEXAGON_CLIENT_FAILED schema client configuration threw." );
			ReportClientBootstrapFailure(
				ClientBootstrapDiagnosticPhase.ClientConfiguration,
				ClientBootstrapDiagnosticCode.ClientConfigurationFailed,
				$"{exception.GetType().Name}: {exception.Message}" );
			return;
		}

		ClientReadiness = HexRuntimeReadiness.Ready;
		ClientTransport.PollSession( Stopwatch.GetTimestamp() );
		Log.Info( "HEXAGON_READY client" );
	}

	void Component.INetworkListener.OnActive( Connection connection )
	{
		// Project settings establish the default before admission; reassert the
		// fail-closed policy per remote connection before any identity shell exists.
		if ( !connection.IsHost )
		{
			connection.CanSpawnObjects = false;
			connection.CanRefreshObjects = false;
			connection.CanDestroyObjects = false;
		}
		if ( _sessions.ContainsKey( connection.Id ) ) return;
		// Mounted map packages ship their own SpawnPoints (flatgrass carries a whole
		// roof-top grid of them, and map spawns can carry the generic "spawn" tag too);
		// schema-authored spawns use the namespaced "hexagon_spawn" tag and must win or
		// players are distributed onto arbitrary map geometry.
		var allSpawns = _runtimeScene.GetAll<SpawnPoint>()
			.Where( candidate => candidate.Active )
			.ToArray();
		var schemaSpawns = allSpawns
			.Where( candidate => candidate.GameObject.Tags.Has( "hexagon_spawn" ) )
			.ToArray();
		var spawns = (schemaSpawns.Length > 0 ? schemaSpawns : allSpawns)
			.OrderBy( candidate => candidate.GameObject.Id )
			.ToArray();
		if ( spawns.Length == 0 )
		{
			Log.Error( $"HEXAGON_PLAYER_SPAWN_FAILED connection={connection.Id} reason=no_spawn_point" );
			return;
		}

		var slot = _spawnSlots.Acquire( connection.Id );
		GameObject? playerObject = null;
		RuntimePlayerSession<HexPlayerBody>? newSession = null;
		try
		{
			var placement = SpawnSlotAllocator.Describe( slot, spawns.Length );
			var spawn = spawns[placement.SpawnPointIndex];
			var position = spawn.WorldPosition;
			if ( placement.Ring > 0 )
			{
				var localOffset = new Vector3(
					(float)(Math.Cos( placement.AngleRadians ) * placement.Radius),
					(float)(Math.Sin( placement.AngleRadians ) * placement.Radius),
					0.0f );
				position += spawn.WorldRotation * localOffset;
			}

			playerObject = new GameObject( true, $"Hexagon Player - {connection.DisplayName}" );
			playerObject.WorldTransform = spawn.WorldTransform.WithPosition( position ).WithScale( 1 );
			var player = playerObject.AddComponent<HexPlayerBody>();
			player.HostSetConnection( connection );
			if ( !playerObject.NetworkSpawn( connection ) )
			{
				playerObject.Destroy();
				_spawnSlots.Release( connection.Id );
				Log.Error( $"HEXAGON_PLAYER_SPAWN_FAILED connection={connection.Id} reason=network_spawn_rejected" );
				return;
			}

			newSession = new RuntimePlayerSession<HexPlayerBody>( player, exception =>
				Log.Error( exception, $"Hexagon command cancellation callback failed for connection '{connection.Id}'." ) );
			_sessions.Add( connection.Id, newSession );
			Log.Info( FormattableString.Invariant(
				$"HEXAGON_PLAYER_SPAWNED connection={connection.Id} slot={slot} position={position.x:0.###},{position.y:0.###},{position.z:0.###}" ) );
			if ( HostReadiness == HexRuntimeReadiness.Ready ) _ = newSession.ObserveHostReady();
			// Application-level connection is delayed until the authenticated caller
			// proves possession of its freshly generated client nonce.
		}
		catch ( Exception exception )
		{
			if ( _sessions.Remove( connection.Id, out var registeredSession ) ) registeredSession.Dispose();
			else newSession?.Dispose();
			if ( playerObject is not null && playerObject.IsValid() ) playerObject.Destroy();
			_spawnSlots.Release( connection.Id );
			Log.Error( exception,
				$"HEXAGON_PLAYER_SPAWN_FAILED connection={connection.Id} reason=spawn_exception" );
		}
	}

	void Component.INetworkListener.OnDisconnected( Connection connection )
	{
		_spawnSlots.Release( connection.Id );
		if ( !_sessions.Remove( connection.Id, out var session ) ) return;
		var actor = session.IsApplicationConnected ? BuildActor( connection, session, false ) : (RpcActor?)null;
		session.Disconnect();
		try
		{
			if ( actor is not null ) HostApplication?.Disconnected( actor.Value );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon disconnect cleanup failed for connection '{connection.Id}'." );
		}
		finally
		{
			try
			{
				// Connection-owned network objects can already be gone by the time the
				// listener is notified. Component.DestroyGameObject() deliberately
				// tolerates that teardown ordering instead of dereferencing a null
				// GameObject during disconnect cleanup.
				session.Player.DestroyGameObject();
			}
			finally
			{
				session.Dispose();
			}
		}
	}

	void Component.INetworkListener.OnBecameHost( Connection previousHost )
	{
		// Hexagon's single-writer persistence model cannot survive a host change: a
		// migrated-to client has no host application, no domain services, and no
		// persistence lease, so accepting hostship would keep an unpersisted zombie
		// session alive under an unprepared, unsandboxed machine's authority. The
		// project networking settings disable migration; this is defense in depth for
		// any composition that re-enables it.
		Log.Error(
			$"HEXAGON_HOST_MIGRATION_REFUSED previous_host={previousHost?.Id.ToString() ?? "unknown"} " +
			"detail=\"Hexagon sessions cannot outlive their prepared host; disconnecting.\"" );
		Sandbox.Networking.Disconnect();
	}

	public bool TryGetPlayer( Guid connectionId, out HexPlayerBody player )
	{
		if ( _sessions.TryGetValue( connectionId, out var session ) )
		{
			player = session.Player;
			return true;
		}
		player = null!;
		return false;
	}

	internal OperationResult<RpcActor> ResolveActor(
		Connection connection,
		ClientSessionScope scope,
		bool requiresStableCharacter )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) ||
			!session.IsApplicationConnected || !session.IsCurrent( scope ) )
			return OperationResult<RpcActor>.Failure( ErrorCode.Unauthorized, "RPC caller has no active host connection binding." );
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( connection.Id ) );
		var lease = session.Boundary.Capture( character?.Id, requiresStableCharacter );
		return OperationResult<RpcActor>.Success( new RpcActor(
			connection,
			new AccountId( connection.SteamId.ValueUnsigned ),
			session.Player,
			character,
			scope,
			lease ) );
	}

	internal CommandAdmissionResult TryBeginCommand( Connection connection, CommandRequestId requestId, int cost ) =>
		_sessions.TryGetValue( connection.Id, out var session )
			? session.TryBeginRequest( requestId, cost )
			: CommandAdmissionResult.Reject( CommandAdmissionFailure.Disconnected );

	internal ClientBootstrapDiagnosticAdmissionResult TryAcceptClientBootstrapDiagnostic(
		Connection connection,
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code,
		string? detail ) => _sessions.TryGetValue( connection.Id, out var session )
		? session.TryAcceptBootstrapDiagnostic( phase, code, detail )
		: new ClientBootstrapDiagnosticAdmissionResult(
			false,
			ClientBootstrapDiagnosticAdmissionFailure.Disconnected,
			default );

	internal bool FinishRejectedCommand( Connection connection, CommandRequestId requestId ) =>
		_sessions.TryGetValue( connection.Id, out var session ) && session.FinishRequest( requestId );

	internal OperationResult<ClientSessionScope> BindClientSession(
		Connection connection,
		ClientSessionNonce nonce )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) )
			return OperationResult<ClientSessionScope>.Failure(
				ErrorCode.Conflict, "The host connection session is not ready." );
		if ( !session.TryBind( nonce, out var scope, out _ ) )
			return OperationResult<ClientSessionScope>.Failure(
				ErrorCode.Unauthorized, "The client session nonce cannot be bound to this connection." );
		return OperationResult<ClientSessionScope>.Success( scope );
	}

	internal OperationResult CompleteClientSessionHello(
		Connection connection,
		ClientSessionScope scope )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) || !session.IsCurrent( scope ) )
			return OperationResult.Failure(
				ErrorCode.Unauthorized, "The client session changed before its hello was issued." );
		if ( session.ObserveHelloIssued() && !NotifyConnected( BuildActor( connection, session, false ), session ) )
			return OperationResult.Failure(
				ErrorCode.InternalError, "The host application rejected connection initialization." );
		return OperationResult.Success();
	}

	internal OperationResult<ClientSessionScope> CaptureClientScope( Connection connection )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) || session.Scope is null )
			return OperationResult<ClientSessionScope>.Failure( ErrorCode.Unauthorized, "Client session is not established." );
		return OperationResult<ClientSessionScope>.Success( session.Scope.Value );
	}

	internal bool IsCommandLeaseCurrent( RpcActor actor )
	{
		if ( !_sessions.TryGetValue( actor.Connection.Id, out var session ) ||
			session.Boundary.ConnectionEpoch != actor.ConnectionEpoch )
			return false;
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( actor.Connection.Id ) );
		session.Boundary.ObserveCharacter( character?.Id );
		return session.Boundary.IsCurrent( actor.Session );
	}

	internal CommandCompletionStatus CompleteCommand( RpcActor actor, CommandRequestId requestId )
	{
		if ( !_sessions.TryGetValue( actor.Connection.Id, out var session ) ||
			session.Boundary.ConnectionEpoch != actor.ConnectionEpoch ||
			!session.FinishRequest( requestId ) )
			return CommandCompletionStatus.Disconnected;
		return session.Boundary.IsCurrent( actor.Session )
			? CommandCompletionStatus.Current
			: CommandCompletionStatus.Stale;
	}

	internal OperationResult<ClientStateEpoch> PublishClientStateEpoch(
		Connection connection,
		CharacterId? characterId )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) || !session.IsConnected )
			return OperationResult<ClientStateEpoch>.Failure( ErrorCode.Unauthorized, "Client state recipient is disconnected." );
		return OperationResult<ClientStateEpoch>.Success( session.Boundary.Publish( characterId ) );
	}

	internal OperationResult<ChatDeliveryEpoch> CaptureChatDeliveryEpoch( Connection connection )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) || !session.IsConnected )
			return OperationResult<ChatDeliveryEpoch>.Failure(
				ErrorCode.Unauthorized, "Chat recipient is disconnected." );
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( connection.Id ) );
		session.Boundary.ObserveCharacter( character?.Id );
		return OperationResult<ChatDeliveryEpoch>.Success( new ChatDeliveryEpoch(
			session.Boundary.ConnectionEpoch,
			session.Boundary.CharacterEpoch ) );
	}

	internal bool IsRegisteredPanel( string panelId ) =>
		_hostSchema?.Panels.Contains( panelId ) == true;

	internal int GetSchemaCommandCost( string commandId )
	{
		return SchemaCommandAdmissionCost.Resolve( commandId, registeredId =>
			_hostSchema?.Commands.TryGet( registeredId, out var definition ) == true && definition is not null
				? (int)definition.Cost
				: null );
	}

	internal bool TryStartHostOperation( string name, Func<Task> operation ) =>
		_hostOperations.TryStartTask( name, operation );

	/// <summary>
	/// Explicit awaitable barrier for game-owned scene transition code. s&amp;box's
	/// synchronous GameObjectSystem.Dispose cannot itself await this result.
	/// </summary>
	public ValueTask<OperationResult> ShutdownAsync()
	{
		RequestShutdown();
		return new ValueTask<OperationResult>( BeginCoordinatedShutdown() );
	}

	public override void Dispose()
	{
		RequestShutdown();
		if ( _persistence is not null || HostApplication is not null || _hostInitialization is not null )
			_ = BeginCoordinatedShutdown();
		else
			DisposeHostLifetime();

		_clientController?.Dispose();
		ClientTransport = null;
		ClientStore?.ClearSession();
		ClientReadiness = HexRuntimeReadiness.Disposed;
		foreach ( var session in _sessions.Values ) session.Dispose();
		_sessions.Clear();
		_spawnSlots.Clear();
		base.Dispose();
	}

	private async Task InitializeHostAsync(
		HexSchemaRuntimeDescriptor descriptor,
		CompiledSchema schema,
		SchemaPersistenceBindings persistenceBindings,
		IPersistenceProvider persistence,
		HexHostServicesComponent services,
		Scene scene,
		string persistenceRoot,
		string verificationProbe )
	{
		// Wait for a previous owner of this persistence root (an in-process scene replacement) to
		// finish releasing its lease, then ALWAYS proceed. The predecessor's drain outcome conflates
		// benign engine-teardown faults with real ones, so it is never an authority on whether the
		// store may be opened — the exclusive lease and WAL recovery below decide that. The wait is
		// time-boxed so a stranded drain degrades to a precise lease failure, never an unbounded hang.
		var previousDrain = SceneShutdownBarrier.Previous( persistenceRoot );
		if ( previousDrain is not null )
		{
			if ( await AwaitPredecessorSettlementAsync( previousDrain ) )
			{
				var previousOutcome = await AsyncOperation.Capture(
					() => new ValueTask<OperationResult>( previousDrain ) );
				var verdict = SceneHandoffPolicy.EvaluatePredecessor( previousOutcome );
				if ( verdict.ShouldWarn )
					Log.Warning(
						$"HEXAGON_HANDOFF_DEGRADED root='{persistenceRoot}' state={verdict.State} " +
						$"detail={verdict.Diagnostic}; deferring ownership and integrity to the lease and recovery." );
			}
			else if ( !_disposeRequested )
			{
				Log.Warning(
					$"HEXAGON_HANDOFF_TIMEOUT root='{persistenceRoot}' the previous owner's drain did not settle " +
					$"within {HandoffWaitBudget.TotalSeconds:0}s; proceeding under exclusive-lease protection." );
			}
			SceneShutdownBarrier.Clear( persistenceRoot, previousDrain );
		}

		if ( _disposeRequested )
		{
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		var recovered = await AsyncOperation.Capture(
			() => persistence.InitializeAsync( _hostLifetime.Token ) );
		if ( !recovered.Succeeded )
		{
			FailHost( $"Persistence recovery failed: {recovered.Exception!.Message}" );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		var health = persistence.Health;
		Log.Info(
			$"HEXAGON_RECOVERED schema={schema.Id} sequence={health.Sequence} checkpoint={health.CheckpointSequence} " +
			$"orphan_frames_discarded={health.DiscardedUnacknowledgedFrames} checkpoint_fallback={health.RecoveredFromCheckpointFallback} " +
			$"quarantined={health.RecoveredByQuarantine} root={persistenceRoot}" );
		if ( health.RecoveredByQuarantine )
			Log.Warning(
				$"HEXAGON_PERSISTENCE_QUARANTINE_CONSUMED root={persistenceRoot} quarantine={health.QuarantinePath}; " +
				$"the corrupt store was archived and a fresh store was opened. Re-arm hexagon-persistence-quarantine to quarantine again." );
		if ( _disposeRequested )
		{
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		var repositories = new DomainRepositories( persistence );
		var invariants = new DomainInvariantValidator(
			repositories,
			schema,
			new SchemaItemShapeCatalog( schema, repositories ),
			descriptor.PersistenceInvariants ).Validate();
		if ( !invariants.IsValid )
		{
			var first = invariants.Issues[0];
			FailHost( $"Persistence invariant failed at '{first.Path}': {first.Message}" );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}
		var configuration = new TypedConfigurationStore( persistence, persistenceBindings.PersistenceConfigs );
		var initializedConfiguration = await AsyncOperation.Capture(
			() => configuration.InitializeAsync( _hostLifetime.Token ) );
		if ( !initializedConfiguration.Succeeded )
		{
			FailHost( $"Typed configuration startup failed: {initializedConfiguration.Exception!.Message}" );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}
		if ( initializedConfiguration.Value.Failed )
		{
			FailHost( $"Typed configuration startup failed: {initializedConfiguration.Value.Error!.Message}" );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}
		var context = new HexHostRuntimeContext(
			scene,
			schema,
			persistence,
			repositories,
			configuration,
			services,
			persistenceRoot,
			verificationProbe );
		var created = await AsyncOperation.Capture(
			() => ValueTask.FromResult( descriptor.CreateHostApplication( context ) ) );
		if ( !created.Succeeded || created.Value is null )
		{
			FailHost( $"Host application construction failed: {created.Exception?.Message ?? "no application was returned"}." );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		HostApplication = created.Value;
		var initialized = await AsyncOperation.Capture(
			() => HostApplication.InitializeAsync( _hostLifetime.Token ) );
		if ( !initialized.Succeeded )
		{
			FailHost( $"Host application initialization threw: {initialized.Exception!.Message}" );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}
		if ( initialized.Value.Failed )
		{
			FailHost( initialized.Value.Error!.Message );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}
		if ( _disposeRequested )
		{
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		HostReadiness = HexRuntimeReadiness.Ready;
		foreach ( var session in _sessions.Values )
		{
			if ( session.ObserveHostReady() && session.Player.HostConnection is not null )
				_ = NotifyConnected( BuildActor( session.Player.HostConnection, session, false ), session );
		}
		Log.Info( $"HEXAGON_READY host schema={schema.Id} sequence={persistence.Health.Sequence} root={persistenceRoot}" );
	}

	private static readonly TimeSpan HandoffWaitBudget = TimeSpan.FromSeconds( 10 );

	/// <summary>
	/// Waits for a previous owner of the same persistence root to finish draining, bounded so a
	/// stranded drain cannot hang host initialization forever. Returns true if the predecessor
	/// settled within the budget, false on timeout or host disposal. Built only from async
	/// primitives proven under the s&amp;box whitelist elsewhere in this assembly — a
	/// CancellationTokenSource timer, token registrations, a TaskCompletionSource, and
	/// AsyncOperation.Capture — with no Task.Delay/WhenAny/WaitAsync.
	/// </summary>
	private async ValueTask<bool> AwaitPredecessorSettlementAsync( Task<OperationResult> previousDrain )
	{
		if ( previousDrain.IsCompleted ) return true;
		var settlement = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
		using var timeout = new CancellationTokenSource();
		using var timeoutRegistration = timeout.Token.Register(
			static state => ((TaskCompletionSource<bool>)state!).TrySetResult( false ), settlement );
		using var lifetimeRegistration = _hostLifetime.Token.Register(
			static state => ((TaskCompletionSource<bool>)state!).TrySetResult( false ), settlement );
		_ = previousDrain.ContinueWith( _ => settlement.TrySetResult( true ) );
		timeout.CancelAfter( HandoffWaitBudget );
		var settled = await AsyncOperation.Capture( () => new ValueTask<bool>( settlement.Task ) );
		return settled.Succeeded && settled.Value;
	}

	private void RequestShutdown()
	{
		if ( _disposeRequested ) return;
		_disposeRequested = true;
		_hostOperations.StopAdmission();
		if ( !_hostLifetimeDisposed )
		{
			try
			{
				_hostLifetime.Cancel();
			}
			catch ( Exception exception )
			{
				Log.Error( exception, "Hexagon host lifetime cancellation callback failed." );
			}
		}
		if ( HostReadiness is not (HexRuntimeReadiness.Absent or HexRuntimeReadiness.Disposed) )
			HostReadiness = HexRuntimeReadiness.Disposing;
	}

	private Task<OperationResult> BeginCoordinatedShutdown()
	{
		if ( _hostShutdown is not null ) return _hostShutdown;
		_hostShutdown = CoordinateShutdownAsync( _hostInitialization );
		if ( !string.IsNullOrWhiteSpace( _persistenceRoot ) )
			SceneShutdownBarrier.Publish( _persistenceRoot, _hostShutdown );
		_ = _hostShutdown.ContinueWith( static task =>
		{
			if ( task.IsFaulted )
				Log.Error( $"HEXAGON_DRAIN_FAILED {task.Exception?.GetBaseException().Message}" );
		} );
		return _hostShutdown;
	}

	private async Task<OperationResult> CoordinateShutdownAsync( Task? initialization )
	{
		if ( initialization is not null )
			_ = await AsyncOperation.Capture( () => new ValueTask( initialization ) );
		return await ShutdownResourcesOnceAsync();
	}

	private Task<OperationResult> ShutdownResourcesOnceAsync() =>
		_resourceShutdown ??= ShutdownHostAsync();

	private async Task<OperationResult> ShutdownHostAsync()
	{
		Exception? firstFailure = null;
		PersistenceShutdownResult? persistenceShutdown = null;
		var application = HostApplication;
		var disconnects = new List<(RuntimePlayerSession<HexPlayerBody> Session, RpcActor? Actor)>();
		foreach ( var session in _sessions.Values )
		{
			RpcActor? actor = null;
			if ( session.Player.HostConnection is Connection connection && session.Scope is not null )
			{
				try { actor = BuildActor( connection, session, false ); }
				catch ( Exception exception )
				{
					firstFailure ??= exception;
					Log.Error( exception, $"Could not capture shutdown actor for '{connection.Id}'." );
				}
			}
			disconnects.Add( (session, actor) );
		}

		foreach ( var disconnect in disconnects )
		{
			disconnect.Session.Disconnect();
			if ( application is not null && disconnect.Actor is RpcActor actor )
			{
				try { application.Disconnected( actor ); }
				catch ( Exception exception )
				{
					firstFailure ??= exception;
					Log.Error( exception, $"Host application disconnect failed for '{actor.Connection.Id}'." );
				}
			}
		}

		var commandDrain = await _hostOperations.DrainAsync();
		foreach ( var failure in commandDrain.Failures )
		{
			if ( !failure.WasCanceled ) firstFailure ??= failure.Exception;
			Log.Warning(
				$"HEXAGON_HOST_OPERATION_FAILED id={failure.OperationId} name={failure.Name} " +
				$"canceled={failure.WasCanceled} message={failure.Exception.Message}" );
		}

		var hostServices = _hostServices;
		_hostServices = null;
		if ( hostServices is not null )
		{
			try
			{
				hostServices.Runtime = null;
				if ( hostServices.GameObject.IsValid() ) hostServices.GameObject.Destroy();
			}
			catch ( Exception exception ) { firstFailure ??= exception; }
		}

		HostApplication = null;
		if ( application is not null )
		{
			var disposedApplication = await AsyncOperation.Capture( application.DisposeAsync );
			if ( !disposedApplication.Succeeded ) firstFailure ??= disposedApplication.Exception;
		}

		var persistence = _persistence;
		_persistence = null;
		_hostSchema = null;
		if ( persistence is not null )
		{
			var drained = await AsyncOperation.Capture( () => persistence.ShutdownAsync() );
			if ( !drained.Succeeded ) firstFailure ??= drained.Exception;
			else
			{
				var shutdown = drained.Value!;
				persistenceShutdown = shutdown;
				if ( !shutdown.IsRecoverable )
					firstFailure ??= new InvalidOperationException(
						shutdown.Detail ?? "Persistence shutdown is not recoverable." );
			}
			var disposedPersistence = await AsyncOperation.Capture( persistence.DisposeAsync );
			if ( !disposedPersistence.Succeeded ) firstFailure ??= disposedPersistence.Exception;
		}

		try { DisposeHostLifetime(); }
		catch ( Exception exception ) { firstFailure ??= exception; }

		if ( firstFailure is null && application is not null && persistenceShutdown?.IsClean == true )
		{
			try
			{
				var finalized = application.CompleteQuiescedShutdown( persistenceShutdown );
				if ( finalized.Failed )
					firstFailure = new InvalidOperationException( finalized.Error!.Message );
			}
			catch ( Exception exception ) { firstFailure = exception; }
		}

		if ( firstFailure is not null )
		{
			HostReadiness = HexRuntimeReadiness.Failed;
			Log.Error( firstFailure, "HEXAGON_DRAIN_FAILED host resources did not close cleanly." );
			return OperationResult.Failure( ErrorCode.InternalError, "Host resources did not close cleanly." );
		}

		if ( HostReadiness != HexRuntimeReadiness.Failed ) HostReadiness = HexRuntimeReadiness.Disposed;
		if ( persistenceShutdown is not null && persistenceShutdown.IsClean )
		{
			Log.Info( "HEXAGON_DRAINED host" );
		}
		else if ( persistenceShutdown is not null )
		{
			Log.Warning(
				$"HEXAGON_DRAIN_DEGRADED durable_sequence={persistenceShutdown.DurableSequence} " +
				$"checkpoint_sequence={persistenceShutdown.CheckpointSequence} " +
				$"lease_released={persistenceShutdown.LeaseReleased} detail={persistenceShutdown.Detail}" );
		}
		else
		{
			Log.Warning( "HEXAGON_DRAIN_DEGRADED persistence_shutdown=unavailable" );
		}
		return OperationResult.Success();
	}

	private OperationResult<HexHostServicesComponent> CreateHostServices()
	{
		GameObject? servicesObject = null;
		try
		{
			servicesObject = new GameObject( true, "Hexagon v2 Host Services" );
			var services = servicesObject.AddComponent<HexHostServicesComponent>();
			services.Runtime = this;
			return HostServicePublication.RequirePublished(
				services,
				servicesObject.NetworkSpawn,
				() =>
				{
					services.Runtime = null;
					if ( servicesObject.IsValid() ) servicesObject.Destroy();
				} );
		}
		catch ( Exception exception )
		{
			try { if ( servicesObject is not null && servicesObject.IsValid() ) servicesObject.Destroy(); }
			catch ( Exception ) { }
			return OperationResult<HexHostServicesComponent>.Failure(
				ErrorCode.InternalError,
				$"Hexagon host services could not be created: {exception.Message}" );
		}
	}

	private void ReportClientBootstrapFailure(
		ClientBootstrapDiagnosticPhase phase,
		ClientBootstrapDiagnosticCode code,
		string detail )
	{
		try
		{
			var services = _runtimeScene.GetAll<HexHostServicesComponent>().FirstOrDefault();
			if ( services is null ) return;
			services.ReportClientBootstrapDiagnostic(
				phase,
				code,
				ClientBootstrapDiagnosticContract.PrepareDetail( code, detail ) );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, "Hexagon could not report the bounded client bootstrap diagnostic to the host." );
		}
	}

	private OperationResult<HexagonBootstrapComponent> RequireBootstrap()
	{
		var bootstraps = Scene.GetAll<HexagonBootstrapComponent>().ToArray();
		if ( bootstraps.Length != 1 )
			return OperationResult<HexagonBootstrapComponent>.Failure(
				ErrorCode.SchemaInvalid, $"Expected exactly one Hexagon bootstrap, found {bootstraps.Length}." );
		if ( string.IsNullOrWhiteSpace( bootstraps[0].SchemaId ) )
			return OperationResult<HexagonBootstrapComponent>.Failure(
				ErrorCode.SchemaInvalid, "Hexagon bootstrap schema ID is blank." );
		try
		{
			if ( !string.IsNullOrWhiteSpace( bootstraps[0].PersistenceRootOverride ) )
				_ = PrefixedPersistenceStorage.Normalize( bootstraps[0].PersistenceRootOverride );
		}
		catch ( Exception exception )
		{
			return OperationResult<HexagonBootstrapComponent>.Failure( ErrorCode.ConfigurationInvalid, exception.Message );
		}
		return OperationResult<HexagonBootstrapComponent>.Success( bootstraps[0] );
	}

	private static OperationResult<HostBootstrapOptions> ResolveHostOptions( HexagonBootstrapComponent bootstrap ) =>
		HostBootstrapResolution.Resolve(
			HexagonRuntimeOverrides.PersistenceRoot,
			bootstrap.PersistenceRootOverride,
			HexagonRuntimeOverrides.VerificationProbe,
			bootstrap.VerificationProbe,
			PrefixedPersistenceStorage.Normalize );

	private RpcActor BuildActor( Connection connection, RuntimePlayerSession<HexPlayerBody> session, bool requiresStableCharacter )
	{
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( connection.Id ) );
		return new RpcActor(
			connection,
			new AccountId( connection.SteamId.ValueUnsigned ),
			session.Player,
			character,
			session.Scope ?? throw new InvalidOperationException( "Client session is not established." ),
			session.Boundary.Capture( character?.Id, requiresStableCharacter ) );
	}

	private bool NotifyConnected( RpcActor actor, RuntimePlayerSession<HexPlayerBody> session )
	{
		var succeeded = false;
		try
		{
			var application = HostApplication ??
				throw new InvalidOperationException( "The host application is not initialized." );
			application.Connected( actor );
			succeeded = true;
			Log.Info(
				$"HEXAGON_SESSION_BOUND account={actor.AccountId.Value} connection={actor.Connection.Id} " +
				$"epoch={actor.ClientScope.Connection}" );
			return true;
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon connection initialization failed for '{actor.Connection.Id}'." );
			return false;
		}
		finally
		{
			if ( !session.CompleteApplicationConnection( succeeded ) || !succeeded )
				session.Disconnect();
		}
	}

	private void PollLifecycle()
	{
		if ( ClientReadiness == HexRuntimeReadiness.Ready )
			ClientTransport?.PollSession( Stopwatch.GetTimestamp() );
		if ( _hostInitialization?.IsFaulted == true && !_hostFailureLogged )
		{
			_hostFailureLogged = true;
			FailHost( _hostInitialization.Exception?.GetBaseException().Message ?? "Host initialization faulted." );
			_ = BeginCoordinatedShutdown();
		}
	}

	private void FailHost( string message )
	{
		HostReadiness = HexRuntimeReadiness.Failed;
		Log.Error( $"HEXAGON_HOST_FAILED {message}" );
	}

	private void DisposeHostLifetime()
	{
		if ( _hostLifetimeDisposed ) return;
		_hostLifetimeDisposed = true;
		_hostLifetime.Dispose();
	}

}
