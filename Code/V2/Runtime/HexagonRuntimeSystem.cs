#nullable enable

using System;
using System.Collections.Generic;
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

internal enum CommandCompletionStatus
{
	Current = 0,
	Stale = 1,
	Disconnected = 2
}

/// <summary>
/// Scene-scoped host/client composition root. Listen servers receive independent
/// scopes, while each host connection owns an ephemeral command and character epoch.
/// </summary>
public sealed class HexagonRuntimeSystem : GameObjectSystem<HexagonRuntimeSystem>, ISceneStartup, Component.INetworkListener
{
	private readonly Scene _runtimeScene;
	private readonly Dictionary<Guid, RuntimePlayerSession> _sessions = new();
	private readonly CancellationTokenSource _hostLifetime = new();
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

		IPersistenceStorage storage = new SandboxPersistenceStorage();
		var logicalRoot = $"hexagon/v2/{compiled.Value.Id}";
		if ( !string.IsNullOrWhiteSpace( hostOptions.Value.PersistenceRootOverride ) )
		{
			storage = new PrefixedPersistenceStorage( storage, hostOptions.Value.PersistenceRootOverride );
			_persistenceRoot = $"{hostOptions.Value.PersistenceRootOverride}/{logicalRoot}";
		}
		else
		{
			_persistenceRoot = logicalRoot;
		}

