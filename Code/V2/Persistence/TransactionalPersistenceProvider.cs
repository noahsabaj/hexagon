#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Shared transactional engine for volatile and WAL-backed providers. The committed view is changed
/// only after the transaction acknowledgement is durable. Recoverable metadata written after that
/// commit point may degrade health, but cannot make the acknowledged transaction fail or republish it.
/// </summary>
public abstract class TransactionalPersistenceProvider : IPersistenceProvider
{
	private readonly SemaphoreSlim _commitGate = new( 1, 1 );
	private readonly object _stateLock = new();
	private Dictionary<DocumentAddress, CommittedState> _documents = new();
	private Dictionary<string, HashSet<DocumentAddress>> _liveCollectionIndex = new( StringComparer.Ordinal );
	private PersistenceInvariantDocumentIndex _invariantDocumentIndex = new();
	private Dictionary<string, Type> _collectionTypes = new( StringComparer.Ordinal );
	private readonly Dictionary<string, object> _repositories = new( StringComparer.Ordinal );
	private readonly IPersistenceInvariantSet _invariants;
	private PersistenceHealth _health;
	private bool _accepting;
	private PersistenceProviderState _providerState;
	private PersistenceShutdownResult? _shutdownResult;
	private bool _shutdownCleanBeforeDisposal;
	private bool _shutdownRecoverableBeforeDisposal;
	private Guid _storeId;
	private Guid _writerEpoch;
	private long _sequence;
	private long _checkpointSequence;
	private long _compactionGeneration;
	private int _backgroundCheckpointScheduled;

	protected TransactionalPersistenceProvider(
		PersistedTypeRegistry? types = null,
		IPersistenceInvariantSet? invariants = null )
	{
		Types = types ?? new PersistedTypeRegistry();
		_invariants = invariants ?? EmptyPersistenceInvariantSet.Instance;
		_providerState = PersistenceProviderState.Created;
		_health = NewHealth( PersistenceHealthStatus.Uninitialized, detail: null );
	}

	public PersistedTypeRegistry Types { get; }
	public PersistenceHealth Health => _health;
	public bool IsInitialized => State == PersistenceProviderState.Ready;
	public PersistenceProviderState State
	{
		get
		{
			lock ( _stateLock ) return _providerState;
		}
	}
	public Guid StoreId => _storeId;
	public Guid WriterEpoch => _writerEpoch;
	public long CompactionGeneration => Interlocked.Read( ref _compactionGeneration );

	protected virtual int AutomaticCheckpointCommitInterval => 0;
	protected virtual bool ShouldCheckpointForStoragePressure => false;
	private protected virtual Func<Func<Task>, bool>? BackgroundCheckpointScheduler => null;

	public async ValueTask InitializeAsync( CancellationToken cancellationToken = default )
	{
		if ( State != PersistenceProviderState.Created )
			throw new InvalidOperationException( $"Persistence provider cannot initialize from state '{State}'." );

		await _commitGate.WaitAsync( cancellationToken );
		try
		{
			if ( State != PersistenceProviderState.Created )
				throw new InvalidOperationException( $"Persistence provider cannot initialize from state '{State}'." );
			SetProviderState( PersistenceProviderState.Initializing );

			Types.Freeze();
			var recovery = await AsyncOperation.Capture( () => RecoverCoreAsync( cancellationToken ) );
			if ( !recovery.Succeeded )
			{
				var original = recovery.Exception!;
				var exception = await FaultInitializationAsync( original );
				if ( original is PersistenceLeaseUnavailableException && ReferenceEquals( original, exception ) )
					throw original;
				ThrowInitializationFailure( exception, cancellationToken );
				return;
			}

			var apply = CaptureSynchronous( () => CompleteInitialization( recovery.Value! ) );
			if ( !apply.Succeeded )
			{
				var exception = await FaultInitializationAsync( apply.Exception! );
				ThrowInitializationFailure( exception, cancellationToken );
			}
		}
		finally
		{
			_commitGate.Release();
		}
	}

	public IPersistenceRepository<T> Repository<T>( string collection ) where T : class
	{
		EnsureReady();
		var normalizedCollection = PersistenceName.Validate( collection, nameof( collection ) );
		var codec = Types.Resolve<T>();

		lock ( _stateLock )
		{
			if ( _repositories.TryGetValue( normalizedCollection, out var existing ) )
			{
				if ( existing is not PersistenceRepository<T> typed )
				{
					throw new InvalidOperationException(
						$"Collection '{normalizedCollection}' is already bound to a different CLR type." );
				}

				return typed;
			}

			if ( _collectionTypes.TryGetValue( normalizedCollection, out var recoveredType ) && recoveredType != typeof( T ) )
			{
				throw new InvalidOperationException(
					$"Collection '{normalizedCollection}' contains '{recoveredType.FullName}', not '{typeof( T ).FullName}'." );
			}

			var repository = new PersistenceRepository<T>( this, normalizedCollection, codec );
			_repositories.Add( normalizedCollection, repository );
			_collectionTypes.TryAdd( normalizedCollection, typeof( T ) );
			return repository;
		}
	}

	public IUnitOfWork BeginUnitOfWork()
	{
		EnsureAccepting();
		return new PersistenceUnitOfWork( this );
	}

