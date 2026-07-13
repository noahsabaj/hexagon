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
/// only after the complete durability operation succeeds.
/// </summary>
public abstract class TransactionalPersistenceProvider : IPersistenceProvider
{
	private readonly SemaphoreSlim _commitGate = new( 1, 1 );
	private readonly object _stateLock = new();
	private readonly Dictionary<DocumentAddress, CommittedState> _documents = new();
	private readonly Dictionary<string, HashSet<DocumentAddress>> _liveCollectionIndex = new( StringComparer.Ordinal );
	private readonly Dictionary<string, Type> _collectionTypes = new( StringComparer.Ordinal );
	private readonly Dictionary<string, object> _repositories = new( StringComparer.Ordinal );
	private PersistenceHealth _health;
	private bool _initialized;
	private bool _accepting;
	private bool _disposed;
	private long _sequence;
	private long _checkpointSequence;

	protected TransactionalPersistenceProvider( PersistedTypeRegistry? types = null )
	{
		Types = types ?? new PersistedTypeRegistry();
		_health = NewHealth( PersistenceHealthStatus.Uninitialized, detail: null );
	}

	public PersistedTypeRegistry Types { get; }
	public PersistenceHealth Health => _health;
	public bool IsInitialized => _initialized && !_disposed;

	protected virtual int AutomaticCheckpointCommitInterval => 0;

	public async ValueTask InitializeAsync( CancellationToken cancellationToken = default )
	{
		ThrowIfDisposed();
		if ( _initialized )
		{
			throw new InvalidOperationException( "Persistence provider is already initialized." );
		}

		await _commitGate.WaitAsync( cancellationToken );
		if ( _initialized )
		{
			_commitGate.Release();
			throw new InvalidOperationException( "Persistence provider is already initialized." );
		}

		Types.Freeze();
		var recovery = await AsyncOperation.Capture( () => RecoverCoreAsync( cancellationToken ) );
		if ( !recovery.Succeeded )
		{
			var exception = recovery.Exception!;
			_accepting = false;
			_health = NewHealth( PersistenceHealthStatus.Fatal, exception.Message );
			_commitGate.Release();
			ThrowInitializationFailure( exception, cancellationToken );
			return;
		}

		var apply = CaptureSynchronous( () => CompleteInitialization( recovery.Value! ) );
		if ( !apply.Succeeded )
		{
			var exception = apply.Exception!;
			_accepting = false;
			_health = NewHealth( PersistenceHealthStatus.Fatal, exception.Message );
			_commitGate.Release();
			ThrowInitializationFailure( exception, cancellationToken );
			return;
		}

		_commitGate.Release();
	}

	public IPersistenceRepository<T> Repository<T>( string collection ) where T : class
	{
		ThrowIfDisposed();
		var normalizedCollection = PersistenceName.Validate( collection, nameof(collection) );
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

			if ( _collectionTypes.TryGetValue( normalizedCollection, out var recoveredType ) && recoveredType != typeof(T) )
			{
				throw new InvalidOperationException(
					$"Collection '{normalizedCollection}' contains '{recoveredType.FullName}', not '{typeof(T).FullName}'." );
			}

			var repository = new PersistenceRepository<T>( this, normalizedCollection, codec );
			_repositories.Add( normalizedCollection, repository );
			_collectionTypes.TryAdd( normalizedCollection, typeof(T) );
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
		if ( !IsInitialized )
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
		_health = new PersistenceHealth(
			PersistenceHealthStatus.Healthy,
			_sequence,
			_checkpointSequence,
			false,
			_health.RepairedPartialWalTail,
			_health.RecoveredFromCheckpointFallback,
			null,
			DateTimeOffset.UtcNow );
		_commitGate.Release();
		return PersistenceResult<long>.Success( snapshot.Sequence );
	}

	public async ValueTask DrainAsync( CancellationToken cancellationToken = default )
	{
		ThrowIfDisposed();
		await _commitGate.WaitAsync( cancellationToken );
		_commitGate.Release();
	}

	public async ValueTask DisposeAsync()
	{
		if ( _disposed )
		{
			return;
		}

		_accepting = false;
		await _commitGate.WaitAsync();
		if ( _initialized && _health.Status != PersistenceHealthStatus.Fatal )
		{
			var snapshotCapture = CaptureSynchronous( CaptureCheckpointSnapshot );
			if ( !snapshotCapture.Succeeded )
			{
				_health = CreateCheckpointFailureHealth( snapshotCapture.Exception!, final: true );
			}
			else
			{
				var snapshot = snapshotCapture.Value!;
				var checkpoint = await AsyncOperation.Capture(
					() => PersistCheckpointCoreAsync( snapshot, CancellationToken.None ) );
				if ( checkpoint.Succeeded )
				{
					_checkpointSequence = snapshot.Sequence;
				}
				else
				{
					_health = CreateCheckpointFailureHealth( checkpoint.Exception!, final: true );
				}
			}
		}

		var disposal = await AsyncOperation.Capture( DisposeCoreAsync );
		_disposed = true;
		_initialized = false;
		_health = new PersistenceHealth(
			PersistenceHealthStatus.Disposed,
			_sequence,
			_checkpointSequence,
			_health.CheckpointRetryPending,
			_health.RepairedPartialWalTail,
			_health.RecoveredFromCheckpointFallback,
			disposal.Succeeded ? _health.Detail : $"Provider disposal failed: {disposal.Exception!.Message}",
			DateTimeOffset.UtcNow );
		_commitGate.Release();
		_commitGate.Dispose();

		if ( !disposal.Succeeded )
		{
			throw new InvalidOperationException( "Persistence provider disposal failed.", disposal.Exception );
		}
	}

