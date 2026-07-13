#nullable enable

using System;
using System.Collections.Generic;
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
	InvalidOperation
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

public sealed record CommitReceipt( long Sequence, IReadOnlyList<CommittedDocumentVersion> Documents );

public enum PersistenceHealthStatus
{
	Uninitialized = 0,
	Healthy,
	Degraded,
	Fatal,
	Disposed
}

/// <summary>
/// Observable persistence status. A checkpoint failure is degraded and retryable; a commit
/// durability failure is fatal because the caller cannot safely know how much of a frame reached storage.
/// </summary>
public sealed record PersistenceHealth(
	PersistenceHealthStatus Status,
	long Sequence,
	long CheckpointSequence,
	bool CheckpointRetryPending,
	bool RepairedPartialWalTail,
	bool RecoveredFromCheckpointFallback,
	string? Detail,
	DateTimeOffset ObservedAtUtc );

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
	ValueTask<PersistenceResult<CommitReceipt>> CommitAsync( CancellationToken cancellationToken = default );
}

public interface IPersistenceProvider : IAsyncDisposable
{
	PersistedTypeRegistry Types { get; }
	PersistenceHealth Health { get; }
	bool IsInitialized { get; }

	ValueTask InitializeAsync( CancellationToken cancellationToken = default );
	IPersistenceRepository<T> Repository<T>( string collection ) where T : class;
	IUnitOfWork BeginUnitOfWork();
	ValueTask<PersistenceResult<long>> CheckpointAsync( CancellationToken cancellationToken = default );
	ValueTask DrainAsync( CancellationToken cancellationToken = default );
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