	public async ValueTask<PersistenceResult<long>> CheckpointAsync( CancellationToken cancellationToken = default )
	{
		if ( State != PersistenceProviderState.Ready )
		{
			return PersistenceResult<long>.Failure(
				new PersistenceError( PersistenceErrorCode.NotInitialized, "Persistence provider is not initialized." ) );
		}

		await _commitGate.WaitAsync( cancellationToken );
		if ( _health.Status == PersistenceHealthStatus.Fatal )
		{
			var error = PersistenceResult<long>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail ?? "Persistence provider is fatal." ) );
			_commitGate.Release();
			return error;
		}
		if ( _health.CommitMetadataRepairPending )
		{
			var repair = await AsyncOperation.Capture(
				() => RepairCommitMetadataCoreAsync( CancellationToken.None ) );
			if ( !repair.Succeeded )
			{
				var exception = repair.Exception!;
				_health = CreateCommitMetadataRepairFailureHealth( exception );
				_commitGate.Release();
				return PersistenceResult<long>.Failure( new PersistenceError(
					PersistenceErrorCode.DurabilityFailed,
					_health.Detail!,
					Exception: exception ) );
			}
			ApplySuccessfulCommitMetadataRepair();
		}

		var snapshotCapture = CaptureSynchronous( CaptureCheckpointSnapshot );
		if ( !snapshotCapture.Succeeded )
		{
			var exception = snapshotCapture.Exception!;
			_health = CreateCheckpointFailureHealth( exception, final: false );
			_commitGate.Release();
			return PersistenceResult<long>.Failure(
				new PersistenceError( PersistenceErrorCode.SerializationFailed, _health.Detail!, Exception: exception ) );
		}

		var snapshot = snapshotCapture.Value!;
		var persistence = await AsyncOperation.Capture( () => PersistCheckpointCoreAsync( snapshot, cancellationToken ) );
		if ( !persistence.Succeeded )
		{
			var exception = persistence.Exception!;
			_health = CreateCheckpointFailureHealth( exception, final: false );
			_commitGate.Release();
			if ( exception is OperationCanceledException && cancellationToken.IsCancellationRequested )
			{
				throw new OperationCanceledException( "Checkpoint was canceled.", exception, cancellationToken );
			}

			return PersistenceResult<long>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
		}

		_checkpointSequence = snapshot.Sequence;
		ApplySuccessfulCheckpoint( snapshot );
		var checkpointOutcome = persistence.Value!;
		_health = new PersistenceHealth(
			checkpointOutcome.CleanupSucceeded
				? PersistenceHealthStatus.Healthy
				: PersistenceHealthStatus.Degraded,
			_sequence,
			_checkpointSequence,
			!checkpointOutcome.CleanupSucceeded,
			_health.DiscardedUnacknowledgedFrames,
			_health.RecoveredFromCheckpointFallback,
			checkpointOutcome.Detail,
			DateTimeOffset.UtcNow );
		_commitGate.Release();
		return PersistenceResult<long>.Success( snapshot.Sequence );
	}

	public async ValueTask<PersistenceShutdownResult> ShutdownAsync( CancellationToken cancellationToken = default )
	{
		if ( _shutdownResult is { LeaseReleased: true } ) return _shutdownResult;
		await _commitGate.WaitAsync( cancellationToken );
		try
		{
			if ( _shutdownResult is { LeaseReleased: true } ) return _shutdownResult;
			if ( _shutdownResult is not null )
			{
				var pending = _shutdownResult;
				var disposalRetry = await AsyncOperation.Capture( DisposeCoreAsync );
				var retriedLeaseReleased = LeaseIsReleased;
				var retriedClean = _shutdownCleanBeforeDisposal && disposalRetry.Succeeded && retriedLeaseReleased;
				var retriedRecoverable = _shutdownRecoverableBeforeDisposal && disposalRetry.Succeeded && retriedLeaseReleased;
				var retriedDetail = retriedClean
					? null
					: disposalRetry.Succeeded
						? pending.Detail
						: $"Provider disposal failed: {disposalRetry.Exception!.Message}";
				_shutdownResult = pending with
				{
					IsClean = retriedClean,
					IsRecoverable = retriedRecoverable,
					LeaseReleased = retriedLeaseReleased,
					Detail = retriedDetail
				};
				_health = new PersistenceHealth(
					State == PersistenceProviderState.Faulted
						? PersistenceHealthStatus.Fatal
						: PersistenceHealthStatus.Disposed,
					_sequence,
					_checkpointSequence,
					!pending.Checkpoint.Succeeded,
					_health.DiscardedUnacknowledgedFrames,
					_health.RecoveredFromCheckpointFallback,
					retriedDetail,
					DateTimeOffset.UtcNow );
				return _shutdownResult;
			}
			_accepting = false;
			var priorState = State;
			if ( priorState == PersistenceProviderState.Ready )
				SetProviderState( PersistenceProviderState.Draining );

			Exception? metadataRepairFailure = null;
			if ( priorState == PersistenceProviderState.Ready && _health.CommitMetadataRepairPending )
			{
				var repair = await AsyncOperation.Capture(
					() => RepairCommitMetadataCoreAsync( CancellationToken.None ) );
				if ( repair.Succeeded ) ApplySuccessfulCommitMetadataRepair();
				else
				{
					metadataRepairFailure = repair.Exception!;
					_health = CreateCommitMetadataRepairFailureHealth( metadataRepairFailure );
				}
			}

			PersistenceResult<long> checkpoint;
			var checkpointCleanup = CheckpointPersistenceOutcome.Clean;
			if ( priorState == PersistenceProviderState.Ready && _health.Status != PersistenceHealthStatus.Fatal &&
				metadataRepairFailure is null )
			{
				var snapshotCapture = CaptureSynchronous( CaptureCheckpointSnapshot );
				if ( !snapshotCapture.Succeeded )
				{
					var exception = snapshotCapture.Exception!;
					_health = CreateCheckpointFailureHealth( exception, final: true );
					checkpoint = PersistenceResult<long>.Failure(
						new PersistenceError( PersistenceErrorCode.SerializationFailed, _health.Detail!, Exception: exception ) );
				}
				else
				{
					var snapshot = snapshotCapture.Value!;
					var persisted = await AsyncOperation.Capture(
						() => PersistCheckpointCoreAsync( snapshot, CancellationToken.None ) );
					if ( persisted.Succeeded )
					{
						checkpointCleanup = persisted.Value!;
						_checkpointSequence = snapshot.Sequence;
						ApplySuccessfulCheckpoint( snapshot );
						if ( !checkpointCleanup.CleanupSucceeded )
						{
							_health = new PersistenceHealth(
								PersistenceHealthStatus.Degraded,
								_sequence,
								_checkpointSequence,
								true,
								_health.DiscardedUnacknowledgedFrames,
								_health.RecoveredFromCheckpointFallback,
								checkpointCleanup.Detail,
								DateTimeOffset.UtcNow );
						}
						checkpoint = PersistenceResult<long>.Success( snapshot.Sequence );
					}
					else
					{
						var exception = persisted.Exception!;
						_health = CreateCheckpointFailureHealth( exception, final: true );
						checkpoint = PersistenceResult<long>.Failure(
							new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
					}
				}
			}
			else
			{
				checkpoint = PersistenceResult<long>.Failure( new PersistenceError(
					PersistenceErrorCode.DurabilityFailed,
					metadataRepairFailure is null
						? _health.Detail ?? "Final checkpoint was unavailable because the provider was not ready."
						: "Final checkpoint was skipped because acknowledged commit metadata repair failed.",
					Exception: metadataRepairFailure ) );
			}

			var disposal = await AsyncOperation.Capture( DisposeCoreAsync );
			var leaseReleased = LeaseIsReleased;
			var knownDurable = priorState == PersistenceProviderState.Ready &&
				_health.Status != PersistenceHealthStatus.Fatal;
			_shutdownCleanBeforeDisposal = knownDurable && checkpoint.Succeeded && checkpointCleanup.CleanupSucceeded;
			_shutdownRecoverableBeforeDisposal = knownDurable && _sequence >= _checkpointSequence;
			var clean = _shutdownCleanBeforeDisposal &&
				disposal.Succeeded && leaseReleased;
			var recoverable = _shutdownRecoverableBeforeDisposal && disposal.Succeeded && leaseReleased;
			var detail = clean ? null : disposal.Succeeded
				? checkpoint.Error?.Message ?? checkpointCleanup.Detail
				: $"Provider disposal failed: {disposal.Exception!.Message}";
			_shutdownResult = new PersistenceShutdownResult(
				_sequence,
				_checkpointSequence,
				clean,
				recoverable,
				checkpoint,
				leaseReleased,
				detail );
			_health = new PersistenceHealth(
				priorState == PersistenceProviderState.Faulted
					? PersistenceHealthStatus.Fatal
					: PersistenceHealthStatus.Disposed,
				_sequence,
				_checkpointSequence,
				!checkpoint.Succeeded,
				_health.DiscardedUnacknowledgedFrames,
				_health.RecoveredFromCheckpointFallback,
				detail,
				DateTimeOffset.UtcNow );
			SetProviderState( priorState == PersistenceProviderState.Faulted
				? PersistenceProviderState.Faulted
				: PersistenceProviderState.Disposed );
			return _shutdownResult;
		}
		finally
		{
			_commitGate.Release();
		}
	}

	public async ValueTask DisposeAsync() => _ = await ShutdownAsync( CancellationToken.None );

	private protected abstract ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken );
	private protected abstract ValueTask<CommitPersistenceOutcome> PersistCommitCoreAsync(
		WalCommitBatch batch,
		CancellationToken cancellationToken );
	private protected abstract ValueTask<CheckpointPersistenceOutcome> PersistCheckpointCoreAsync(
		CheckpointSnapshot snapshot,
		CancellationToken cancellationToken );
	private protected virtual ValueTask RepairCommitMetadataCoreAsync( CancellationToken cancellationToken ) =>
		ValueTask.CompletedTask;
	protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
	protected virtual bool LeaseIsReleased => true;
	protected virtual string DurableAckHash => string.Empty;

	internal DocumentSnapshot<T>? Find<T>( PersistenceRepository<T> repository, string key ) where T : class
	{
		EnsureReady();
		EnsureRepository( repository );
		var address = new DocumentAddress( repository.Collection, key );

		lock ( _stateLock )
		{
			if ( !_documents.TryGetValue( address, out var state ) || state.IsDeleted )
			{
				return null;
			}

			return new DocumentSnapshot<T>( key, state.Revision, (T)state.Value! );
		}
	}

	internal IReadOnlyList<DocumentSnapshot<T>> All<T>( PersistenceRepository<T> repository ) where T : class
	{
		EnsureReady();
		EnsureRepository( repository );
		lock ( _stateLock )
		{
			if ( !_liveCollectionIndex.TryGetValue( repository.Collection, out var addresses ) )
			{
				return Array.Empty<DocumentSnapshot<T>>();
			}

			return addresses
				.OrderBy( address => address.Key, StringComparer.Ordinal )
				.Select( address => _documents[address] )
				.Where( state => !state.IsDeleted )
				.Select( state => new DocumentSnapshot<T>( state.Address.Key, state.Revision, (T)state.Value! ) )
				.ToArray();
		}
	}

	internal CapturedDocument<T> Capture<T>( PersistenceRepository<T> repository, string key, bool clone ) where T : class
	{
		EnsureReady();
		EnsureRepository( repository );
		var address = new DocumentAddress( repository.Collection, key );
		lock ( _stateLock )
		{
			if ( !_documents.TryGetValue( address, out var state ) )
			{
				return new CapturedDocument<T>( address, DocumentRevision.None, false, null );
			}

			if ( state.IsDeleted )
			{
				return new CapturedDocument<T>( address, state.Revision, false, null );
			}

			var value = (T)state.Value!;
			if ( clone )
			{
				value = Clone( repository.Codec, value );
			}

			return new CapturedDocument<T>( address, state.Revision, true, value );
		}
	}

	internal async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync(
		IReadOnlyCollection<StagedChange> stagedChanges,
		IReadOnlyCollection<RevisionDependency> dependencies,
		IReadOnlyCollection<ICommitPrecondition> preconditions,
		long openedCompactionGeneration,
		CancellationToken cancellationToken )
	{
		if ( !_accepting || !IsInitialized )
		{
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError(
					_health.Status == PersistenceHealthStatus.Fatal ? PersistenceErrorCode.DurabilityFailed : PersistenceErrorCode.NotInitialized,
					_health.Detail ?? "Persistence provider is not accepting commits." ) );
		}

		await _commitGate.WaitAsync( cancellationToken );
		if ( !_accepting || _health.Status == PersistenceHealthStatus.Fatal )
		{
			var error = PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail ?? "Persistence provider is fatal." ) );
			_commitGate.Release();
			return error;
		}

		if ( _health.CommitMetadataRepairPending )
		{
			var repair = await AsyncOperation.Capture(
				() => RepairCommitMetadataCoreAsync( CancellationToken.None ) );
			if ( !repair.Succeeded )
			{
				var exception = repair.Exception!;
				_health = CreateCommitMetadataRepairFailureHealth( exception );
				_commitGate.Release();
				return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
					PersistenceErrorCode.DurabilityFailed,
					_health.Detail!,
					Exception: exception ) );
			}
			ApplySuccessfulCommitMetadataRepair();
		}
		if ( openedCompactionGeneration != _compactionGeneration )
		{
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
				PersistenceErrorCode.StaleTransaction,
				$"Unit of work opened under compaction generation {openedCompactionGeneration}; current generation is {_compactionGeneration}." ) );
		}

		var validationError = ValidateExpectedRevisions( stagedChanges, dependencies );
		if ( validationError is not null )
		{
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( validationError );
		}

		var preparation = CaptureSynchronous( () => stagedChanges.Count == 0
			? new PreparedCommit(
				new WalCommitBatch
				{
					FormatVersion = WalCommitBatch.CurrentFormatVersion,
					Sequence = _sequence,
					CommittedAtUtc = DateTimeOffset.UtcNow,
					Mutations = Array.Empty<PersistedMutation>()
				},
				Array.Empty<CommittedState>() )
			: PrepareCommit( stagedChanges, checked(_sequence + 1) ) );
		if ( !preparation.Succeeded )
		{
			var exception = preparation.Exception!;
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.SerializationFailed, exception.Message, Exception: exception ) );
		}

		var prepared = preparation.Value!;
		var candidateCapture = CaptureSynchronous( () => BuildInvariantContext(
			prepared.States,
			prepared.Batch.Sequence,
			_compactionGeneration ) );
		if ( !candidateCapture.Succeeded )
		{
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
				PersistenceErrorCode.InvariantViolation,
				$"Candidate state could not be validated: {candidateCapture.Exception!.Message}",
				Exception: candidateCapture.Exception ) );
		}
		var candidate = candidateCapture.Value!;
		foreach ( var precondition in preconditions )
		{
			PersistenceInvariantIssue? issue;
			try
			{
				issue = precondition.Validate( new CommitPreconditionContext(
					_sequence, _compactionGeneration, candidate ) );
			}
			catch ( Exception exception )
			{
				_commitGate.Release();
				return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
					PersistenceErrorCode.InvariantViolation,
					$"Commit precondition '{precondition.GetType().Name}' failed closed: {exception.Message}",
					Exception: exception ) );
			}
			if ( issue is not null )
			{
				_commitGate.Release();
				return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
					PersistenceErrorCode.RevisionConflict,
					$"Commit precondition failed at '{issue.Path}': {issue.Message}" ) );
			}
		}

		if ( stagedChanges.Count == 0 )
		{
			var empty = PersistenceResult<CommitReceipt>.Success(
				new CommitReceipt( _sequence, Array.Empty<CommittedDocumentVersion>() )
				{
					CompactionGeneration = _compactionGeneration
				} );
			_commitGate.Release();
			return empty;
		}

		IReadOnlyList<PersistenceInvariantIssue> invariantIssues;
		PersistenceInvariantPreparation? invariantPreparation = null;
		var incrementalInvariants = _invariants as IIncrementalPersistenceInvariantSet;
		try
		{
			if ( incrementalInvariants is not null )
			{
				invariantPreparation = incrementalInvariants.Prepare( candidate ) ??
					throw new InvalidOperationException( "Incremental invariant preparation returned null." );
				invariantIssues = invariantPreparation.Issues ??
					throw new InvalidOperationException( "Incremental invariant preparation returned null issues." );
			}
			else
			{
				invariantIssues = _invariants.Validate( candidate ) ??
					throw new InvalidOperationException( "Persistence invariant set returned null issues." );
			}
		}
		catch ( Exception exception )
		{
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
				PersistenceErrorCode.InvariantViolation,
				$"Persistence invariant set failed closed: {exception.Message}",
				Exception: exception ) );
		}
		if ( invariantIssues.Count > 0 )
		{
			var issue = invariantIssues[0];
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
				PersistenceErrorCode.InvariantViolation,
				$"Persistence invariant '{issue.Code}' failed at '{issue.Path}': {issue.Message}" ) );
		}

		// Once the durability call starts, caller cancellation cannot make commit state ambiguous.
		var durability = await AsyncOperation.Capture(
			() => PersistCommitCoreAsync( prepared.Batch, CancellationToken.None ) );
		if ( !durability.Succeeded )
		{
			var exception = durability.Exception!;
			if ( exception is PersistenceStorageLimitException )
			{
				_commitGate.Release();
				return PersistenceResult<CommitReceipt>.Failure( new PersistenceError(
					PersistenceErrorCode.StorageLimitExceeded, exception.Message, Exception: exception ) );
			}
			_accepting = false;
			_health = CreateFatalCommitHealth( exception );
			SetProviderState( PersistenceProviderState.Faulted );
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
		}

		var durabilityOutcome = durability.Value!;
		var publication = CaptureSynchronous( () => PublishCommit(
			prepared, incrementalInvariants, invariantPreparation ) );
		if ( !publication.Succeeded )
		{
			var exception = publication.Exception!;
			_accepting = false;
			_health = CreateFatalCommitHealth( exception );
			SetProviderState( PersistenceProviderState.Faulted );
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
		}

		var receipt = publication.Value!;
		if ( durabilityOutcome.MetadataRepairPending )
		{
			_health = _health with
			{
				Status = PersistenceHealthStatus.Degraded,
				CommitMetadataRepairPending = true,
				Detail = durabilityOutcome.Detail,
				ObservedAtUtc = DateTimeOffset.UtcNow
			};
		}
		var shouldCheckpoint = !durabilityOutcome.MetadataRepairPending && (ShouldCheckpointForStoragePressure ||
			(AutomaticCheckpointCommitInterval > 0
			&& _sequence - _checkpointSequence >= AutomaticCheckpointCommitInterval));
		_commitGate.Release();

		if ( shouldCheckpoint )
		{
			var scheduler = BackgroundCheckpointScheduler;
			if ( scheduler is null )
			{
				_ = await CheckpointAsync( CancellationToken.None );
			}
			else if ( Interlocked.CompareExchange( ref _backgroundCheckpointScheduled, 1, 0 ) == 0 )
			{
				// The committing caller must not pay for checkpoint I/O. Exactly one background
				// checkpoint may be in flight; a scheduler refusal (or throw) re-arms the latch
				// so a later threshold-crossing commit retries. Shutdown still takes its own
				// final checkpoint, so a refused automatic checkpoint is never data loss.
				var scheduled = CaptureSynchronous( () => scheduler( RunScheduledCheckpointAsync ) );
				if ( !scheduled.Succeeded || !scheduled.Value )
					Interlocked.Exchange( ref _backgroundCheckpointScheduled, 0 );
			}
		}

		return PersistenceResult<CommitReceipt>.Success( receipt );
	}

	private async Task RunScheduledCheckpointAsync()
	{
		try
		{
			_ = await CheckpointAsync( CancellationToken.None );
		}
		finally
		{
			Interlocked.Exchange( ref _backgroundCheckpointScheduled, 0 );
		}
	}

	private void CompleteInitialization( RecoveryState recovered )
	{
		if ( recovered.Sequence < 0 || recovered.CheckpointSequence < 0 || recovered.CheckpointSequence > recovered.Sequence )
		{
			throw new PersistenceCorruptionException( "Recovered sequence metadata is invalid." );
		}

		if ( recovered.StoreId == Guid.Empty || recovered.WriterEpoch == Guid.Empty || recovered.CompactionGeneration < 0 )
			throw new PersistenceCorruptionException( "Recovered store identity metadata is invalid." );

		var documents = new Dictionary<DocumentAddress, CommittedState>();
		var collectionTypes = new Dictionary<string, Type>( StringComparer.Ordinal );
		var liveIndex = new Dictionary<string, HashSet<DocumentAddress>>( StringComparer.Ordinal );
		foreach ( var mutation in recovered.Documents )
		{
			var state = DecodeRecoveredMutation( mutation, collectionTypes );
			documents.Add( state.Address, state );
			if ( !liveIndex.TryGetValue( state.Address.Collection, out var index ) )
			{
				index = new HashSet<DocumentAddress>();
				liveIndex.Add( state.Address.Collection, index );
			}
			if ( !state.IsDeleted ) index.Add( state.Address );
		}

		var recoveredCandidates = documents.Values
			.Where( state => !state.IsDeleted )
			.ToDictionary( state => state.Address, ToCandidate );
		var candidate = new PersistenceInvariantContext(
			recovered.Sequence,
			recovered.CompactionGeneration,
			recoveredCandidates,
			isRecovery: true );
		var issues = _invariants.Validate( candidate );
		if ( issues.Count > 0 )
		{
			var issue = issues[0];
			throw new PersistenceCorruptionException(
				$"Recovered persistence invariant '{issue.Code}' failed at '{issue.Path}': {issue.Message}" );
		}
		if ( _invariants is IIncrementalPersistenceInvariantSet incremental ) incremental.Rebuild( candidate );

		lock ( _stateLock )
		{
			_documents = documents;
			_liveCollectionIndex = liveIndex;
			_invariantDocumentIndex = new PersistenceInvariantDocumentIndex( recoveredCandidates.Values );
			_collectionTypes = collectionTypes;
			_storeId = recovered.StoreId;
			_writerEpoch = recovered.WriterEpoch;
			_compactionGeneration = recovered.CompactionGeneration;
			_sequence = recovered.Sequence;
			_checkpointSequence = recovered.CheckpointSequence;
		}

		// Discarded unacknowledged frames are normal crash recovery (the transactions were never
		// acknowledged), so only a checkpoint-fallback recovery degrades the store.
		var recoveredDegraded = recovered.RecoveredFromCheckpointFallback;
		_health = new PersistenceHealth(
			recoveredDegraded ? PersistenceHealthStatus.Degraded : PersistenceHealthStatus.Healthy,
			_sequence,
			_checkpointSequence,
			recoveredDegraded,
			recovered.DiscardedUnacknowledgedFrames,
			recovered.RecoveredFromCheckpointFallback,
			recovered.Detail,
			DateTimeOffset.UtcNow );
		_accepting = true;
		SetProviderState( PersistenceProviderState.Ready );
	}

	private static void ThrowInitializationFailure( Exception exception, CancellationToken cancellationToken )
	{
		if ( exception is OperationCanceledException && cancellationToken.IsCancellationRequested )
		{
			throw new OperationCanceledException( "Persistence initialization was canceled.", exception, cancellationToken );
		}

		throw new PersistenceCorruptionException( $"Persistence initialization failed: {exception.Message}", exception );
	}

	private async ValueTask<Exception> FaultInitializationAsync( Exception exception )
	{
		_accepting = false;
		SetProviderState( PersistenceProviderState.Faulted );
		var disposal = await AsyncOperation.Capture( DisposeCoreAsync );
		if ( !disposal.Succeeded )
		{
			exception = new AggregateException(
				"Persistence initialization failed and its acquired resources could not be released.",
				exception,
				disposal.Exception! );
		}
		_health = NewHealth( PersistenceHealthStatus.Fatal, exception.Message );
		return exception;
	}

	private PersistenceHealth CreateCheckpointFailureHealth( Exception exception, bool final ) => new(
		PersistenceHealthStatus.Degraded,
		_sequence,
		_checkpointSequence,
		true,
		_health.DiscardedUnacknowledgedFrames,
		_health.RecoveredFromCheckpointFallback,
		$"{(final ? "Final checkpoint failed" : "Checkpoint failed and will be retried")}: {exception.Message}",
		DateTimeOffset.UtcNow );

	private PersistenceHealth CreateFatalCommitHealth( Exception exception ) => new(
		PersistenceHealthStatus.Fatal,
		_sequence,
		_checkpointSequence,
		_health.CheckpointRetryPending,
		_health.DiscardedUnacknowledgedFrames,
		_health.RecoveredFromCheckpointFallback,
		$"Commit durability or publication failed; restart is required before more writes: {exception.Message}",
		DateTimeOffset.UtcNow );

	private PersistenceHealth CreateCommitMetadataRepairFailureHealth( Exception exception ) => _health with
	{
		Status = PersistenceHealthStatus.Degraded,
		CommitMetadataRepairPending = true,
		Detail = $"Acknowledged commit metadata repair failed and must be retried before another write: " +
			$"{exception.GetType().Name}: {exception.Message}",
		ObservedAtUtc = DateTimeOffset.UtcNow
	};

	private void ApplySuccessfulCommitMetadataRepair()
	{
		var remainsDegraded = _health.CheckpointRetryPending || _health.RecoveredFromCheckpointFallback;
		_health = _health with
		{
			Status = remainsDegraded ? PersistenceHealthStatus.Degraded : PersistenceHealthStatus.Healthy,
			CommitMetadataRepairPending = false,
			Detail = remainsDegraded ? _health.Detail : null,
			ObservedAtUtc = DateTimeOffset.UtcNow
		};
	}

	private CommitReceipt PublishCommit(
		PreparedCommit prepared,
		IIncrementalPersistenceInvariantSet? incrementalInvariants,
		PersistenceInvariantPreparation? invariantPreparation )
	{
		lock ( _stateLock )
		{
			foreach ( var state in prepared.States )
			{
				Publish( state );
			}
			if ( incrementalInvariants is not null )
				incrementalInvariants.Publish( invariantPreparation ??
					throw new InvalidOperationException( "Incremental invariant publication has no preparation." ) );

			_sequence = prepared.Batch.Sequence;
		}

		_health = _health with
		{
			Sequence = _sequence,
			ObservedAtUtc = DateTimeOffset.UtcNow
		};

		return new CommitReceipt(
			_sequence,
			prepared.States
				.Select( state => new CommittedDocumentVersion( state.Address, state.Revision, state.IsDeleted ) )
				.ToArray() )
		{
			CompactionGeneration = _compactionGeneration
		};
	}

	private static OperationOutcome CaptureSynchronous( Action operation )
	{
		try
		{
			operation();
			return OperationOutcome.Success();
		}
		catch ( Exception exception )
		{
			return OperationOutcome.Failure( exception );
		}
	}

	private static OperationOutcome<T> CaptureSynchronous<T>( Func<T> operation )
	{
		try
		{
			return OperationOutcome<T>.Success( operation() );
		}
		catch ( Exception exception )
		{
			return OperationOutcome<T>.Failure( exception );
		}
	}

	private PersistenceError? ValidateExpectedRevisions(
		IReadOnlyCollection<StagedChange> changes,
		IReadOnlyCollection<RevisionDependency> dependencies )
	{
		lock ( _stateLock )
		{
			foreach ( var dependency in dependencies )
			{
				_documents.TryGetValue( dependency.Address, out var current );
				var currentRevision = current?.Revision ?? DocumentRevision.None;
				if ( currentRevision != dependency.ExpectedRevision || current is not { IsDeleted: false } )
					return new PersistenceError(
						PersistenceErrorCode.RevisionConflict,
						$"Read dependency changed for '{dependency.Address}': expected {dependency.ExpectedRevision}, current {currentRevision}.",
						dependency.Address );
			}
			foreach ( var change in changes )
			{
				_documents.TryGetValue( change.Address, out var current );
				var currentRevision = current?.Revision ?? DocumentRevision.None;
				var currentExists = current is { IsDeleted: false };

				if ( currentRevision != change.ExpectedRevision )
				{
					return new PersistenceError(
						PersistenceErrorCode.RevisionConflict,
						$"Revision conflict for '{change.Address}': expected {change.ExpectedRevision}, current {currentRevision}.",
						change.Address );
				}

				if ( change.Requirement == StagedRequirement.Missing && currentExists )
				{
					return new PersistenceError(
						PersistenceErrorCode.AlreadyExists,
						$"Document '{change.Address}' already exists.",
						change.Address );
				}

				if ( change.Requirement == StagedRequirement.Existing && !currentExists )
				{
					return new PersistenceError(
						PersistenceErrorCode.NotFound,
						$"Document '{change.Address}' does not exist.",
						change.Address );
				}
			}

			return null;
		}
	}

	private PreparedCommit PrepareCommit( IReadOnlyCollection<StagedChange> changes, long sequence )
	{
		var states = new List<CommittedState>( changes.Count );
		var mutations = new List<PersistedMutation>( changes.Count );

		foreach ( var change in changes.OrderBy( change => change.Address.Collection, StringComparer.Ordinal )
			.ThenBy( change => change.Address.Key, StringComparer.Ordinal ) )
		{
			var nextRevision = change.ExpectedRevision.Next();
			if ( change.IsDeleted )
			{
				var tombstone = new PersistedMutation
				{
					EnvelopeVersion = PersistedTombstoneEnvelope.CurrentEnvelopeVersion,
					Collection = change.Address.Collection,
					Key = change.Address.Key,
					Revision = nextRevision.Value,
					PersistedType = change.Codec.Key.Value,
					TypeVersion = change.Codec.CurrentVersion,
					IsDeleted = true,
					Payload = null
				};
				mutations.Add( tombstone );
				states.Add( new CommittedState( change.Address, nextRevision, change.Codec, true, null, null ) );
				continue;
			}

			var canonical = PersistedCodecCanonicalization.FromValue( change.Codec, change.Value! );
			var mutation = new PersistedMutation
			{
				EnvelopeVersion = PersistedDocumentEnvelope.CurrentEnvelopeVersion,
				Collection = change.Address.Collection,
				Key = change.Address.Key,
				Revision = nextRevision.Value,
				PersistedType = change.Codec.Key.Value,
				TypeVersion = change.Codec.CurrentVersion,
				IsDeleted = false,
				Payload = canonical.Payload
			};
			mutations.Add( mutation );
			states.Add( new CommittedState(
				change.Address,
				nextRevision,
				change.Codec,
				false,
				canonical.Value,
				canonical.Payload ) );
		}

		return new PreparedCommit(
			new WalCommitBatch
			{
				FormatVersion = WalCommitBatch.CurrentFormatVersion,
				Sequence = sequence,
				CommittedAtUtc = DateTimeOffset.UtcNow,
				Mutations = mutations
			},
			states );
	}

	private CheckpointSnapshot CaptureCheckpointSnapshot()
	{
		CommittedState[] states;
		long sequence;
		Guid storeId;
		long compactionGeneration;
		lock ( _stateLock )
		{
			states = _documents.Values.ToArray();
			sequence = _sequence;
			storeId = _storeId;
			compactionGeneration = _compactionGeneration;
		}

		// Committed states are immutable and the commit gate is held for the whole capture, so
		// the tombstone scan, sort, and payload clones can run outside _stateLock without
		// blocking keyed reads.
		var reclaimsTombstones = states.Any( state => state.IsDeleted );
		return new CheckpointSnapshot
		{
			FormatVersion = CheckpointSnapshot.CurrentFormatVersion,
			Sequence = sequence,
			StoreId = storeId,
			CompactionGeneration = reclaimsTombstones
				? checked(compactionGeneration + 1)
				: compactionGeneration,
			LastAckHash = DurableAckHash,
			Documents = states
				.Where( state => !state.IsDeleted )
				.OrderBy( state => state.Address.Collection, StringComparer.Ordinal )
				.ThenBy( state => state.Address.Key, StringComparer.Ordinal )
				.Select( ToMutation )
				.ToArray()
		};
	}

	private void ApplySuccessfulCheckpoint( CheckpointSnapshot snapshot )
	{
		if ( snapshot.CompactionGeneration > _compactionGeneration )
		{
			lock ( _stateLock )
			{
				foreach ( var address in _documents
					.Where( pair => pair.Value.IsDeleted )
					.Select( pair => pair.Key )
					.ToArray() )
					_documents.Remove( address );
			}
		}
		_compactionGeneration = snapshot.CompactionGeneration;
	}

	private CommittedState DecodeRecoveredMutation(
		PersistedMutation mutation,
		IDictionary<string, Type> collectionTypes )
	{
		if ( mutation.EnvelopeVersion != PersistedDocumentEnvelope.CurrentEnvelopeVersion )
		{
			throw new PersistenceCorruptionException(
				$"Unsupported envelope version {mutation.EnvelopeVersion} for '{mutation.Collection}/{mutation.Key}'." );
		}

		if ( mutation.Revision <= 0 )
		{
			throw new PersistenceCorruptionException( $"Document '{mutation.Collection}/{mutation.Key}' has invalid revision {mutation.Revision}." );
		}

		var address = new DocumentAddress( mutation.Collection, mutation.Key );
		IPersistedTypeCodec codec;
		try
		{
			codec = Types.Resolve( new PersistedTypeKey( mutation.PersistedType ) );
		}
		catch ( Exception exception ) when ( exception is KeyNotFoundException or ArgumentException )
		{
			throw new PersistenceCorruptionException(
				$"Document '{address}' uses unknown persisted type '{mutation.PersistedType}'.",
				exception );
		}

		BindCollectionType( collectionTypes, address.Collection, codec.ClrType );
		if ( mutation.TypeVersion != codec.CurrentVersion )
			throw new PersistenceCorruptionException(
				$"Document '{address}' uses persisted type version {mutation.TypeVersion}; exact current version {codec.CurrentVersion} is required." );

		if ( mutation.IsDeleted )
		{
			return new CommittedState( address, new DocumentRevision( mutation.Revision ), codec, true, null, null );
		}

		if ( mutation.Payload is not { } payload )
		{
			throw new PersistenceCorruptionException( $"Live document '{address}' has no payload." );
		}

		try
		{
			var canonical = PersistedCodecCanonicalization.FromPayload(
				codec, payload, mutation.TypeVersion, requireCanonicalPayload: true );
			return new CommittedState(
				address,
				new DocumentRevision( mutation.Revision ),
				codec,
				false,
				canonical.Value,
				canonical.Payload );
		}
		catch ( Exception exception ) when ( exception is JsonException or InvalidOperationException or NotSupportedException )
		{
			throw new PersistenceCorruptionException( $"Document '{address}' could not be decoded.", exception );
		}
	}

	private PersistenceInvariantContext BuildInvariantContext(
		IEnumerable<CommittedState> replacements,
		long sequence,
		long compactionGeneration,
		bool isRecovery = false )
	{
		var delta = new Dictionary<DocumentAddress, PersistenceCandidateDocument?>();
		var changed = new HashSet<DocumentAddress>();
		foreach ( var state in replacements )
		{
			changed.Add( state.Address );
			delta[state.Address] = state.IsDeleted ? null : ToCandidate( state );
		}
		return new PersistenceInvariantContext(
			sequence, compactionGeneration, _invariantDocumentIndex, delta, changed, isRecovery );
	}

	private static PersistenceCandidateDocument ToCandidate( CommittedState state ) => new(
		state.Address,
		state.Revision,
		state.Codec.Key,
		state.Codec.CurrentVersion,
		state.Value! );

	private void Publish( CommittedState state )
	{
		BindCollectionType( state.Address.Collection, state.Codec.ClrType );
		_documents[state.Address] = state;

		if ( !_liveCollectionIndex.TryGetValue( state.Address.Collection, out var index ) )
		{
			index = new HashSet<DocumentAddress>();
			_liveCollectionIndex.Add( state.Address.Collection, index );
		}

		if ( state.IsDeleted )
		{
			index.Remove( state.Address );
			_invariantDocumentIndex.Remove( state.Address );
		}
		else
		{
			index.Add( state.Address );
			_invariantDocumentIndex.Publish( ToCandidate( state ) );
		}
	}

	private void BindCollectionType( string collection, Type type )
	{
		BindCollectionType( _collectionTypes, collection, type );
	}

	private static void BindCollectionType( IDictionary<string, Type> collectionTypes, string collection, Type type )
	{
		if ( collectionTypes.TryGetValue( collection, out var existing ) && existing != type )
		{
			throw new PersistenceCorruptionException(
				$"Collection '{collection}' mixes persisted CLR types '{existing.FullName}' and '{type.FullName}'." );
		}

		collectionTypes[collection] = type;
	}

	private static PersistedMutation ToMutation( CommittedState state ) => new()
	{
		EnvelopeVersion = state.IsDeleted
			? PersistedTombstoneEnvelope.CurrentEnvelopeVersion
			: PersistedDocumentEnvelope.CurrentEnvelopeVersion,
		Collection = state.Address.Collection,
		Key = state.Address.Key,
		Revision = state.Revision.Value,
		PersistedType = state.Codec.Key.Value,
		TypeVersion = state.Codec.CurrentVersion,
		IsDeleted = state.IsDeleted,
		Payload = state.Payload?.Clone()
	};

	private static T Clone<T>( IPersistedTypeCodec<T> codec, T value ) where T : class
	{
		var payload = codec.Serialize( value );
		return codec.Deserialize( payload, codec.CurrentVersion );
	}

	private void EnsureAccepting()
	{
		EnsureReady();
		if ( !_accepting )
		{
			throw new InvalidOperationException( _health.Detail ?? "Persistence provider is not accepting work." );
		}
	}

	private void EnsureRepository<T>( PersistenceRepository<T> repository ) where T : class
	{
		if ( !ReferenceEquals( repository.Provider, this ) )
		{
			throw new ArgumentException( "Repository belongs to a different persistence provider.", nameof( repository ) );
		}
	}

	private void EnsureReady()
	{
		var state = State;
		if ( state == PersistenceProviderState.Disposed ) throw new ObjectDisposedException( GetType().Name );
		if ( state != PersistenceProviderState.Ready )
			throw new InvalidOperationException( $"Persistence provider is not ready (state: {state})." );
	}

	private void SetProviderState( PersistenceProviderState state )
	{
		lock ( _stateLock ) _providerState = state;
	}

	private PersistenceHealth NewHealth( PersistenceHealthStatus status, string? detail ) => new(
		status,
		_sequence,
		_checkpointSequence,
		false,
		0,
		false,
		detail,
		DateTimeOffset.UtcNow );

	internal sealed record CapturedDocument<T>(
		DocumentAddress Address,
		DocumentRevision Revision,
		bool Exists,
		T? Value ) where T : class;

	internal sealed record StagedChange(
		DocumentAddress Address,
		IPersistedTypeCodec Codec,
		object? Value,
		DocumentRevision ExpectedRevision,
		StagedRequirement Requirement,
		bool IsDeleted );

	internal sealed record RevisionDependency(
		DocumentAddress Address,
		DocumentRevision ExpectedRevision );

	internal enum StagedRequirement
	{
		Any,
		Missing,
		Existing
	}

	private sealed record CommittedState(
		DocumentAddress Address,
		DocumentRevision Revision,
		IPersistedTypeCodec Codec,
		bool IsDeleted,
		object? Value,
		JsonElement? Payload );

	private sealed record PreparedCommit( WalCommitBatch Batch, IReadOnlyList<CommittedState> States );
}

