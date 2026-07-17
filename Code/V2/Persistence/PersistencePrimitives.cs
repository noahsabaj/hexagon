#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Stable persistence identifier for a concrete registered CLR type.
/// This deliberately remains independent from domain-layer identifiers.
/// </summary>
public readonly record struct PersistedTypeKey
{
	public string Value { get; }

	public PersistedTypeKey( string value )
	{
		Value = PersistenceName.Validate( value, nameof(value) );
	}

	public override string ToString() => Value;
}

/// <summary>
/// Address of one document in the logical persistence view.
/// </summary>
public readonly record struct DocumentAddress
{
	public string Collection { get; }
	public string Key { get; }

	public DocumentAddress( string collection, string key )
	{
		Collection = PersistenceName.Validate( collection, nameof(collection) );
		Key = PersistenceName.ValidateDocumentKey( key, nameof(key) );
	}

	public override string ToString() => $"{Collection}/{Key}";
}

/// <summary>
/// Monotonic per-document revision. Zero represents a document that has never existed.
/// Tombstones retain their positive revision.
/// </summary>
public readonly record struct DocumentRevision( long Value ) : IComparable<DocumentRevision>
{
	public static DocumentRevision None { get; } = new( 0 );

	public DocumentRevision Next()
	{
		if ( Value < 0 || Value == long.MaxValue )
		{
			throw new InvalidOperationException( $"Document revision {Value} cannot be incremented." );
		}

		return new DocumentRevision( Value + 1 );
	}

	public int CompareTo( DocumentRevision other ) => Value.CompareTo( other.Value );
	public override string ToString() => Value.ToString( System.Globalization.CultureInfo.InvariantCulture );
}

/// <summary>
/// Public, versioned representation of a committed document.
/// </summary>
public sealed record PersistedDocumentEnvelope
{
	public const int CurrentEnvelopeVersion = 1;

	public int EnvelopeVersion { get; init; } = CurrentEnvelopeVersion;
	public required string Collection { get; init; }
	public required string Key { get; init; }
	public required long Revision { get; init; }
	public required string PersistedType { get; init; }
	public required int TypeVersion { get; init; }
	public required JsonElement Payload { get; init; }
}

/// <summary>
/// Public, versioned representation of a committed deletion. Tombstones participate in
/// optimistic concurrency and prevent a deleted document from being mistaken for a never-seen key.
/// </summary>
public sealed record PersistedTombstoneEnvelope
{
	public const int CurrentEnvelopeVersion = 1;

	public int EnvelopeVersion { get; init; } = CurrentEnvelopeVersion;
	public required string Collection { get; init; }
	public required string Key { get; init; }
	public required long Revision { get; init; }
	public required string PersistedType { get; init; }
	public required int TypeVersion { get; init; }
}

public sealed record DocumentSnapshot<T>( string Key, DocumentRevision Revision, T Value ) where T : class;

public enum PersistenceErrorCode
{
	None = 0,
	NotInitialized,
	Disposed,
	NotFound,
	AlreadyExists,
	RevisionConflict,
	TypeNotRegistered,
	CollectionTypeMismatch,
	SerializationFailed,
	DurabilityFailed,
	CorruptStore,
	InvalidOperation,
	LeaseUnavailable,
	InvariantViolation,
	StorageLimitExceeded,
	StaleTransaction
}

public sealed record PersistenceError(
	PersistenceErrorCode Code,
	string Message,
	DocumentAddress? Address = null,
	Exception? Exception = null );

public readonly record struct PersistenceResult<T>( T? Value, PersistenceError? Error )
{
	public bool Succeeded => Error is null;

	public static PersistenceResult<T> Success( T value ) => new( value, null );
	public static PersistenceResult<T> Failure( PersistenceError error ) => new( default, error );
}

public sealed record CommittedDocumentVersion( DocumentAddress Address, DocumentRevision Revision, bool IsDeleted );

public sealed record CommitReceipt( long Sequence, IReadOnlyList<CommittedDocumentVersion> Documents )
{
	public long CompactionGeneration { get; init; }
}

public enum PersistenceProviderState
{
	Created = 0,
	Initializing,
	Ready,
	Draining,
	Disposed,
	Faulted
}

public sealed record PersistenceInvariantIssue( string Code, string Path, string Message );

public sealed record PersistenceCandidateDocument(
	DocumentAddress Address,
	DocumentRevision Revision,
	PersistedTypeKey PersistedType,
	int TypeVersion,
	object Value );

