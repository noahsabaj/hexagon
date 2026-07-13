#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Deterministic, process-local provider. It shares all transaction, identity-map, tombstone,
/// and optimistic-concurrency behavior with the durable provider without performing I/O.
/// </summary>
public sealed class InMemoryPersistenceProvider : TransactionalPersistenceProvider
{
	public InMemoryPersistenceProvider( PersistedTypeRegistry? types = null ) : base( types ) { }

	private protected override ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken )
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult( new RecoveryState(
			0,
			0,
			Array.Empty<PersistedMutation>(),
			false,
			false,
			null ) );
	}

	private protected override ValueTask PersistCommitCoreAsync( WalCommitBatch batch, CancellationToken cancellationToken ) =>
		ValueTask.CompletedTask;

	private protected override ValueTask PersistCheckpointCoreAsync( CheckpointSnapshot snapshot, CancellationToken cancellationToken ) =>
		ValueTask.CompletedTask;
}