internal sealed class PersistenceRepository<T> : IPersistenceRepository<T> where T : class
{
	public PersistenceRepository(
		TransactionalPersistenceProvider provider,
		string collection,
		IPersistedTypeCodec<T> codec )
	{
		Provider = provider;
		Collection = collection;
		Codec = codec;
	}

	internal TransactionalPersistenceProvider Provider { get; }
	internal IPersistedTypeCodec<T> Codec { get; }

	public string Collection { get; }
	public DocumentSnapshot<T>? Find( string key ) => Provider.Find( this, key );
	public IReadOnlyList<DocumentSnapshot<T>> All() => Provider.All( this );
}

internal sealed class PersistenceUnitOfWork : IUnitOfWork
{
	private readonly TransactionalPersistenceProvider _provider;
	private readonly Dictionary<DocumentAddress, TransactionalPersistenceProvider.StagedChange> _changes = new();
	private readonly Dictionary<DocumentAddress, TransactionalPersistenceProvider.RevisionDependency> _dependencies = new();
	private readonly List<ICommitPrecondition> _preconditions = new();
	private readonly long _openedCompactionGeneration;
	private bool _completed;

	public PersistenceUnitOfWork( TransactionalPersistenceProvider provider )
	{
		_provider = provider;
		_openedCompactionGeneration = provider.CompactionGeneration;
	}

