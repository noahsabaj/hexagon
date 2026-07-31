#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Durability boundary used by <c>FileSystemPersistenceProvider</c>. Paths are normalized,
/// relative storage keys. Immutable publication must be absent-or-complete and durable before returning.
/// Adapters that cannot guarantee atomic finalization (for example the s&amp;box editor adapter, which
/// has no atomic rename available to whitelist-safe code) may leave one torn artifact at the final
/// name after a crash; recovery tolerates exactly one such torn <em>tail</em> artifact per WAL
/// metadata class (acknowledgement, commit head, checkpoint prune intent) in the exact state an
/// interrupted write can produce, and stays fail-closed for every other corruption.
/// </summary>
public interface IPersistenceStorage
{
	ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default );
	ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default );
	ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default );
	ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default );
	ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default );
	ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default );
}

public interface IPersistenceLease : IAsyncDisposable
{
	string Path { get; }
	bool IsReleased { get; }
}

/// <summary>
/// Deterministic storage adapter useful for tests and ephemeral hosts. Unlike
/// <see cref="InMemoryPersistenceProvider"/>, it exercises the complete WAL/checkpoint implementation.
/// </summary>
public sealed class InMemoryPersistenceStorage : IPersistenceStorage
{
	private readonly ConcurrentDictionary<string, byte[]> _files = new( StringComparer.Ordinal );
	private readonly ConcurrentDictionary<string, InMemoryLease> _leases = new( StringComparer.Ordinal );

	public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = NormalizePath( path );
		var lease = new InMemoryLease( normalized, ReleaseLease );
		if ( !_leases.TryAdd( normalized, lease ) )
			throw new PersistenceLeaseUnavailableException( $"Persistence lease '{normalized}' is already held." );
		return ValueTask.FromResult<IPersistenceLease>( lease );
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult( _files.ContainsKey( NormalizePath( path ) ) );
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ( !_files.TryGetValue( NormalizePath( path ), out var content ) )
		{
			return ValueTask.FromResult<ReadOnlyMemory<byte>?>( null );
		}

		ReadOnlyMemory<byte>? result = new ReadOnlyMemory<byte>( content.ToArray() );
		return ValueTask.FromResult( result );
	}

	public ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalizedPrefix = NormalizePath( prefix ).TrimEnd( '/' ) + "/";
		IReadOnlyList<string> result = _files.Keys
			.Where( path => path.StartsWith( normalizedPrefix, StringComparison.Ordinal ) )
			.OrderBy( path => path, StringComparer.Ordinal )
			.ToArray();
		return ValueTask.FromResult( result );
	}

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult( _files.TryAdd( NormalizePath( path ), content.ToArray() ) );
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		_files.TryRemove( NormalizePath( path ), out _ );
		return ValueTask.CompletedTask;
	}

	private static string NormalizePath( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
		{
			throw new ArgumentException( $"Persistence path '{path}' is not a normalized relative path.", nameof(path) );
		}

		return normalized;
	}

	private void ReleaseLease( InMemoryLease lease ) => _leases.TryRemove( lease.Path, out _ );

	private sealed class InMemoryLease : IPersistenceLease
	{
		private readonly Action<InMemoryLease> _release;
		private int _released;

		public InMemoryLease( string path, Action<InMemoryLease> release )
		{
			Path = path;
			_release = release;
		}

		public string Path { get; }
		public bool IsReleased => Interlocked.CompareExchange( ref _released, 0, 0 ) != 0;

		public ValueTask DisposeAsync()
		{
			if ( Interlocked.Exchange( ref _released, 1 ) == 0 ) _release( this );
			return ValueTask.CompletedTask;
		}
	}
}

public sealed class PersistenceLeaseUnavailableException : IOException
{
	public PersistenceLeaseUnavailableException( string message ) : base( message ) { }
	public PersistenceLeaseUnavailableException( string message, Exception innerException ) : base( message, innerException ) { }
}

public sealed class PersistenceStorageLimitException : IOException
{
	public PersistenceStorageLimitException( string message ) : base( message ) { }
}