/// <summary>Read-only candidate view used identically before commit publication and during recovery.</summary>
public sealed class PersistenceInvariantContext
{
	private static readonly IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument?> EmptyDelta =
		new Dictionary<DocumentAddress, PersistenceCandidateDocument?>();
	private readonly PersistenceInvariantDocumentIndex _baseIndex;
	private readonly IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument?> _delta;
	private readonly Dictionary<string, IReadOnlyList<PersistenceCandidateDocument>> _collections =
		new( StringComparer.Ordinal );
	private IReadOnlyCollection<PersistenceCandidateDocument>? _documentValues;
	private long _visitedDocumentCount;

	internal PersistenceInvariantContext(
		long sequence,
		long compactionGeneration,
		IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument> documents,
		IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument>? previousDocuments = null,
		IReadOnlySet<DocumentAddress>? changedAddresses = null,
		bool isRecovery = false )
	{
		Sequence = sequence;
		CompactionGeneration = compactionGeneration;
		_baseIndex = new PersistenceInvariantDocumentIndex( documents.Values );
		_delta = EmptyDelta;
		PreviousDocuments = previousDocuments ?? new Dictionary<DocumentAddress, PersistenceCandidateDocument>();
		ChangedAddresses = changedAddresses ?? new HashSet<DocumentAddress>();
		IsRecovery = isRecovery;
	}

	internal PersistenceInvariantContext(
		long sequence,
		long compactionGeneration,
		PersistenceInvariantDocumentIndex baseIndex,
		IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument?> delta,
		IReadOnlySet<DocumentAddress> changedAddresses,
		bool isRecovery = false )
	{
		Sequence = sequence;
		CompactionGeneration = compactionGeneration;
		_baseIndex = baseIndex ?? throw new ArgumentNullException( nameof(baseIndex) );
		_delta = delta ?? throw new ArgumentNullException( nameof(delta) );
		PreviousDocuments = baseIndex.Documents;
		ChangedAddresses = changedAddresses ?? throw new ArgumentNullException( nameof(changedAddresses) );
		IsRecovery = isRecovery;
	}

	public long Sequence { get; }
	public long CompactionGeneration { get; }
	public bool IsRecovery { get; }
	public IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument> PreviousDocuments { get; }
	public IReadOnlySet<DocumentAddress> ChangedAddresses { get; }
	public long VisitedDocumentCount => Interlocked.Read( ref _visitedDocumentCount );
	public IReadOnlyCollection<PersistenceCandidateDocument> Documents =>
		_documentValues ??= MaterializeDocuments();

	public IReadOnlyList<PersistenceCandidateDocument> Collection( string collection )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( collection );
		if ( _collections.TryGetValue( collection, out var documents ) ) return documents;
		var values = _baseIndex.Collection( collection )
			.ToDictionary( document => document.Address, document => document );
		foreach ( var change in _delta )
		{
			if ( change.Key.Collection != collection ) continue;
			if ( change.Value is null ) values.Remove( change.Key );
			else values[change.Key] = change.Value;
		}
		documents = values.Values
			.OrderBy( document => document.Address.Key, StringComparer.Ordinal )
			.ToArray();
		RecordVisits( documents.Count );
		_collections.Add( collection, documents );
		return documents;
	}

	public bool TryGet( DocumentAddress address, out PersistenceCandidateDocument? document )
	{
		RecordVisits( 1 );
		if ( _delta.TryGetValue( address, out document ) ) return document is not null;
		return _baseIndex.Documents.TryGetValue( address, out document );
	}

	public bool TryGetPrevious( DocumentAddress address, out PersistenceCandidateDocument? document )
	{
		RecordVisits( 1 );
		return PreviousDocuments.TryGetValue( address, out document );
	}

	internal void RecordVisitedDocuments( int count )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof(count) );
		RecordVisits( count );
	}

	private IReadOnlyCollection<PersistenceCandidateDocument> MaterializeDocuments()
	{
		var values = new Dictionary<DocumentAddress, PersistenceCandidateDocument>( _baseIndex.Documents );
		foreach ( var change in _delta )
		{
			if ( change.Value is null ) values.Remove( change.Key );
			else values[change.Key] = change.Value;
		}
		var documents = values.Values.ToArray();
		RecordVisits( documents.Length );
		return documents;
	}

	private void RecordVisits( int count ) => Interlocked.Add( ref _visitedDocumentCount, count );
}

internal sealed class PersistenceInvariantDocumentIndex
{
	private readonly Dictionary<DocumentAddress, PersistenceCandidateDocument> _documents = new();
	private readonly IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument> _readOnlyDocuments;
	private readonly Dictionary<string, SortedDictionary<string, PersistenceCandidateDocument>> _collections =
		new( StringComparer.Ordinal );

	public PersistenceInvariantDocumentIndex() =>
		_readOnlyDocuments = new ReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument>( _documents );