	public DocumentEditor<T>? Edit<T>(
		IPersistenceRepository<T> repository,
		DocumentSnapshot<T> observed ) where T : class
	{
		ArgumentNullException.ThrowIfNull( observed );
		EnsureOpen();
		var concrete = Resolve( repository );
		var captured = _provider.Capture( concrete, observed.Key, clone: true );
		return !captured.Exists || captured.Revision != observed.Revision
			? null
			: new DocumentEditor<T>( this, repository, observed.Key, observed.Revision, captured.Value! );
	}

	public void RequireUnchanged<T>(
		IPersistenceRepository<T> repository,
		DocumentSnapshot<T> observed ) where T : class
	{
		ArgumentNullException.ThrowIfNull( observed );
		EnsureOpen();
		var concrete = Resolve( repository );
		var address = new DocumentAddress( concrete.Collection, observed.Key );
		if ( _changes.TryGetValue( address, out var change ) )
		{
			if ( change.ExpectedRevision != observed.Revision )
				throw new InvalidOperationException( $"Unit of work has conflicting revisions for '{address}'." );
			return;
		}
		if ( !_dependencies.TryAdd( address,
			new TransactionalPersistenceProvider.RevisionDependency( address, observed.Revision ) ) &&
			_dependencies[address].ExpectedRevision != observed.Revision )
			throw new InvalidOperationException( $"Unit of work has conflicting read dependencies for '{address}'." );
	}

