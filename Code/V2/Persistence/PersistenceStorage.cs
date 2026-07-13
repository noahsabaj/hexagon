#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Durability boundary used by <c>FileSystemPersistenceProvider</c>. Paths are normalized,
/// relative storage keys. Implementations must make AppendAsync durable before returning. An append
/// failure may leave a partial suffix; the provider enters a fatal state and repairs that suffix on restart.
/// </summary>
public interface IPersistenceStorage
{
	ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default );
	ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default );
	ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default );
	ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default );
	ValueTask AppendAsync( string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default );
	ValueTask TruncateAsync( string path, long length, CancellationToken cancellationToken = default );
	ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default );
}

/// <summary>
/// Deterministic storage adapter useful for tests and ephemeral hosts. Unlike
/// <see cref="InMemoryPersistenceProvider"/>, it exercises the complete WAL/checkpoint implementation.
/// </summary>
public sealed class InMemoryPersistenceStorage : IPersistenceStorage
{
	private readonly ConcurrentDictionary<string, byte[]> _files = new( StringComparer.Ordinal );

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

	public ValueTask AppendAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = NormalizePath( path );
		_files.AddOrUpdate(
			normalized,
			_ => content.ToArray(),
			(_, existing) =>
			{
				var combined = new byte[existing.Length + content.Length];
				existing.CopyTo( combined, 0 );
				content.Span.CopyTo( combined.AsSpan( existing.Length ) );
				return combined;
			} );
		return ValueTask.CompletedTask;
	}

	public ValueTask TruncateAsync( string path, long length, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ( length < 0 || length > int.MaxValue )
		{
			throw new ArgumentOutOfRangeException( nameof(length) );
		}

		var normalized = NormalizePath( path );
		_files.AddOrUpdate(
			normalized,
			_ => length == 0 ? Array.Empty<byte>() : throw new InvalidOperationException( $"Cannot truncate missing file '{normalized}'." ),
			(_, existing) =>
			{
				if ( length > existing.LongLength )
				{
					throw new InvalidOperationException( $"Cannot extend '{normalized}' through truncate." );
				}

				return existing.AsSpan( 0, (int)length ).ToArray();
			} );
		return ValueTask.CompletedTask;
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
}

internal sealed record PersistenceFormatManifest
{
	public const string ExpectedFormat = "hexagon.persistence";
	public const int CurrentVersion = 2;

	public required string Format { get; init; }
	public required int Version { get; init; }
	public required string SchemaId { get; init; }
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

internal sealed record CheckpointSnapshot
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required long Sequence { get; init; }
	public required IReadOnlyList<PersistedMutation> Documents { get; init; }
}

internal sealed record CheckpointManifest
{
	public const int CurrentFormatVersion = 1;

	public required int FormatVersion { get; init; }
	public required string SchemaId { get; init; }
	public required long Sequence { get; init; }
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

internal sealed record RecoveryState(
	long Sequence,
	long CheckpointSequence,
	IReadOnlyList<PersistedMutation> Documents,
	bool RepairedPartialWalTail,
	bool RecoveredFromCheckpointFallback,
	string? Detail );
