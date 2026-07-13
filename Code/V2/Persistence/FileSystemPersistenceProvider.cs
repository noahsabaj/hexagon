#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.V2.Persistence;

public sealed record FileSystemPersistenceOptions
{
	public FileSystemPersistenceOptions( string schemaId )
	{
		SchemaId = PersistenceName.Validate( schemaId, nameof(schemaId) );
	}

	public string SchemaId { get; }
	public int CheckpointEveryCommits { get; init; } = 128;
	public int RetainedCheckpointGenerations { get; init; } = 2;

	internal void Validate()
	{
		if ( CheckpointEveryCommits < 0 )
		{
			throw new ArgumentOutOfRangeException( nameof(CheckpointEveryCommits) );
		}

		if ( RetainedCheckpointGenerations < 2 )
		{
			throw new ArgumentOutOfRangeException(
				nameof(RetainedCheckpointGenerations),
				"At least two complete checkpoint generations must be retained." );
		}
	}
}

/// <summary>
/// WAL-backed single-host provider. All I/O is delegated to <see cref="IPersistenceStorage"/> so
/// Sandbox.FileSystem, physical-disk, or test adapters can be supplied by the composition root.
/// </summary>
public sealed class FileSystemPersistenceProvider : TransactionalPersistenceProvider
{
	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		WriteIndented = false
	};

	private readonly IPersistenceStorage _storage;
	private readonly FileSystemPersistenceOptions _options;
	private readonly string _formatManifestPath;
	private readonly string _walPath;
	private readonly string _checkpointBlobPrefix;
	private readonly string _checkpointManifestPrefix;
	private readonly string _checkpointCompletionPrefix;

	public FileSystemPersistenceProvider(
		IPersistenceStorage storage,
		FileSystemPersistenceOptions options,
		PersistedTypeRegistry? types = null ) : base( types )
	{
		_storage = storage ?? throw new ArgumentNullException( nameof(storage) );
		_options = options ?? throw new ArgumentNullException( nameof(options) );
		_options.Validate();

		RootPath = $"hexagon/v2/{_options.SchemaId}";
		_formatManifestPath = Path( "format.json" );
		_walPath = Path( "wal/commits.wal" );
		_checkpointBlobPrefix = Path( "checkpoints/blobs" );
		_checkpointManifestPrefix = Path( "checkpoints/manifests" );
		_checkpointCompletionPrefix = Path( "checkpoints/complete" );
	}

	public string RootPath { get; }

	protected override int AutomaticCheckpointCommitInterval => _options.CheckpointEveryCommits;

	private protected override async ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken )
	{
		await EnsureFormatManifestAsync( cancellationToken );
		var checkpoint = await LoadCheckpointAsync( cancellationToken );
		var documents = new Dictionary<DocumentAddress, PersistedMutation>();
		var checkpointSequence = checkpoint.Snapshot?.Sequence ?? 0;

		if ( checkpoint.Snapshot is not null )
		{
			LoadCheckpointDocuments( checkpoint.Snapshot, documents );
		}

		var walBytes = await _storage.ReadAsync( _walPath, cancellationToken );
		var repairedPartialTail = false;
		long sequence = checkpointSequence;

		if ( walBytes is { } bytes && bytes.Length > 0 )
		{
			var decoded = WalFrameCodec.Decode( bytes.Span );
			if ( decoded.HasPartialTail )
			{
				await _storage.TruncateAsync( _walPath, decoded.ValidLength, cancellationToken );
				repairedPartialTail = true;
			}

			long? previousWalSequence = null;
			foreach ( var batch in decoded.Batches )
			{
				if ( previousWalSequence is { } previous && batch.Sequence != checked(previous + 1) )
				{
					throw new PersistenceCorruptionException(
						$"WAL sequence gap or duplicate: expected {previous + 1}, found {batch.Sequence}." );
				}

				previousWalSequence = batch.Sequence;
				if ( batch.Sequence <= checkpointSequence )
				{
					continue;
				}

				if ( batch.Sequence != checked(sequence + 1) )
				{
					throw new PersistenceCorruptionException(
						$"WAL cannot continue checkpoint {sequence}; next sequence is {batch.Sequence}." );
				}

				ApplyWalBatch( batch, documents );
				sequence = batch.Sequence;
			}
		}

		var detailParts = new List<string>();
		if ( checkpoint.Detail is not null )
		{
			detailParts.Add( checkpoint.Detail );
		}
		if ( repairedPartialTail )
		{
			detailParts.Add( "A partial final WAL frame was truncated." );
		}

		return new RecoveryState(
			sequence,
			checkpointSequence,
			documents.Values
				.OrderBy( mutation => mutation.Collection, StringComparer.Ordinal )
				.ThenBy( mutation => mutation.Key, StringComparer.Ordinal )
				.ToArray(),
			repairedPartialTail,
			checkpoint.RecoveredFromFallback,
			detailParts.Count == 0 ? null : string.Join( " ", detailParts ) );
	}

	private protected override ValueTask PersistCommitCoreAsync(
		WalCommitBatch batch,
		CancellationToken cancellationToken )
	{
		var frame = WalFrameCodec.Encode( batch );
		return _storage.AppendAsync( _walPath, frame, cancellationToken );
	}

	private protected override async ValueTask PersistCheckpointCoreAsync(
		CheckpointSnapshot snapshot,
		CancellationToken cancellationToken )
	{
		var blobBytes = JsonSerializer.SerializeToUtf8Bytes( snapshot, _jsonOptions );
		var blobHash = ComputeHash( blobBytes );
		var blobPath = $"{_checkpointBlobPrefix}/{blobHash}.json";
		await EnsureImmutableContentAsync( blobPath, blobBytes, cancellationToken );

		var manifest = new CheckpointManifest
		{
			FormatVersion = CheckpointManifest.CurrentFormatVersion,
			SchemaId = _options.SchemaId,
			Sequence = snapshot.Sequence,
			BlobPath = blobPath,
			BlobHash = blobHash
		};
		var manifestBytes = JsonSerializer.SerializeToUtf8Bytes( manifest, _jsonOptions );
		var manifestHash = ComputeHash( manifestBytes );
		var generationName = $"{snapshot.Sequence:D20}-{blobHash}";
		var manifestPath = $"{_checkpointManifestPrefix}/{generationName}.json";
		await EnsureImmutableContentAsync( manifestPath, manifestBytes, cancellationToken );

		var completion = new CheckpointCompletion
		{
			FormatVersion = CheckpointCompletion.CurrentFormatVersion,
			Sequence = snapshot.Sequence,
			ManifestPath = manifestPath,
			ManifestHash = manifestHash
		};
		var completionBytes = JsonSerializer.SerializeToUtf8Bytes( completion, _jsonOptions );
		var completionPath = $"{_checkpointCompletionPrefix}/{generationName}.complete";
		await EnsureImmutableContentAsync( completionPath, completionBytes, cancellationToken );

		await PruneOldCheckpointMetadataAsync( cancellationToken );
	}

	private async ValueTask EnsureFormatManifestAsync( CancellationToken cancellationToken )
	{
		var existing = await _storage.ReadAsync( _formatManifestPath, cancellationToken );
		if ( existing is null )
		{
			var manifest = new PersistenceFormatManifest
			{
				Format = PersistenceFormatManifest.ExpectedFormat,
				Version = PersistenceFormatManifest.CurrentVersion,
				SchemaId = _options.SchemaId,
				CreatedAtUtc = DateTimeOffset.UtcNow
			};
			var bytes = JsonSerializer.SerializeToUtf8Bytes( manifest, _jsonOptions );
			if ( bytes.Length == 0 )
			{
				throw new InvalidOperationException( "JSON serializer returned an empty format manifest." );
			}
			ReadOnlyMemory<byte> formatContent = new ReadOnlyMemory<byte>( bytes );
			if ( formatContent.Length != bytes.Length )
			{
				throw new InvalidOperationException( "Format-manifest memory view lost its content." );
			}
			_ = await _storage.TryWriteImmutableAsync( _formatManifestPath, formatContent, cancellationToken );
			existing = await _storage.ReadAsync( _formatManifestPath, cancellationToken )
				?? throw new PersistenceCorruptionException( "Format manifest disappeared during initialization." );
		}

		PersistenceFormatManifest decoded;
		try
		{
			decoded = JsonSerializer.Deserialize<PersistenceFormatManifest>( existing.Value.Span, _jsonOptions )
				?? throw new JsonException( "Format manifest is null." );
		}
		catch ( Exception exception ) when ( exception is JsonException or NotSupportedException )
		{
			throw new PersistenceCorruptionException(
				$"Format manifest is invalid ({existing.Value.Length} bytes): {exception.Message}", exception );
		}

		if ( decoded.Format != PersistenceFormatManifest.ExpectedFormat
			|| decoded.Version != PersistenceFormatManifest.CurrentVersion
			|| decoded.SchemaId != _options.SchemaId )
		{
			throw new PersistenceCorruptionException(
				$"Persistence root '{RootPath}' has incompatible format '{decoded.Format}' v{decoded.Version} for schema '{decoded.SchemaId}'." );
		}
	}

	private async ValueTask<CheckpointLoad> LoadCheckpointAsync( CancellationToken cancellationToken )
	{
		var completionPaths = await _storage.ListAsync( _checkpointCompletionPrefix, cancellationToken );
		if ( completionPaths.Count == 0 )
		{
			return new CheckpointLoad( null, false, null );
		}

		var valid = new List<CheckpointCandidate>();
		var failures = new List<string>();
		foreach ( var completionPath in completionPaths )
		{
			var candidate = await AsyncOperation.Capture( () => ReadCheckpointAsync( completionPath, cancellationToken ) );
			if ( candidate.Succeeded )
			{
				valid.Add( candidate.Value! );
				continue;
			}

			var exception = candidate.Exception!;
			if ( exception is PersistenceCorruptionException or JsonException or NotSupportedException )
			{
				failures.Add( $"{completionPath}: {exception.Message}" );
				continue;
			}

			throw new InvalidOperationException( $"Reading checkpoint '{completionPath}' failed.", exception );
		}

		if ( valid.Count == 0 )
		{
			return new CheckpointLoad(
				null,
				true,
				$"No valid checkpoint generation was available; replaying WAL. {string.Join( " | ", failures )}" );
		}

		var selected = valid.OrderByDescending( candidate => candidate.Snapshot.Sequence ).First();
		var fallback = failures.Count > 0;
		return new CheckpointLoad(
			selected.Snapshot,
			fallback,
			fallback
				? $"Recovered checkpoint {selected.Snapshot.Sequence} after ignoring invalid generation(s): {string.Join( " | ", failures )}"
				: null );
	}

	private async ValueTask<CheckpointCandidate> ReadCheckpointAsync(
		string completionPath,
		CancellationToken cancellationToken )
	{
		var completionBytes = await _storage.ReadAsync( completionPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint completion '{completionPath}' is missing." );
		var completion = Deserialize<CheckpointCompletion>( completionBytes, "checkpoint completion" );
		if ( completion.FormatVersion != CheckpointCompletion.CurrentFormatVersion || completion.Sequence < 0 )
		{
			throw new PersistenceCorruptionException( $"Checkpoint completion '{completionPath}' has invalid metadata." );
		}

		EnsurePathUnder( completion.ManifestPath, _checkpointManifestPrefix );
		var manifestBytes = await _storage.ReadAsync( completion.ManifestPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' is missing." );
		if ( ComputeHash( manifestBytes.Span ) != completion.ManifestHash )
		{
			throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' failed its checksum." );
		}

		var manifest = Deserialize<CheckpointManifest>( manifestBytes, "checkpoint manifest" );
		if ( manifest.FormatVersion != CheckpointManifest.CurrentFormatVersion
			|| manifest.SchemaId != _options.SchemaId
			|| manifest.Sequence != completion.Sequence )
		{
			throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' has incompatible metadata." );
		}

		EnsurePathUnder( manifest.BlobPath, _checkpointBlobPrefix );
		var blobBytes = await _storage.ReadAsync( manifest.BlobPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint blob '{manifest.BlobPath}' is missing." );
		if ( ComputeHash( blobBytes.Span ) != manifest.BlobHash )
		{
			throw new PersistenceCorruptionException( $"Checkpoint blob '{manifest.BlobPath}' failed its checksum." );
		}

		var snapshot = Deserialize<CheckpointSnapshot>( blobBytes, "checkpoint snapshot" );
		if ( snapshot.FormatVersion != CheckpointSnapshot.CurrentFormatVersion || snapshot.Sequence != manifest.Sequence )
		{
			throw new PersistenceCorruptionException( $"Checkpoint snapshot '{manifest.BlobPath}' has incompatible metadata." );
		}

		return new CheckpointCandidate( completionPath, completion.ManifestPath, manifest.BlobPath, snapshot );
	}

	private static void LoadCheckpointDocuments(
		CheckpointSnapshot snapshot,
		Dictionary<DocumentAddress, PersistedMutation> documents )
	{
		if ( snapshot.Sequence < 0 || snapshot.Documents is null )
		{
			throw new PersistenceCorruptionException( "Checkpoint snapshot metadata is invalid." );
		}

		foreach ( var mutation in snapshot.Documents )
		{
			ValidateMutationShape( mutation );
			if ( mutation.Revision > snapshot.Sequence )
			{
				throw new PersistenceCorruptionException(
					$"Checkpoint document '{mutation.Address}' revision {mutation.Revision} exceeds checkpoint sequence {snapshot.Sequence}." );
			}
			if ( !documents.TryAdd( mutation.Address, mutation ) )
			{
				throw new PersistenceCorruptionException( $"Checkpoint contains duplicate document '{mutation.Address}'." );
			}
		}
	}

	private static void ApplyWalBatch(
		WalCommitBatch batch,
		Dictionary<DocumentAddress, PersistedMutation> documents )
	{
		var seen = new HashSet<DocumentAddress>();
		foreach ( var mutation in batch.Mutations )
		{
			ValidateMutationShape( mutation );
			if ( !seen.Add( mutation.Address ) )
			{
				throw new PersistenceCorruptionException(
					$"WAL sequence {batch.Sequence} changes '{mutation.Address}' more than once." );
			}

			documents.TryGetValue( mutation.Address, out var current );
			var expectedRevision = checked((current?.Revision ?? 0) + 1);
			if ( mutation.Revision != expectedRevision )
			{
				throw new PersistenceCorruptionException(
					$"WAL sequence {batch.Sequence} gives '{mutation.Address}' revision {mutation.Revision}; expected {expectedRevision}." );
			}

			documents[mutation.Address] = mutation;
		}
	}

	private static void ValidateMutationShape( PersistedMutation mutation )
	{
		if ( mutation.EnvelopeVersion != PersistedDocumentEnvelope.CurrentEnvelopeVersion || mutation.Revision <= 0 )
		{
			throw new PersistenceCorruptionException( "Persisted mutation has incompatible envelope metadata." );
		}

		_ = mutation.Address;
		_ = new PersistedTypeKey( mutation.PersistedType );
		if ( mutation.TypeVersion <= 0 )
		{
			throw new PersistenceCorruptionException( $"Persisted mutation '{mutation.Address}' has invalid type version." );
		}

		if ( mutation.IsDeleted == mutation.Payload.HasValue )
		{
			throw new PersistenceCorruptionException(
				$"Persisted mutation '{mutation.Address}' has an invalid deletion/payload combination." );
		}
	}

	private async ValueTask EnsureImmutableContentAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken )
	{
		if ( await _storage.TryWriteImmutableAsync( path, content, cancellationToken ) )
		{
			return;
		}

		var existing = await _storage.ReadAsync( path, cancellationToken )
			?? throw new InvalidOperationException( $"Immutable path '{path}' exists but cannot be read." );
		if ( !existing.Span.SequenceEqual( content.Span ) )
		{
			throw new PersistenceCorruptionException( $"Immutable path collision at '{path}'." );
		}
	}

	private async ValueTask PruneOldCheckpointMetadataAsync( CancellationToken cancellationToken )
	{
		var completionPaths = await _storage.ListAsync( _checkpointCompletionPrefix, cancellationToken );
		var valid = new List<CheckpointCandidate>();
		foreach ( var path in completionPaths )
		{
			var candidate = await AsyncOperation.Capture( () => ReadCheckpointAsync( path, cancellationToken ) );
			if ( candidate.Succeeded )
			{
				valid.Add( candidate.Value! );
				continue;
			}

			if ( candidate.Exception is not (PersistenceCorruptionException or JsonException or NotSupportedException) )
			{
				throw new InvalidOperationException( $"Checkpoint retention could not inspect '{path}'.", candidate.Exception );
			}
		}

		var retained = valid
			.OrderByDescending( candidate => candidate.Snapshot.Sequence )
			.ThenByDescending( candidate => candidate.CompletionPath, StringComparer.Ordinal )
			.Take( _options.RetainedCheckpointGenerations )
			.ToArray();
		var retainedCompletions = retained.Select( candidate => candidate.CompletionPath ).ToHashSet( StringComparer.Ordinal );
		var retainedManifests = retained.Select( candidate => candidate.ManifestPath ).ToHashSet( StringComparer.Ordinal );
		var retainedBlobs = retained.Select( candidate => candidate.BlobPath ).ToHashSet( StringComparer.Ordinal );

		foreach ( var path in completionPaths.Where( path => !retainedCompletions.Contains( path ) ) )
		{
			await _storage.DeleteAsync( path, cancellationToken );
		}

		var manifestPaths = await _storage.ListAsync( _checkpointManifestPrefix, cancellationToken );
		foreach ( var path in manifestPaths.Where( path => !retainedManifests.Contains( path ) ) )
		{
			await _storage.DeleteAsync( path, cancellationToken );
		}

		var blobPaths = await _storage.ListAsync( _checkpointBlobPrefix, cancellationToken );
		foreach ( var path in blobPaths.Where( path => !retainedBlobs.Contains( path ) ) )
		{
			await _storage.DeleteAsync( path, cancellationToken );
		}
	}

	private T Deserialize<T>( ReadOnlyMemory<byte> content, string description ) where T : class
	{
		try
		{
			return JsonSerializer.Deserialize<T>( content.Span, _jsonOptions )
				?? throw new JsonException( $"{description} is null." );
		}
		catch ( Exception exception ) when ( exception is JsonException or NotSupportedException )
		{
			throw new PersistenceCorruptionException( $"Invalid {description}.", exception );
		}
	}

	private static string ComputeHash( ReadOnlySpan<byte> content )
	{
		var hash = SHA256.HashData( content );
		var builder = new StringBuilder( hash.Length * 2 );
		foreach ( var value in hash )
		{
			builder.Append( value.ToString( "x2", System.Globalization.CultureInfo.InvariantCulture ) );
		}
		return builder.ToString();
	}

	private void EnsurePathUnder( string path, string prefix )
	{
		var invalidSegment = !string.IsNullOrWhiteSpace( path )
			&& path.Split( '/' ).Any( segment => segment is "" or "." or ".." );
		if ( string.IsNullOrWhiteSpace( path )
			|| path.Contains( '\\' )
			|| invalidSegment
			|| !path.StartsWith( prefix + "/", StringComparison.Ordinal ) )
		{
			throw new PersistenceCorruptionException( $"Checkpoint path '{path}' escapes '{prefix}'." );
		}
	}

	private string Path( string suffix ) => $"{RootPath}/{suffix}";

	private sealed record CheckpointCandidate(
		string CompletionPath,
		string ManifestPath,
		string BlobPath,
		CheckpointSnapshot Snapshot );

	private sealed record CheckpointLoad(
		CheckpointSnapshot? Snapshot,
		bool RecoveredFromFallback,
		string? Detail );
}