	public void Create<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class
	{
		ArgumentNullException.ThrowIfNull( value );
		EnsureOpen();
		var concrete = Resolve( repository );
		var captured = _provider.Capture( concrete, key, clone: false );
		Stage( new TransactionalPersistenceProvider.StagedChange(
			captured.Address,
			concrete.Codec,
			value,
			captured.Revision,
			TransactionalPersistenceProvider.StagedRequirement.Missing,
			false ) );
	}

	public void Put<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class
	{
		ArgumentNullException.ThrowIfNull( value );
		EnsureOpen();
		var concrete = Resolve( repository );
		var captured = _provider.Capture( concrete, key, clone: false );
		Stage( new TransactionalPersistenceProvider.StagedChange(
			captured.Address,
			concrete.Codec,
			value,
			captured.Revision,
			TransactionalPersistenceProvider.StagedRequirement.Any,
			false ) );
	}

	public void Save<T>( DocumentEditor<T> editor ) where T : class
	{
		ArgumentNullException.ThrowIfNull( editor );
		EnsureOpen();
		if ( !editor.BelongsTo( this ) )
		{
			throw new ArgumentException( "Editor belongs to a different unit of work.", nameof( editor ) );
		}

		var concrete = Resolve( editor.Repository );
		Stage( new TransactionalPersistenceProvider.StagedChange(
			new DocumentAddress( concrete.Collection, editor.Key ),
			concrete.Codec,
			editor.Value,
			editor.OriginalRevision,
			TransactionalPersistenceProvider.StagedRequirement.Existing,
			false ) );
	}