	private protected abstract ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken );
	private protected abstract ValueTask PersistCommitCoreAsync( WalCommitBatch batch, CancellationToken cancellationToken );
	private protected abstract ValueTask PersistCheckpointCoreAsync( CheckpointSnapshot snapshot, CancellationToken cancellationToken );
	protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

	internal DocumentSnapshot<T>? Find<T>( PersistenceRepository<T> repository, string key ) where T : class
	{
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

		var validationError = ValidateExpectedRevisions( stagedChanges, dependencies );
		if ( validationError is not null )
		{
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure( validationError );
		}

		if ( stagedChanges.Count == 0 )
		{
			var empty = PersistenceResult<CommitReceipt>.Success(
				new CommitReceipt( _sequence, Array.Empty<CommittedDocumentVersion>() ) );
			_commitGate.Release();
			return empty;
		}

		var preparation = CaptureSynchronous( () => PrepareCommit( stagedChanges, checked(_sequence + 1) ) );
		if ( !preparation.Succeeded )
		{
			var exception = preparation.Exception!;
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.SerializationFailed, exception.Message, Exception: exception ) );
		}

		var prepared = preparation.Value!;
		// Once the durability call starts, caller cancellation cannot make commit state ambiguous.
		var durability = await AsyncOperation.Capture(
			() => PersistCommitCoreAsync( prepared.Batch, CancellationToken.None ) );
		if ( !durability.Succeeded )
		{
			var exception = durability.Exception!;
			_accepting = false;
			_health = CreateFatalCommitHealth( exception );
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
		}

		var publication = CaptureSynchronous( () => PublishCommit( prepared ) );
		if ( !publication.Succeeded )
		{
			var exception = publication.Exception!;
			_accepting = false;
			_health = CreateFatalCommitHealth( exception );
			_commitGate.Release();
			return PersistenceResult<CommitReceipt>.Failure(
				new PersistenceError( PersistenceErrorCode.DurabilityFailed, _health.Detail!, Exception: exception ) );
		}

		var receipt = publication.Value!;
		var shouldCheckpoint = AutomaticCheckpointCommitInterval > 0
			&& _sequence - _checkpointSequence >= AutomaticCheckpointCommitInterval;
		_commitGate.Release();

		if ( shouldCheckpoint )
		{
			_ = await CheckpointAsync( CancellationToken.None );
		}

		return PersistenceResult<CommitReceipt>.Success( receipt );
	}

	private void CompleteInitialization( RecoveryState recovered )
	{
		if ( recovered.Sequence < 0 || recovered.CheckpointSequence < 0 || recovered.CheckpointSequence > recovered.Sequence )
		{
			throw new PersistenceCorruptionException( "Recovered sequence metadata is invalid." );
		}

		lock ( _stateLock )
		{
			foreach ( var mutation in recovered.Documents )
			{
				ApplyRecoveredMutation( mutation );
			}

			_sequence = recovered.Sequence;
			_checkpointSequence = recovered.CheckpointSequence;
		}

		_initialized = true;
		_accepting = true;
		var recoveredWithRepair = recovered.RepairedPartialWalTail || recovered.RecoveredFromCheckpointFallback;
		_health = new PersistenceHealth(
			recoveredWithRepair ? PersistenceHealthStatus.Degraded : PersistenceHealthStatus.Healthy,
			_sequence,
			_checkpointSequence,
			recoveredWithRepair,
			recovered.RepairedPartialWalTail,
			recovered.RecoveredFromCheckpointFallback,
			recovered.Detail,
			DateTimeOffset.UtcNow );
	}

	private static void ThrowInitializationFailure( Exception exception, CancellationToken cancellationToken )
	{
		if ( exception is OperationCanceledException && cancellationToken.IsCancellationRequested )
		{
			throw new OperationCanceledException( "Persistence initialization was canceled.", exception, cancellationToken );
		}

		throw new PersistenceCorruptionException( $"Persistence initialization failed: {exception.Message}", exception );
	}

	private PersistenceHealth CreateCheckpointFailureHealth( Exception exception, bool final ) => new(
		PersistenceHealthStatus.Degraded,
		_sequence,
		_checkpointSequence,
		true,
		_health.RepairedPartialWalTail,
		_health.RecoveredFromCheckpointFallback,
		$"{(final ? "Final checkpoint failed" : "Checkpoint failed and will be retried")}: {exception.Message}",
		DateTimeOffset.UtcNow );

	private PersistenceHealth CreateFatalCommitHealth( Exception exception ) => new(
		PersistenceHealthStatus.Fatal,
		_sequence,
		_checkpointSequence,
		_health.CheckpointRetryPending,
		_health.RepairedPartialWalTail,
		_health.RecoveredFromCheckpointFallback,
		$"Commit durability or publication failed; restart is required before more writes: {exception.Message}",
		DateTimeOffset.UtcNow );

	private CommitReceipt PublishCommit( PreparedCommit prepared )
	{
		lock ( _stateLock )
		{
			foreach ( var state in prepared.States )
			{
				Publish( state );
			}

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
				.ToArray() );
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

			var payload = change.Codec.Serialize( change.Value! ).Clone();
			var canonicalValue = change.Codec.PrepareForPublication(
				change.Codec.Deserialize( payload, change.Codec.CurrentVersion ) );
			var mutation = new PersistedMutation
			{
				EnvelopeVersion = PersistedDocumentEnvelope.CurrentEnvelopeVersion,
				Collection = change.Address.Collection,
				Key = change.Address.Key,
				Revision = nextRevision.Value,
				PersistedType = change.Codec.Key.Value,
				TypeVersion = change.Codec.CurrentVersion,
				IsDeleted = false,
				Payload = payload
			};
			mutations.Add( mutation );
			states.Add( new CommittedState( change.Address, nextRevision, change.Codec, false, canonicalValue, payload ) );
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
		lock ( _stateLock )
		{
			return new CheckpointSnapshot
			{
				FormatVersion = CheckpointSnapshot.CurrentFormatVersion,
				Sequence = _sequence,
				Documents = _documents.Values
					.OrderBy( state => state.Address.Collection, StringComparer.Ordinal )
					.ThenBy( state => state.Address.Key, StringComparer.Ordinal )
					.Select( ToMutation )
					.ToArray()
			};
		}
	}

	private void ApplyRecoveredMutation( PersistedMutation mutation )
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

		BindCollectionType( address.Collection, codec.ClrType );

		if ( mutation.IsDeleted )
		{
			Publish( new CommittedState( address, new DocumentRevision( mutation.Revision ), codec, true, null, null ) );
			return;
		}

		if ( mutation.Payload is not { } payload )
		{
			throw new PersistenceCorruptionException( $"Live document '{address}' has no payload." );
		}

		try
		{
			var value = codec.PrepareForPublication( codec.Deserialize( payload, mutation.TypeVersion ) );
			var currentPayload = codec.Serialize( value ).Clone();
			Publish( new CommittedState(
				address,
				new DocumentRevision( mutation.Revision ),
				codec,
				false,
				value,
				currentPayload ) );
		}
		catch ( Exception exception ) when ( exception is JsonException or InvalidOperationException or NotSupportedException )
		{
			throw new PersistenceCorruptionException( $"Document '{address}' could not be decoded.", exception );
		}
	}

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
		}
		else
		{
			index.Add( state.Address );
		}
	}

	private void BindCollectionType( string collection, Type type )
	{
		if ( _collectionTypes.TryGetValue( collection, out var existing ) && existing != type )
		{
			throw new PersistenceCorruptionException(
				$"Collection '{collection}' mixes persisted CLR types '{existing.FullName}' and '{type.FullName}'." );
		}

		_collectionTypes[collection] = type;
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
		ThrowIfDisposed();
		if ( !_initialized || !_accepting )
		{
			throw new InvalidOperationException( _health.Detail ?? "Persistence provider is not accepting work." );
		}
	}

	private void EnsureRepository<T>( PersistenceRepository<T> repository ) where T : class
	{
		if ( !ReferenceEquals( repository.Provider, this ) )
		{
			throw new ArgumentException( "Repository belongs to a different persistence provider.", nameof(repository) );
		}
	}

	private void ThrowIfDisposed()
	{
		if ( _disposed )
		{
			throw new ObjectDisposedException( GetType().Name );
		}
	}

	private PersistenceHealth NewHealth( PersistenceHealthStatus status, string? detail ) => new(
		status,
		_sequence,
		_checkpointSequence,
		false,
		false,
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
	private bool _completed;

	public PersistenceUnitOfWork( TransactionalPersistenceProvider provider ) => _provider = provider;

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
			throw new ArgumentException( "Editor belongs to a different unit of work.", nameof(editor) );
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

	public async ValueTask<PersistenceResult<CommitReceipt>> CommitAsync( CancellationToken cancellationToken = default )
	{
		EnsureOpen();
		_completed = true;
		return await _provider.CommitAsync( _changes.Values, _dependencies.Values, cancellationToken );
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
		?? throw new ArgumentException( "Repository is not a Hexagon persistence repository.", nameof(repository) );

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
			throw new ObjectDisposedException( nameof(PersistenceUnitOfWork), "Unit of work is already completed." );
		}
	}
}