	public PersistenceInvariantDocumentIndex( IEnumerable<PersistenceCandidateDocument> documents ) : this()
	{
		foreach ( var document in documents ) Publish( document );
	}

	public IReadOnlyDictionary<DocumentAddress, PersistenceCandidateDocument> Documents => _readOnlyDocuments;

	public IReadOnlyList<PersistenceCandidateDocument> Collection( string collection ) =>
		_collections.TryGetValue( collection, out var documents )
			? documents.Values.ToArray()
			: Array.Empty<PersistenceCandidateDocument>();

	public void Publish( PersistenceCandidateDocument document )
	{
		_documents[document.Address] = document;
		if ( !_collections.TryGetValue( document.Address.Collection, out var collection ) )
		{
			collection = new SortedDictionary<string, PersistenceCandidateDocument>( StringComparer.Ordinal );
			_collections.Add( document.Address.Collection, collection );
		}
		collection[document.Address.Key] = document;
	}

	public void Remove( DocumentAddress address )
	{
		_documents.Remove( address );
		if ( !_collections.TryGetValue( address.Collection, out var collection ) ) return;
		collection.Remove( address.Key );
		if ( collection.Count == 0 ) _collections.Remove( address.Collection );
	}
}

public interface IPersistenceInvariantSet
{
	IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context );
}

public sealed record PersistenceInvariantPreparation(
	IReadOnlyList<PersistenceInvariantIssue> Issues,
	object? PublicationState = null );

/// <summary>
/// Optional commit-time incremental invariant path. Prepare must not mutate the
/// committed invariant index. Publish runs only after the commit is durable;
/// Rebuild reconstructs the index from a fully validated recovery view.
/// </summary>
public interface IIncrementalPersistenceInvariantSet : IPersistenceInvariantSet
{
	PersistenceInvariantPreparation Prepare( PersistenceInvariantContext context );
	void Publish( PersistenceInvariantPreparation preparation );
	void Rebuild( PersistenceInvariantContext context );
}

public sealed class EmptyPersistenceInvariantSet : IPersistenceInvariantSet
{
	public static EmptyPersistenceInvariantSet Instance { get; } = new();
	private EmptyPersistenceInvariantSet() { }
	public IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context ) =>
		Array.Empty<PersistenceInvariantIssue>();
}

public sealed record CommitPreconditionContext(
	long Sequence,
	long CompactionGeneration,
	PersistenceInvariantContext Candidate );

public interface ICommitPrecondition
{
	PersistenceInvariantIssue? Validate( CommitPreconditionContext context );
}

public sealed record PersistenceShutdownResult(
	long DurableSequence,
	long CheckpointSequence,
	bool IsClean,
	bool IsRecoverable,
	PersistenceResult<long> Checkpoint,
	bool LeaseReleased,
	string? Detail );

internal readonly record struct CheckpointPersistenceOutcome(
	bool CleanupSucceeded,
	string? Detail )
{
	public static CheckpointPersistenceOutcome Clean { get; } = new( true, null );

	public static CheckpointPersistenceOutcome Degraded( Exception exception ) => new(
		false,
		$"Verified checkpoint was published, but immutable-history cleanup remains pending: " +
		$"{exception.GetType().Name}: {exception.Message}" );
}

/// <summary>
/// Result of publishing one immutable WAL acknowledgement. The acknowledgement is the
/// transaction commit point; metadata written after it is recoverable bookkeeping only.
/// </summary>
internal readonly record struct CommitPersistenceOutcome(
	bool MetadataRepairPending,
	string? Detail )
{
	public static CommitPersistenceOutcome Clean { get; } = new( false, null );

	public static CommitPersistenceOutcome Degraded( Exception exception ) => new(
		true,
		$"The transaction acknowledgement is durable, but redundant commit-head repair is pending: " +
		$"{exception.GetType().Name}: {exception.Message}" );
}

public enum PersistenceHealthStatus
{
	Uninitialized = 0,
	Healthy,
	Degraded,
	Fatal,
	Disposed
}

/// <summary>
/// Observable persistence status. A checkpoint or redundant commit-head failure is degraded and
/// retryable. A failure before a verified acknowledgement is fatal because the caller cannot safely
/// infer a durable transaction from a frame alone. <see cref="DiscardedUnacknowledgedFrames"/> counts
/// frames swept during recovery because no acknowledgement referenced them (interrupted commits and
/// interrupted checkpoint prunes); those transactions were never acknowledged, so their discard is
/// normal crash recovery, not data loss, and does not degrade the store.
/// </summary>
public sealed record PersistenceHealth(
	PersistenceHealthStatus Status,
	long Sequence,
	long CheckpointSequence,
	bool CheckpointRetryPending,
	int DiscardedUnacknowledgedFrames,
	bool RecoveredFromCheckpointFallback,
	string? Detail,
	DateTimeOffset ObservedAtUtc )
{
	/// <summary>
	/// True when an acknowledged transaction is durable and visible but its redundant
	/// commit-head file must be repaired before another write or clean shutdown.
	/// </summary>
	public bool CommitMetadataRepairPending { get; init; }
}