	public void Delete<T>(
		IPersistenceRepository<T> repository,
		DocumentSnapshot<T> observed ) where T : class
	{
		ArgumentNullException.ThrowIfNull( observed );
		EnsureOpen();
		var concrete = Resolve( repository );
		Stage( new TransactionalPersistenceProvider.StagedChange(
			new DocumentAddress( concrete.Collection, observed.Key ),
			concrete.Codec,
			null,
			observed.Revision,
			TransactionalPersistenceProvider.StagedRequirement.Existing,
			true ) );
	}

	public void Require( ICommitPrecondition precondition )
	{
		ArgumentNullException.ThrowIfNull( precondition );
		EnsureOpen();
		_preconditions.Add( precondition );
	}

	public async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync( CancellationToken cancellationToken = default )
	{
		EnsureOpen();
		_completed = true;
		return await _provider.CommitAsync(
			_changes.Values,
			_dependencies.Values,
			_preconditions,
			_openedCompactionGeneration,
			cancellationToken );
	}

	public ValueTask DisposeAsync()
	{
		_completed = true;
		_changes.Clear();
		_dependencies.Clear();
		return ValueTask.CompletedTask;
	}

	private static PersistenceRepository<T> Resolve<T>( IPersistenceRepository<T> repository ) where T : class =>
		repository as PersistenceRepository<T>
		?? throw new ArgumentException( "Repository is not a Hexagon persistence repository.", nameof( repository ) );

	private void Stage( TransactionalPersistenceProvider.StagedChange change )
	{
		if ( _dependencies.Remove( change.Address, out var dependency ) &&
			dependency.ExpectedRevision != change.ExpectedRevision )
			throw new InvalidOperationException( $"Unit of work has conflicting revisions for '{change.Address}'." );
		if ( !_changes.TryAdd( change.Address, change ) )
		{
			throw new InvalidOperationException( $"Unit of work already contains a change for '{change.Address}'." );
		}
	}

	private void EnsureOpen()
	{
		if ( _completed )
		{
			throw new ObjectDisposedException( nameof( PersistenceUnitOfWork ), "Unit of work is already completed." );
		}
	}
}