internal sealed record PersistenceFormatManifest
{
	public const string ExpectedFormat = "hexagon.persistence";
	public const int CurrentVersion = 3;

	public required string Format { get; init; }
	public required int Version { get; init; }
	public required string SchemaId { get; init; }
	public required Guid StoreId { get; init; }
	public required DateTimeOffset CreatedAtUtc { get; init; }
}

internal sealed record PersistedMutation
{
	public required int EnvelopeVersion { get; init; }
	public required string Collection { get; init; }
	public required string Key { get; init; }
	public required long Revision { get; init; }
	public required string PersistedType { get; init; }
	public required int TypeVersion { get; init; }
	public required bool IsDeleted { get; init; }
	public JsonElement? Payload { get; init; }

	public DocumentAddress Address => new( Collection, Key );
}

internal sealed record WalCommitBatch
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required long Sequence { get; init; }
	public required DateTimeOffset CommittedAtUtc { get; init; }
	public required IReadOnlyList<PersistedMutation> Mutations { get; init; }
}

internal sealed record WalFrame
{
	public const int CurrentFormatVersion = 1;
	public required int FormatVersion { get; init; }
	public required Guid StoreId { get; init; }
	public required Guid WriterEpoch { get; init; }
	public required long CompactionGeneration { get; init; }
	public required long Sequence { get; init; }
	public required string PreviousAckHash { get; init; }
	public required DateTimeOffset CommittedAtUtc { get; init; }
	public required IReadOnlyList<PersistedMutation> Mutations { get; init; }
}

internal sealed record WalAcknowledgement
{
	public const int CurrentFormatVersion = 1;
	public required int FormatVersion { get; init; }
	public required Guid StoreId { get; init; }
	public required Guid WriterEpoch { get; init; }
	public required long CompactionGeneration { get; init; }
	public required long Sequence { get; init; }
	public required string FramePath { get; init; }
	public required string FrameHash { get; init; }
	public required string PreviousAckHash { get; init; }
}

internal sealed record WalCommitHead
{
	public const int CurrentFormatVersion = 1;
	public required int FormatVersion { get; init; }
	public required Guid StoreId { get; init; }
	public required Guid WriterEpoch { get; init; }
	public required long CompactionGeneration { get; init; }
	public required long Sequence { get; init; }
	public required string AcknowledgementPath { get; init; }
	public required string AcknowledgementHash { get; init; }
	public required string FramePath { get; init; }
	public required string FrameHash { get; init; }
}

internal sealed record CheckpointSnapshot
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required long Sequence { get; init; }
	public required Guid StoreId { get; init; }
	public required long CompactionGeneration { get; init; }
	public required string LastAckHash { get; init; }
	public required IReadOnlyList<PersistedMutation> Documents { get; init; }
}

internal sealed record CheckpointManifest
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required string SchemaId { get; init; }
	public required Guid StoreId { get; init; }
	public required long Sequence { get; init; }
	public required long CompactionGeneration { get; init; }
	public required string LastAckHash { get; init; }
	public required string BlobPath { get; init; }
	public required string BlobHash { get; init; }
}

internal sealed record CheckpointCompletion
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required long Sequence { get; init; }
	public required string ManifestPath { get; init; }
	public required string ManifestHash { get; init; }
}

internal sealed record CheckpointPruneIntent
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required Guid StoreId { get; init; }
	public required long AnchorSequence { get; init; }
	public required string AnchorAckHash { get; init; }
	public required IReadOnlyList<string> ObsoleteCompletions { get; init; }
	public required IReadOnlyList<string> ObsoleteManifests { get; init; }
	public required IReadOnlyList<string> ObsoleteBlobs { get; init; }
}

internal sealed record RecoveryState(
	Guid StoreId,
	Guid WriterEpoch,
	long CompactionGeneration,
	string LastAckHash,
	long Sequence,
	long CheckpointSequence,
	IReadOnlyList<PersistedMutation> Documents,
	int DiscardedUnacknowledgedFrames,
	bool RecoveredFromCheckpointFallback,
	string? Detail,
	bool RecoveredByQuarantine = false,
	string? QuarantinePath = null );