public sealed class PersistenceCorruptionException : Exception
{
	public PersistenceCorruptionException( string message ) : base( message ) { }
	public PersistenceCorruptionException( string message, Exception innerException ) : base( message, innerException ) { }
}

/// <summary>
/// Typed committed view for an immutable aggregate type. Repeated reads of one revision return the
/// same canonical instance; all proposed mutation must therefore use a unit-of-work editor.
/// </summary>
public interface IPersistenceRepository<T> where T : class
{
	string Collection { get; }
	DocumentSnapshot<T>? Find( string key );
	IReadOnlyList<DocumentSnapshot<T>> All();
}

public interface IUnitOfWork : IAsyncDisposable
{
	DocumentEditor<T>? Edit<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class;
	void RequireUnchanged<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class;
	void Create<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class;
	void Put<T>( IPersistenceRepository<T> repository, string key, T value ) where T : class;
	void Save<T>( DocumentEditor<T> editor ) where T : class;
	void Delete<T>( IPersistenceRepository<T> repository, DocumentSnapshot<T> observed ) where T : class;
	void Require( ICommitPrecondition precondition );
	ValueTask<PersistenceResult<CommitReceipt>> CommitAsync( CancellationToken cancellationToken = default );
}

public interface IPersistenceProvider : IAsyncDisposable
{
	PersistedTypeRegistry Types { get; }
	PersistenceHealth Health { get; }
	bool IsInitialized { get; }
	PersistenceProviderState State { get; }
	Guid StoreId { get; }
	Guid WriterEpoch { get; }
	long CompactionGeneration { get; }

	ValueTask InitializeAsync( CancellationToken cancellationToken = default );
	IPersistenceRepository<T> Repository<T>( string collection ) where T : class;
	IUnitOfWork BeginUnitOfWork();
	ValueTask<PersistenceResult<long>> CheckpointAsync( CancellationToken cancellationToken = default );
	ValueTask<PersistenceShutdownResult> ShutdownAsync( CancellationToken cancellationToken = default );
}

/// <summary>
/// Isolated editable copy of a committed document. Calling <see cref="Replace"/> or mutating
/// <see cref="Value"/> never changes the logical view until the owning unit of work commits.
/// </summary>
public sealed class DocumentEditor<T> where T : class
{
	private readonly object _owner;

	internal DocumentEditor(
		object owner,
		IPersistenceRepository<T> repository,
		string key,
		DocumentRevision originalRevision,
		T value )
	{
		_owner = owner;
		Repository = repository;
		Key = key;
		OriginalRevision = originalRevision;
		Value = value;
	}

	public IPersistenceRepository<T> Repository { get; }
	public string Key { get; }
	public DocumentRevision OriginalRevision { get; }
	public T Value { get; private set; }

	public void Replace( T value ) => Value = value ?? throw new ArgumentNullException( nameof(value) );

	internal bool BelongsTo( object owner ) => ReferenceEquals( _owner, owner );
}

internal static class PersistenceName
{
	private const int MaximumNameLength = 128;
	private const int MaximumDocumentKeyLength = 512;

	public static string Validate( string value, string parameterName )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( value, parameterName );

		if ( value.Length > MaximumNameLength )
		{
			throw new ArgumentOutOfRangeException( parameterName, $"Persistence name exceeds {MaximumNameLength} characters." );
		}

		for ( var index = 0; index < value.Length; index++ )
		{
			var character = value[index];
			if ( !IsNameCharacter( character ) )
			{
				throw new ArgumentException(
					$"Persistence name '{value}' contains invalid character '{character}'. Use lowercase letters, digits, '.', '_' or '-'.",
					parameterName );
			}
		}

		return value;
	}

	public static string ValidateDocumentKey( string value, string parameterName )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( value, parameterName );

		if ( value.Length > MaximumDocumentKeyLength )
		{
			throw new ArgumentOutOfRangeException( parameterName, $"Document key exceeds {MaximumDocumentKeyLength} characters." );
		}

		for ( var index = 0; index < value.Length; index++ )
		{
			var character = value[index];
			if ( char.IsControl( character ) || character is '/' or '\\' )
			{
				throw new ArgumentException( "Document keys cannot contain control characters or path separators.", parameterName );
			}
		}

		return value;
	}

	private static bool IsNameCharacter( char character ) =>
		character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}
