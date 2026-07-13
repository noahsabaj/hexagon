#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Application;
using Hexagon.V2.Client;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Networking;
using Hexagon.V2.Persistence;
using Hexagon.V2.Runtime;
using Sandbox;

namespace Hexagon.V2.Infrastructure;

public sealed class SchemaSourceCollector
{
	private readonly Dictionary<string, HexSchemaRuntimeDescriptor> _registrations =
		new( StringComparer.Ordinal );
	private readonly List<string> _errors = new();

	public IReadOnlyList<string> Errors => _errors;

	public void Register( HexSchemaRuntimeDescriptor descriptor )
	{
		if ( descriptor is null )
		{
			_errors.Add( "Schema runtime descriptor cannot be null." );
			return;
		}
		if ( !_registrations.TryAdd( descriptor.Schema.Id, descriptor ) )
			_errors.Add( $"Schema runtime ID '{descriptor.Schema.Id}' was registered more than once." );
	}

	public OperationResult<HexSchemaRuntimeDescriptor> Require( string schemaId )
	{
		if ( _errors.Count > 0 )
			return OperationResult<HexSchemaRuntimeDescriptor>.Failure(
				ErrorCode.DuplicateRegistration, string.Join( " ", _errors ) );
		return _registrations.TryGetValue( schemaId, out var descriptor )
			? OperationResult<HexSchemaRuntimeDescriptor>.Success( descriptor )
			: OperationResult<HexSchemaRuntimeDescriptor>.Failure(
				ErrorCode.UnknownDefinition, $"Schema runtime '{schemaId}' is not mounted." );
	}
}

/// <summary>
/// Explicit scene-scoped schema source. Game packages implement this on a
/// GameObjectSystem; no assembly scanning or reflected construction is used.
/// </summary>
public interface IHexSchemaSource : ISceneEvent<IHexSchemaSource>
{
	void CollectSchemas( SchemaSourceCollector collector );
}

public sealed record HexSchemaRuntimeDescriptor
{
	public required IHexSchema Schema { get; init; }
	public required SchemaPersistenceInvariantProfile PersistenceInvariants { get; init; }
	public IReadOnlyList<IPersistedTypeCodec> PersistenceCodecs { get; init; } =
		Array.Empty<IPersistedTypeCodec>();
	/// <summary>
	/// Creates the game-owned durable storage adapter. Hexagon defines and consumes the
	/// persistence protocol, while the standalone game owns any raw operating-system access.
	/// </summary>
	public required Func<IPersistenceStorage> CreatePersistenceStorage { get; init; }
	public required Func<HexHostRuntimeContext, IHexHostApplication> CreateHostApplication { get; init; }
	public Action<HexClientRuntimeContext>? ConfigureClient { get; init; }
}

public sealed record HexHostRuntimeContext(
	Scene Scene,
	CompiledSchema Schema,
	IPersistenceProvider Persistence,
	DomainRepositories Repositories,
	ITypedConfigurationStore Configuration,
	IHexHostTransport Transport,
	string PersistenceRoot,
	string VerificationProbe );

public sealed record HexClientRuntimeContext(
	Scene Scene,
	HexClientStore Store,
	HexClientController Controller );

public interface IHexHostTransport
{
	void SendOperationResult(
		Connection recipient,
		ClientSessionScope scope,
		CommandRequestId requestId,
		OperationResult result );
	void SendCharacterList( Connection recipient, CharacterListSnapshot snapshot );
	void SendClientState(
		Connection recipient,
		PlayerPublicSnapshot publicSnapshot,
		PlayerPrivateSnapshot? privateSnapshot,
		PlayerRosterSnapshot roster,
		IReadOnlyList<SchemaViewSnapshot> schemaViews,
		IReadOnlyList<Hexagon.V2.Networking.InventorySnapshot> inventories,
		ActionProgressSnapshot? activeAction );
	void SendChat(
		Connection recipient,
		long revision,
		IReadOnlyList<ChatMessageSnapshot> messages );
}

public interface IHexHostApplication : IAsyncDisposable
{
	ValueTask<OperationResult> InitializeAsync( CancellationToken cancellationToken = default );
	void Connected( RpcActor actor );
	void Disconnected( RpcActor actor );
	CharacterRecord? FindActiveCharacter( ConnectionId connectionId );
	ValueTask<OperationResult> HandleCommandAsync(
		RpcActor actor,
		ClientCommand command,
		CancellationToken cancellationToken = default );
}
