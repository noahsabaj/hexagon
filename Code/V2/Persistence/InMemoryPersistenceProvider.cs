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
	public InMemoryPersistenceProvider(
		PersistedTypeRegistry? types = null,
		IPersistenceInvariantSet? invariants = null ) : base( types, invariants ) { }

	private protected override ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken )
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult( new RecoveryState(
			Guid.NewGuid(),
			Guid.NewGuid(),
			0,
			string.Empty,
			0,
			0,
			Array.Empty<PersistedMutation>(),
			0,
			false,
			null ) );
	}

	private protected override ValueTask<CommitPersistenceOutcome> PersistCommitCoreAsync(
		WalCommitBatch batch,
		CancellationToken cancellationToken ) =>
		ValueTask.FromResult( CommitPersistenceOutcome.Clean );

	private protected override ValueTask<CheckpointPersistenceOutcome> PersistCheckpointCoreAsync(
		CheckpointSnapshot snapshot,
		CancellationToken cancellationToken ) =>
		ValueTask.FromResult( CheckpointPersistenceOutcome.Clean );
}