		_persistence = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( compiled.Value.Id ),
			bindings.Value.Types );
		_hostSchema = compiled.Value;
		_hostServices = CreateHostServices();
		HostReadiness = HexRuntimeReadiness.Initializing;
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
			return;
		}

		var collector = new SchemaSourceCollector();
		Scene.RunEvent<IHexSchemaSource>( source => source.CollectSchemas( collector ) );
		var descriptor = collector.Require( bootstrapResult.Value.SchemaId );
		if ( descriptor.Failed )
		{
			ClientReadiness = HexRuntimeReadiness.Failed;
			Log.Error( $"HEXAGON_CLIENT_FAILED {descriptor.Error!.Message}" );
			return;
		}

		var clientObject = new GameObject( true, "Hexagon v2 Client" );
		_clientRoot = clientObject.AddComponent<HexClientRootComponent>();
		ClientStore = new HexClientStore();
		ClientStore.BeginSession();
		ClientTransport = new SandboxClientCommandTransport( Scene );
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
			return;
		}

		ClientReadiness = HexRuntimeReadiness.Ready;
		Log.Info( "HEXAGON_READY client" );
	}

	void Component.INetworkListener.OnActive( Connection connection )
	{
		if ( _sessions.ContainsKey( connection.Id ) ) return;
		var spawn = _runtimeScene.GetAll<SpawnPoint>()
			.OrderBy( candidate => candidate.GameObject.Id )
			.FirstOrDefault();
		if ( spawn is null )
		{
			Log.Error( $"HEXAGON_PLAYER_SPAWN_FAILED connection={connection.Id} reason=no_spawn_point" );
			return;
		}
		var playerObject = new GameObject( true, $"Hexagon Player - {connection.DisplayName}" );
		playerObject.WorldTransform = spawn.WorldTransform.WithScale( 1 );
		var player = playerObject.AddComponent<HexPlayerBody>();
		player.HostSetConnection( connection );
		playerObject.NetworkSpawn( connection );
		var session = new RuntimePlayerSession( player, exception =>
			Log.Error( exception, $"Hexagon command cancellation callback failed for connection '{connection.Id}'." ) );
		_sessions.Add( connection.Id, session );
		if ( HostReadiness == HexRuntimeReadiness.Ready && HostApplication is not null )
			NotifyConnected( BuildActor( connection, session, false ) );
	}

	void Component.INetworkListener.OnDisconnected( Connection connection )
	{
		if ( !_sessions.Remove( connection.Id, out var session ) ) return;
		var actor = BuildActor( connection, session, false );
		session.Disconnect();
		try
		{
			HostApplication?.Disconnected( actor );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon disconnect cleanup failed for connection '{connection.Id}'." );
		}
		finally
		{
			try
			{
				session.Player.GameObject.Destroy();
			}
			finally
			{
				session.Dispose();
			}
		}
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

	internal OperationResult<RpcActor> ResolveActor( Connection connection, bool requiresStableCharacter )
	{
		if ( !_sessions.TryGetValue( connection.Id, out var session ) || !session.IsConnected )
			return OperationResult<RpcActor>.Failure( ErrorCode.Unauthorized, "RPC caller has no active host connection binding." );
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( connection.Id ) );
		var lease = session.Boundary.Capture( character?.Id, requiresStableCharacter );
		return OperationResult<RpcActor>.Success( new RpcActor(
			connection,
			new AccountId( connection.SteamId.ValueUnsigned ),
			session.Player,
			character,
			lease ) );
	}

	internal bool TryBeginCommand( RpcActor actor, CommandRequestId requestId ) =>
		_sessions.TryGetValue( actor.Connection.Id, out var session ) &&
		session.Boundary.ConnectionEpoch == actor.ConnectionEpoch &&
		session.TryBeginRequest( requestId );

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
		var previousDrain = SceneShutdownBarrier.Previous( persistenceRoot );
		if ( previousDrain is not null )
		{
			var previousOutcome = await RuntimeAsyncOperation.Capture(
				() => new ValueTask<OperationResult>( previousDrain ) );
			SceneShutdownBarrier.Clear( persistenceRoot, previousDrain );
			if ( !previousOutcome.Succeeded || previousOutcome.Value.Failed )
			{
				var detail = previousOutcome.Exception?.Message ?? previousOutcome.Value.Error!.Message;
				FailHost( $"Previous owner of persistence root '{persistenceRoot}' did not drain cleanly: {detail}" );
				_ = await ShutdownResourcesOnceAsync();
				return;
			}
		}

		if ( _disposeRequested )
		{
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		var recovered = await RuntimeAsyncOperation.Capture(
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
			$"wal_tail_repaired={health.RepairedPartialWalTail} checkpoint_fallback={health.RecoveredFromCheckpointFallback} root={persistenceRoot}" );
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
		var initializedConfiguration = await RuntimeAsyncOperation.Capture(
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
		var created = await RuntimeAsyncOperation.Capture(
			() => ValueTask.FromResult( descriptor.CreateHostApplication( context ) ) );
		if ( !created.Succeeded || created.Value is null )
		{
			FailHost( $"Host application construction failed: {created.Exception?.Message ?? "no application was returned"}." );
			_ = await ShutdownResourcesOnceAsync();
			return;
		}

		HostApplication = created.Value;
		var initialized = await RuntimeAsyncOperation.Capture(
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

		foreach ( var session in _sessions.Values )
		{
			if ( session.Player.HostConnection is not null )
				NotifyConnected( BuildActor( session.Player.HostConnection, session, false ) );
		}
		HostReadiness = HexRuntimeReadiness.Ready;
		Log.Info( $"HEXAGON_READY host schema={schema.Id} sequence={persistence.Health.Sequence} root={persistenceRoot}" );
	}

	private void RequestShutdown()
	{
		if ( _disposeRequested ) return;
		_disposeRequested = true;
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
			_ = await RuntimeAsyncOperation.Capture( () => new ValueTask( initialization ) );
		return await ShutdownResourcesOnceAsync();
	}

	private Task<OperationResult> ShutdownResourcesOnceAsync() =>
		_resourceShutdown ??= ShutdownHostAsync();

	private async Task<OperationResult> ShutdownHostAsync()
	{
		Exception? firstFailure = null;
		var application = HostApplication;
		HostApplication = null;
		if ( application is not null )
		{
			var disposedApplication = await RuntimeAsyncOperation.Capture( application.DisposeAsync );
			if ( !disposedApplication.Succeeded ) firstFailure = disposedApplication.Exception;
		}

		var persistence = _persistence;
		_persistence = null;
		_hostSchema = null;
		if ( persistence is not null )
		{
			var drained = await RuntimeAsyncOperation.Capture( () => persistence.DrainAsync() );
			if ( !drained.Succeeded ) firstFailure ??= drained.Exception;
			var disposedPersistence = await RuntimeAsyncOperation.Capture( persistence.DisposeAsync );
			if ( !disposedPersistence.Succeeded ) firstFailure ??= disposedPersistence.Exception;
		}

		if ( firstFailure is not null )
		{
			DisposeHostLifetime();
			HostReadiness = HexRuntimeReadiness.Failed;
			Log.Error( firstFailure, "HEXAGON_DRAIN_FAILED host resources did not close cleanly." );
			return OperationResult.Failure( ErrorCode.InternalError, "Host resources did not close cleanly." );
		}

		if ( HostReadiness != HexRuntimeReadiness.Failed ) HostReadiness = HexRuntimeReadiness.Disposed;
		DisposeHostLifetime();
		Log.Info( "HEXAGON_DRAINED host" );
		return OperationResult.Success();
	}

	private HexHostServicesComponent CreateHostServices()
	{
		var servicesObject = new GameObject( true, "Hexagon v2 Host Services" );
		var services = servicesObject.AddComponent<HexHostServicesComponent>();
		services.Runtime = this;
		servicesObject.NetworkSpawn();
		return services;
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

	private static OperationResult<HostBootstrapOptions> ResolveHostOptions( HexagonBootstrapComponent bootstrap )
	{
		try
		{
			var requestedRoot = string.IsNullOrWhiteSpace( HexagonRuntimeOverrides.PersistenceRoot )
				? bootstrap.PersistenceRootOverride
				: HexagonRuntimeOverrides.PersistenceRoot;
			var root = string.IsNullOrWhiteSpace( requestedRoot )
				? string.Empty
				: PrefixedPersistenceStorage.Normalize( requestedRoot );
			var requestedProbe = string.IsNullOrWhiteSpace( HexagonRuntimeOverrides.VerificationProbe )
				? bootstrap.VerificationProbe
				: HexagonRuntimeOverrides.VerificationProbe;
			var probe = (requestedProbe ?? string.Empty).Trim();
			if ( probe.Length > 128 ) probe = probe[..128];
			return OperationResult<HostBootstrapOptions>.Success( new HostBootstrapOptions( root, probe ) );
		}
		catch ( Exception exception )
		{
			return OperationResult<HostBootstrapOptions>.Failure( ErrorCode.ConfigurationInvalid, exception.Message );
		}
	}

	private RpcActor BuildActor( Connection connection, RuntimePlayerSession session, bool requiresStableCharacter )
	{
		var character = HostApplication?.FindActiveCharacter( new ConnectionId( connection.Id ) );
		return new RpcActor(
			connection,
			new AccountId( connection.SteamId.ValueUnsigned ),
			session.Player,
			character,
			session.Boundary.Capture( character?.Id, requiresStableCharacter ) );
	}

	private void NotifyConnected( RpcActor actor )
	{
		try
		{
			HostApplication?.Connected( actor );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon connection initialization failed for '{actor.Connection.Id}'." );
		}
	}

	private void PollLifecycle()
	{
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

	private sealed record HostBootstrapOptions(
		string PersistenceRootOverride,
		string VerificationProbe );
}
