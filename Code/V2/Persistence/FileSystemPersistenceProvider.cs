#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
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
		SchemaId = PersistenceName.Validate( schemaId, nameof( schemaId ) );
	}

	public string SchemaId { get; }
	public int CheckpointEveryCommits { get; init; } = 128;
	public int RetainedCheckpointGenerations { get; init; } = 2;
	public long SoftCheckpointBytes { get; init; } = 16L * 1024 * 1024;
	public long MaximumRetainedWalBytes { get; init; } = 64L * 1024 * 1024;
	public int MaximumFrameBytes { get; init; } = 8 * 1024 * 1024;
	public int MaximumRetainedCommits { get; init; } = 1_024;
	public Action<string>? Log { get; init; }

	internal void Validate()
	{
		if ( CheckpointEveryCommits < 0 ) throw new ArgumentOutOfRangeException( nameof( CheckpointEveryCommits ) );
		if ( RetainedCheckpointGenerations < 2 )
			throw new ArgumentOutOfRangeException(
				nameof( RetainedCheckpointGenerations ),
				"At least two complete checkpoint generations must be retained." );
		if ( SoftCheckpointBytes <= 0 ) throw new ArgumentOutOfRangeException( nameof( SoftCheckpointBytes ) );
		if ( MaximumRetainedWalBytes < SoftCheckpointBytes )
			throw new ArgumentOutOfRangeException( nameof( MaximumRetainedWalBytes ) );
		if ( MaximumFrameBytes <= 0 || MaximumFrameBytes > MaximumRetainedWalBytes )
			throw new ArgumentOutOfRangeException( nameof( MaximumFrameBytes ) );
		if ( MaximumRetainedCommits <= 0 ) throw new ArgumentOutOfRangeException( nameof( MaximumRetainedCommits ) );
	}
}

/// <summary>
/// Immutable-frame WAL provider. A process-wide lease is acquired before any store file is read.
/// A commit is visible only after its frame and acknowledgement have both been reread and verified.
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
	private readonly string _leasePath;
	private readonly string _framePrefix;
	private readonly string _ackPrefix;
	private readonly string _commitHeadPrefix;
	private readonly string _checkpointBlobPrefix;
	private readonly string _checkpointManifestPrefix;
	private readonly string _checkpointCompletionPrefix;
	private readonly string _pruneIntentPrefix;
	private IPersistenceLease? _lease;
	private Guid _storeId;
	private Guid _writerEpoch;
	private long _durableSequence;
	private long _durableGeneration;
	private string _lastAckHash = string.Empty;
	private long _retainedWalBytes;
	private int _retainedCommitCount;
	private PendingCommitHead? _pendingCommitHead;

	public FileSystemPersistenceProvider(
		IPersistenceStorage storage,
		FileSystemPersistenceOptions options,
		PersistedTypeRegistry? types = null,
		IPersistenceInvariantSet? invariants = null ) : base( types, invariants )
	{
		_storage = storage ?? throw new ArgumentNullException( nameof( storage ) );
		_options = options ?? throw new ArgumentNullException( nameof( options ) );
		_options.Validate();
		RootPath = $"hexagon/persistence/v3/{_options.SchemaId}";
		LegacyRootPath = $"hexagon/v2/{_options.SchemaId}";
		_formatManifestPath = Path( "format.json" );
		_leasePath = Path( "lease.lock" );
		_framePrefix = Path( "wal/frames" );
		_ackPrefix = Path( "wal/acks" );
		_commitHeadPrefix = Path( "wal/commit-heads" );
		_checkpointBlobPrefix = Path( "checkpoints/blobs" );
		_checkpointManifestPrefix = Path( "checkpoints/manifests" );
		_checkpointCompletionPrefix = Path( "checkpoints/complete" );
		_pruneIntentPrefix = Path( "checkpoints/prune-intents" );
	}

	public string RootPath { get; }
	public string LegacyRootPath { get; }

	protected override int AutomaticCheckpointCommitInterval => _options.CheckpointEveryCommits;
	protected override bool ShouldCheckpointForStoragePressure => _retainedWalBytes >= _options.SoftCheckpointBytes;
	protected override bool LeaseIsReleased => _lease is null || _lease.IsReleased;
	protected override string DurableAckHash => _lastAckHash;

	private protected override async ValueTask<RecoveryState> RecoverCoreAsync( CancellationToken cancellationToken )
	{
		_lease = await _storage.AcquireExclusiveLeaseAsync( _leasePath, cancellationToken );
		_writerEpoch = Guid.NewGuid();
		var recovery = await AsyncOperation.Capture( () => RecoverAfterLeaseAcquiredAsync( cancellationToken ) );
		if ( recovery.Succeeded ) return recovery.Value!;

		var release = await AsyncOperation.Capture( ReleaseLeaseAsync );
		if ( !release.Succeeded )
		{
			throw new InvalidOperationException(
				$"Persistence recovery failed ({recovery.Exception!.GetType().Name}: {recovery.Exception.Message}) " +
				$"and the exclusive lease could not be released ({release.Exception!.GetType().Name}: {release.Exception.Message}).",
				release.Exception );
		}

		throw recovery.Exception!;
	}

	private async ValueTask<RecoveryState> RecoverAfterLeaseAcquiredAsync( CancellationToken cancellationToken )
	{
		await RemoveAbandonedStagingFilesAsync( cancellationToken );
		var manifest = await EnsureFormatManifestAsync( cancellationToken );
		_storeId = manifest.StoreId;
		var checkpoint = await LoadCheckpointAsync( cancellationToken );
		var pendingPrune = await ReadPendingPruneIntentAsync( checkpoint, cancellationToken );
		var documents = new Dictionary<DocumentAddress, PersistedMutation>();
		var checkpointSequence = checkpoint.Snapshot?.Sequence ?? 0;
		var sequence = checkpointSequence;
		var generation = checkpoint.Snapshot?.CompactionGeneration ?? 0;
		var previousAckHash = checkpoint.Snapshot?.LastAckHash ?? string.Empty;
		if ( checkpoint.Snapshot is not null ) LoadCheckpointDocuments( checkpoint.Snapshot, documents );

		var acknowledgements = await ReadAcknowledgementFilesAsync( cancellationToken );
		var commitHeads = await ReadCommitHeadFilesAsync( cancellationToken );
		var acknowledgementsBySequence = acknowledgements.ToDictionary(
			entry => entry.Acknowledgement.Sequence );
		foreach ( var head in commitHeads.Values )
		{
			if ( !acknowledgementsBySequence.TryGetValue( head.Head.Sequence, out var acknowledged ) )
			{
				if ( pendingPrune is not null && head.Head.Sequence <= pendingPrune.Intent.AnchorSequence ) continue;
				throw new PersistenceCorruptionException(
					$"Durable commit head {head.Head.Sequence} is missing its acknowledgement." );
			}
			ValidateCommitHead( head, acknowledged );
		}
		var referencedFrames = new HashSet<string>( StringComparer.Ordinal );
		var verifiedFrames = new Dictionary<long, WalFrame>();
		var chainAnchor = DetermineRetainedChainAnchor( checkpoint, acknowledgements, pendingPrune?.Intent );
		var chainSequence = chainAnchor.Sequence;
		var chainHash = chainAnchor.Hash;
		var checkpointHeadVerified = checkpointSequence == chainSequence &&
			string.Equals( previousAckHash, chainHash, StringComparison.Ordinal );
		foreach ( var entry in acknowledgements )
		{
			var acknowledgement = entry.Acknowledgement;
			var frame = await ReadAndVerifyFrameAsync( acknowledgement, cancellationToken );
			referencedFrames.Add( acknowledgement.FramePath );
			verifiedFrames.Add( acknowledgement.Sequence, frame );
		}
		ValidateCoveredAcknowledgementSuffix(
			acknowledgements.Where( entry => entry.Acknowledgement.Sequence <= chainAnchor.Sequence ).ToArray(),
			verifiedFrames,
			chainAnchor,
			pendingPrune is not null );
		foreach ( var entry in acknowledgements.Where(
			entry => entry.Acknowledgement.Sequence > chainAnchor.Sequence ) )
		{
			var acknowledgement = entry.Acknowledgement;
			var frame = verifiedFrames[acknowledgement.Sequence];
			if ( acknowledgement.Sequence != checked(chainSequence + 1) ||
				acknowledgement.PreviousAckHash != chainHash || frame.PreviousAckHash != chainHash )
				throw new PersistenceCorruptionException(
					$"Retained acknowledgement hash chain failed at sequence {acknowledgement.Sequence}." );
			chainSequence = acknowledgement.Sequence;
			chainHash = entry.Hash;
			if ( acknowledgement.Sequence == checkpointSequence )
			{
				if ( !string.Equals( chainHash, previousAckHash, StringComparison.Ordinal ) )
					throw new PersistenceCorruptionException(
						$"Checkpoint {checkpointSequence} does not match the retained acknowledgement chain." );
				checkpointHeadVerified = true;
			}
		}
		if ( checkpoint.Snapshot is not null && !checkpointHeadVerified )
			throw new PersistenceCorruptionException(
				$"Checkpoint {checkpointSequence} cannot be verified against the retained acknowledgement chain." );
		foreach ( var acknowledged in acknowledgements.Where( entry =>
			!commitHeads.ContainsKey( entry.Acknowledgement.Sequence ) &&
			!(pendingPrune is not null && entry.Acknowledgement.Sequence <= pendingPrune.Intent.AnchorSequence) ) )
		{
			var head = CreateCommitHead( acknowledged.Acknowledgement, acknowledged.Path, acknowledged.Hash );
			var headBytes = JsonSerializer.SerializeToUtf8Bytes( head, _jsonOptions );
			await WriteAndVerifyImmutableAsync(
				$"{_commitHeadPrefix}/{head.Sequence:D20}-{acknowledged.Hash}.head",
				headBytes,
				cancellationToken );
		}
		foreach ( var entry in acknowledgements )
		{
			var acknowledgement = entry.Acknowledgement;
			if ( acknowledgement.Sequence <= checkpointSequence ) continue;
			var frame = verifiedFrames[acknowledgement.Sequence];
			var expectedSequence = checked(sequence + 1);
			if ( acknowledgement.Sequence != expectedSequence )
				throw new PersistenceCorruptionException(
					$"Acknowledgement sequence gap: expected {expectedSequence}, found {acknowledgement.Sequence}." );
			if ( !string.Equals( acknowledgement.PreviousAckHash, previousAckHash, StringComparison.Ordinal ) ||
				!string.Equals( frame.PreviousAckHash, previousAckHash, StringComparison.Ordinal ) )
				throw new PersistenceCorruptionException(
					$"Acknowledgement hash chain failed at sequence {acknowledgement.Sequence}." );
			if ( acknowledgement.CompactionGeneration < generation ||
				acknowledgement.CompactionGeneration > checked(generation + 1) )
				throw new PersistenceCorruptionException(
					$"WAL generation transition from {generation} to {acknowledgement.CompactionGeneration} " +
					$"is invalid at sequence {acknowledgement.Sequence}." );
			if ( acknowledgement.CompactionGeneration > generation )
			{
				// A completed checkpoint introduced this writer generation and reclaimed
				// tombstones. Fallback replay must reproduce that boundary before applying
				// revision-one recreations emitted in the new generation.
				foreach ( var tombstone in documents
					.Where( pair => pair.Value.IsDeleted )
					.Select( pair => pair.Key )
					.ToArray() )
					documents.Remove( tombstone );
				generation = acknowledgement.CompactionGeneration;
			}
			ApplyWalFrame( frame, documents );
			sequence = acknowledgement.Sequence;
			previousAckHash = entry.Hash;
		}

		var framePaths = await _storage.ListAsync( _framePrefix, cancellationToken );
		var discardedUnacknowledgedFrames = 0;
		foreach ( var orphan in framePaths.Where( path => !referencedFrames.Contains( path ) ) )
		{
			await _storage.DeleteAsync( orphan, cancellationToken );
			discardedUnacknowledgedFrames++;
		}
		if ( pendingPrune is not null )
			await ApplyPruneIntentAsync( pendingPrune, cancellationToken );

		_durableSequence = sequence;
		_durableGeneration = generation;
		_lastAckHash = previousAckHash;
		await RecalculateWalUsageAsync( cancellationToken );

		var detail = checkpoint.Detail;
		var legacyFiles = await _storage.ListAsync( LegacyRootPath, cancellationToken );
		if ( legacyFiles.Count > 0 )
		{
			var reset = $"HEXAGON_PERSISTENCE_RESET root={RootPath} legacy={LegacyRootPath} action=untouched";
			_options.Log?.Invoke( reset );
			detail = detail is null ? reset : $"{detail} {reset}";
		}

		return new RecoveryState(
			_storeId,
			_writerEpoch,
			generation,
			previousAckHash,
			sequence,
			checkpointSequence,
			documents.Values
				.OrderBy( mutation => mutation.Collection, StringComparer.Ordinal )
				.ThenBy( mutation => mutation.Key, StringComparer.Ordinal )
				.ToArray(),
			discardedUnacknowledgedFrames,
			checkpoint.RecoveredFromFallback,
			detail );
	}

	private protected override async ValueTask<CommitPersistenceOutcome> PersistCommitCoreAsync(
		WalCommitBatch batch,
		CancellationToken cancellationToken )
	{
		if ( batch.Sequence != checked(_durableSequence + 1) )
			throw new InvalidOperationException(
				$"Durable sequence mismatch: expected {_durableSequence + 1}, found {batch.Sequence}." );

		var frame = new WalFrame
		{
			FormatVersion = WalFrame.CurrentFormatVersion,
			StoreId = _storeId,
			WriterEpoch = _writerEpoch,
			CompactionGeneration = _durableGeneration,
			Sequence = batch.Sequence,
			PreviousAckHash = _lastAckHash,
			CommittedAtUtc = batch.CommittedAtUtc,
			Mutations = batch.Mutations
		};
		var frameBytes = JsonSerializer.SerializeToUtf8Bytes( frame, _jsonOptions );
		if ( frameBytes.Length > _options.MaximumFrameBytes )
			throw new PersistenceStorageLimitException(
				$"Commit frame is {frameBytes.Length} bytes; maximum is {_options.MaximumFrameBytes}." );
		var frameHash = ComputeHash( frameBytes );
		var framePath = $"{_framePrefix}/{batch.Sequence:D20}-{frameHash}.frame";
		var acknowledgement = new WalAcknowledgement
		{
			FormatVersion = WalAcknowledgement.CurrentFormatVersion,
			StoreId = _storeId,
			WriterEpoch = _writerEpoch,
			CompactionGeneration = _durableGeneration,
			Sequence = batch.Sequence,
			FramePath = framePath,
			FrameHash = frameHash,
			PreviousAckHash = _lastAckHash
		};
		var acknowledgementBytes = JsonSerializer.SerializeToUtf8Bytes( acknowledgement, _jsonOptions );
		var acknowledgementHash = ComputeHash( acknowledgementBytes );
		var acknowledgementPath = $"{_ackPrefix}/{batch.Sequence:D20}.ack";
		var commitHead = CreateCommitHead( acknowledgement, acknowledgementPath, acknowledgementHash );
		var commitHeadBytes = JsonSerializer.SerializeToUtf8Bytes( commitHead, _jsonOptions );
		var commitHeadPath = $"{_commitHeadPrefix}/{batch.Sequence:D20}-{acknowledgementHash}.head";
		var projectedBytes = checked(
			_retainedWalBytes + frameBytes.Length + acknowledgementBytes.Length + commitHeadBytes.Length);
		if ( projectedBytes > _options.MaximumRetainedWalBytes ||
			_retainedCommitCount >= _options.MaximumRetainedCommits )
			throw new PersistenceStorageLimitException(
				$"Retained WAL hard limit would be exceeded ({projectedBytes} bytes, {_retainedCommitCount + 1} commits)." );

		await WriteAndVerifyImmutableAsync( framePath, frameBytes, cancellationToken );
		await WriteAndVerifyImmutableAsync( acknowledgementPath, acknowledgementBytes, cancellationToken );

		// The verified acknowledgement is the transaction commit point. Everything below is
		// redundant recovery metadata and cannot turn a committed transaction into a failure.
		_durableSequence = batch.Sequence;
		_lastAckHash = acknowledgementHash;
		_retainedWalBytes = projectedBytes;
		_retainedCommitCount++;
		try
		{
			await WriteAndVerifyImmutableAsync( commitHeadPath, commitHeadBytes, cancellationToken );
			return CommitPersistenceOutcome.Clean;
		}
		catch ( Exception exception )
		{
			_pendingCommitHead = new PendingCommitHead( commitHeadPath, commitHeadBytes );
			_options.Log?.Invoke(
				$"HEXAGON_COMMIT_HEAD_DEGRADED sequence={batch.Sequence} " +
				$"detail={exception.GetType().Name}:{exception.Message}" );
			return CommitPersistenceOutcome.Degraded( exception );
		}
	}

	private protected override async ValueTask RepairCommitMetadataCoreAsync(
		CancellationToken cancellationToken )
	{
		var pending = _pendingCommitHead;
		if ( pending is null ) return;
		await WriteAndVerifyImmutableAsync( pending.Path, pending.Content, cancellationToken );
		_pendingCommitHead = null;
		_options.Log?.Invoke( $"HEXAGON_COMMIT_HEAD_REPAIRED sequence={_durableSequence}" );
	}

	private protected override async ValueTask<CheckpointPersistenceOutcome> PersistCheckpointCoreAsync(
		CheckpointSnapshot snapshot,
		CancellationToken cancellationToken )
	{
		if ( snapshot.StoreId != _storeId || snapshot.Sequence != _durableSequence ||
			!string.Equals( snapshot.LastAckHash, _lastAckHash, StringComparison.Ordinal ) )
			throw new InvalidOperationException( "Checkpoint snapshot does not describe the durable WAL head." );

		var blobBytes = JsonSerializer.SerializeToUtf8Bytes( snapshot, _jsonOptions );
		var blobHash = ComputeHash( blobBytes );
		var blobPath = $"{_checkpointBlobPrefix}/{blobHash}.json";
		await WriteAndVerifyImmutableAsync( blobPath, blobBytes, cancellationToken );

		var manifest = new CheckpointManifest
		{
			FormatVersion = CheckpointManifest.CurrentFormatVersion,
			SchemaId = _options.SchemaId,
			StoreId = _storeId,
			Sequence = snapshot.Sequence,
			CompactionGeneration = snapshot.CompactionGeneration,
			LastAckHash = snapshot.LastAckHash,
			BlobPath = blobPath,
			BlobHash = blobHash
		};
		var manifestBytes = JsonSerializer.SerializeToUtf8Bytes( manifest, _jsonOptions );
		var manifestHash = ComputeHash( manifestBytes );
		var generationName = $"{snapshot.Sequence:D20}-{snapshot.CompactionGeneration:D20}-{blobHash}";
		var manifestPath = $"{_checkpointManifestPrefix}/{generationName}.json";
		await WriteAndVerifyImmutableAsync( manifestPath, manifestBytes, cancellationToken );

		var completion = new CheckpointCompletion
		{
			FormatVersion = CheckpointCompletion.CurrentFormatVersion,
			Sequence = snapshot.Sequence,
			ManifestPath = manifestPath,
			ManifestHash = manifestHash
		};
		var completionBytes = JsonSerializer.SerializeToUtf8Bytes( completion, _jsonOptions );
		var completionPath = $"{_checkpointCompletionPrefix}/{generationName}.complete";
		await WriteAndVerifyImmutableAsync( completionPath, completionBytes, cancellationToken );

		_durableGeneration = snapshot.CompactionGeneration;
		try
		{
			await PruneAfterVerifiedCheckpointAsync( cancellationToken );
			return CheckpointPersistenceOutcome.Clean;
		}
		catch ( Exception exception )
		{
			// The verified completion is already the durable publication point. Pruning only
			// reclaims older immutable files, so failure must not leave the live provider on
			// the pre-compaction generation and admit stale transactions.
			_options.Log?.Invoke(
				$"HEXAGON_COMPACTION_DEGRADED sequence={snapshot.Sequence} generation={snapshot.CompactionGeneration} " +
				$"detail={exception.GetType().Name}:{exception.Message}" );
			return CheckpointPersistenceOutcome.Degraded( exception );
		}
	}

	protected override async ValueTask DisposeCoreAsync() => await ReleaseLeaseAsync();

	private async ValueTask<PersistenceFormatManifest> EnsureFormatManifestAsync( CancellationToken cancellationToken )
	{
		var existing = await _storage.ReadAsync( _formatManifestPath, cancellationToken );
		if ( existing is null )
		{
			var manifest = new PersistenceFormatManifest
			{
				Format = PersistenceFormatManifest.ExpectedFormat,
				Version = PersistenceFormatManifest.CurrentVersion,
				SchemaId = _options.SchemaId,
				StoreId = Guid.NewGuid(),
				CreatedAtUtc = DateTimeOffset.UtcNow
			};
			var bytes = JsonSerializer.SerializeToUtf8Bytes( manifest, _jsonOptions );
			await WriteAndVerifyImmutableAsync( _formatManifestPath, bytes, cancellationToken );
			existing = bytes;
		}

		var decoded = Deserialize<PersistenceFormatManifest>( existing.Value, "format manifest" );
		if ( decoded.Format != PersistenceFormatManifest.ExpectedFormat ||
			decoded.Version != PersistenceFormatManifest.CurrentVersion ||
			decoded.SchemaId != _options.SchemaId || decoded.StoreId == Guid.Empty )
			throw new PersistenceCorruptionException(
				$"Persistence root '{RootPath}' has incompatible format metadata." );
		return decoded;
	}

	private async ValueTask<IReadOnlyList<AcknowledgementEntry>> ReadAcknowledgementFilesAsync(
		CancellationToken cancellationToken )
	{
		var paths = await _storage.ListAsync( _ackPrefix, cancellationToken );
		var entries = new List<AcknowledgementEntry>( paths.Count );
		var sequences = new HashSet<long>();
		foreach ( var path in paths.OrderBy( value => value, StringComparer.Ordinal ) )
		{
			var sequence = ParseSequenceFileName( path, ".ack" );
			if ( !sequences.Add( sequence ) )
				throw new PersistenceCorruptionException( $"Duplicate acknowledgement sequence {sequence}." );
			var bytes = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"Acknowledgement '{path}' disappeared." );
			var acknowledgement = Deserialize<WalAcknowledgement>( bytes, "WAL acknowledgement" );
			if ( acknowledgement.FormatVersion != WalAcknowledgement.CurrentFormatVersion ||
				acknowledgement.StoreId != _storeId || acknowledgement.WriterEpoch == Guid.Empty ||
				acknowledgement.Sequence != sequence || acknowledgement.CompactionGeneration < 0 )
				throw new PersistenceCorruptionException( $"Acknowledgement '{path}' has invalid metadata." );
			entries.Add( new AcknowledgementEntry( path, acknowledgement, ComputeHash( bytes.Span ), bytes.Length ) );
		}
		return entries.OrderBy( entry => entry.Acknowledgement.Sequence ).ToArray();
	}

	private async ValueTask<IReadOnlyDictionary<long, CommitHeadEntry>> ReadCommitHeadFilesAsync(
		CancellationToken cancellationToken )
	{
		var result = new Dictionary<long, CommitHeadEntry>();
		foreach ( var path in await _storage.ListAsync( _commitHeadPrefix, cancellationToken ) )
		{
			var sequence = ParseSequenceFileName( path, ".head", hasHashSuffix: true );
			var bytes = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"Commit head '{path}' disappeared." );
			var head = Deserialize<WalCommitHead>( bytes, "WAL commit head" );
			var expectedPath = $"{_commitHeadPrefix}/{sequence:D20}-{head.AcknowledgementHash}.head";
			if ( head.FormatVersion != WalCommitHead.CurrentFormatVersion || head.StoreId != _storeId ||
				head.WriterEpoch == Guid.Empty || head.Sequence != sequence ||
				head.CompactionGeneration < 0 || !string.Equals( path, expectedPath, StringComparison.Ordinal ) ||
				!result.TryAdd( sequence, new CommitHeadEntry( path, head ) ) )
				throw new PersistenceCorruptionException( $"Commit head '{path}' has invalid metadata." );
		}
		return result;
	}

	private static WalCommitHead CreateCommitHead(
		WalAcknowledgement acknowledgement,
		string acknowledgementPath,
		string acknowledgementHash ) => new()
		{
			FormatVersion = WalCommitHead.CurrentFormatVersion,
			StoreId = acknowledgement.StoreId,
			WriterEpoch = acknowledgement.WriterEpoch,
			CompactionGeneration = acknowledgement.CompactionGeneration,
			Sequence = acknowledgement.Sequence,
			AcknowledgementPath = acknowledgementPath,
			AcknowledgementHash = acknowledgementHash,
			FramePath = acknowledgement.FramePath,
			FrameHash = acknowledgement.FrameHash
		};

	private static void ValidateCommitHead( CommitHeadEntry entry, AcknowledgementEntry acknowledged )
	{
		var head = entry.Head;
		var acknowledgement = acknowledged.Acknowledgement;
		if ( head.StoreId != acknowledgement.StoreId || head.WriterEpoch != acknowledgement.WriterEpoch ||
			head.CompactionGeneration != acknowledgement.CompactionGeneration || head.Sequence != acknowledgement.Sequence ||
			head.AcknowledgementPath != acknowledged.Path || head.AcknowledgementHash != acknowledged.Hash ||
			head.FramePath != acknowledgement.FramePath || head.FrameHash != acknowledgement.FrameHash )
			throw new PersistenceCorruptionException(
				$"Commit head {head.Sequence} does not match its acknowledgement." );
	}

	private async ValueTask<WalFrame> ReadAndVerifyFrameAsync(
		WalAcknowledgement acknowledgement,
		CancellationToken cancellationToken )
	{
		EnsurePathUnder( acknowledgement.FramePath, _framePrefix );
		var expectedFramePath = $"{_framePrefix}/{acknowledgement.Sequence:D20}-{acknowledgement.FrameHash}.frame";
		if ( !string.Equals( acknowledgement.FramePath, expectedFramePath, StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException(
				$"Acknowledgement {acknowledgement.Sequence} names frame '{acknowledgement.FramePath}', expected '{expectedFramePath}'." );
		var bytes = await _storage.ReadAsync( acknowledgement.FramePath, cancellationToken )
			?? throw new PersistenceCorruptionException(
				$"Acknowledged frame '{acknowledgement.FramePath}' is missing." );
		if ( bytes.Length > _options.MaximumFrameBytes ||
			!string.Equals( ComputeHash( bytes.Span ), acknowledgement.FrameHash, StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException(
				$"Acknowledged frame '{acknowledgement.FramePath}' failed verification." );
		var frame = Deserialize<WalFrame>( bytes, "WAL frame" );
		if ( frame.FormatVersion != WalFrame.CurrentFormatVersion || frame.StoreId != acknowledgement.StoreId ||
			frame.WriterEpoch != acknowledgement.WriterEpoch || frame.CompactionGeneration != acknowledgement.CompactionGeneration ||
			frame.Sequence != acknowledgement.Sequence || frame.PreviousAckHash != acknowledgement.PreviousAckHash ||
			frame.Mutations is null )
			throw new PersistenceCorruptionException(
				$"Acknowledged frame '{acknowledgement.FramePath}' metadata does not match its acknowledgement." );
		return frame;
	}

	private async ValueTask<CheckpointLoad> LoadCheckpointAsync( CancellationToken cancellationToken )
	{
		var completionPaths = await _storage.ListAsync( _checkpointCompletionPrefix, cancellationToken );
		if ( completionPaths.Count == 0 )
			return new CheckpointLoad( null, false, null, Array.Empty<CheckpointSnapshot>() );
		var valid = new List<CheckpointCandidate>();
		var failures = new List<string>();
		foreach ( var path in completionPaths )
		{
			var capture = await AsyncOperation.Capture( () => ReadCheckpointAsync( path, cancellationToken ) );
			if ( capture.Succeeded ) valid.Add( capture.Value! );
			else if ( capture.Exception is PersistenceCorruptionException or JsonException or NotSupportedException )
				failures.Add( $"{path}: {capture.Exception.Message}" );
			else throw new InvalidOperationException( $"Reading checkpoint '{path}' failed.", capture.Exception );
		}
		if ( valid.Count == 0 )
			return new CheckpointLoad( null, true,
				$"No valid checkpoint generation was available; replaying acknowledged WAL. {string.Join( " | ", failures )}",
				Array.Empty<CheckpointSnapshot>() );
		var selected = valid.OrderByDescending( value => value.Snapshot.Sequence )
			.ThenByDescending( value => value.Snapshot.CompactionGeneration ).First();
		return new CheckpointLoad(
			selected.Snapshot,
			failures.Count > 0,
			failures.Count > 0
				? $"Recovered checkpoint {selected.Snapshot.Sequence} after invalid generation(s): {string.Join( " | ", failures )}"
				: null,
			valid.Select( value => value.Snapshot ).ToArray() );
	}

	private static ChainAnchor DetermineRetainedChainAnchor(
		CheckpointLoad checkpoint,
		IReadOnlyList<AcknowledgementEntry> acknowledgements,
		CheckpointPruneIntent? pendingPrune )
	{
		if ( pendingPrune is not null )
			return new ChainAnchor( pendingPrune.AnchorSequence, pendingPrune.AnchorAckHash );
		if ( acknowledgements.Count == 0 )
		{
			if ( checkpoint.Snapshot is null || checkpoint.Snapshot.Sequence == 0 )
				return new ChainAnchor( 0, string.Empty );
			throw new PersistenceCorruptionException(
				$"Checkpoint {checkpoint.Snapshot.Sequence} has no retained acknowledgement chain." );
		}

		var first = acknowledgements[0].Acknowledgement;
		if ( first.Sequence == 1 ) return new ChainAnchor( 0, string.Empty );
		var expectedAnchorSequence = first.Sequence - 1;
		var anchors = checkpoint.ValidSnapshots
			.Where( snapshot => snapshot.Sequence == expectedAnchorSequence )
			.Select( snapshot => snapshot.LastAckHash )
			.Distinct( StringComparer.Ordinal )
			.ToArray();
		if ( anchors.Length != 1 )
			throw new PersistenceCorruptionException(
				$"Retained WAL begins at sequence {first.Sequence} without one unambiguous checkpoint at sequence {expectedAnchorSequence}." );
		if ( !string.Equals( first.PreviousAckHash, anchors[0], StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException(
				$"Retained WAL sequence {first.Sequence} does not continue checkpoint {expectedAnchorSequence}." );
		return new ChainAnchor( expectedAnchorSequence, anchors[0] );
	}

	private static void ValidateCoveredAcknowledgementSuffix(
		IReadOnlyList<AcknowledgementEntry> covered,
		IReadOnlyDictionary<long, WalFrame> frames,
		ChainAnchor anchor,
		bool partialPruneAuthorized )
	{
		if ( covered.Count == 0 ) return;
		if ( !partialPruneAuthorized )
			throw new PersistenceCorruptionException(
				$"Retained acknowledgement sequence {covered[0].Acknowledgement.Sequence} precedes chain anchor {anchor.Sequence}." );
		var ordered = covered.OrderBy( entry => entry.Acknowledgement.Sequence ).ToArray();
		if ( ordered[^1].Acknowledgement.Sequence != anchor.Sequence ||
			!string.Equals( ordered[^1].Hash, anchor.Hash, StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException(
				$"Partially pruned acknowledgement suffix does not terminate at checkpoint {anchor.Sequence}." );
		for ( var index = 1; index < ordered.Length; index++ )
		{
			var previous = ordered[index - 1];
			var current = ordered[index];
			var frame = frames[current.Acknowledgement.Sequence];
			if ( current.Acknowledgement.Sequence != checked(previous.Acknowledgement.Sequence + 1) ||
				current.Acknowledgement.PreviousAckHash != previous.Hash ||
				frame.PreviousAckHash != previous.Hash )
				throw new PersistenceCorruptionException(
					$"Partially pruned acknowledgement suffix has a gap at sequence {current.Acknowledgement.Sequence}." );
		}
	}

	private async ValueTask<PendingPruneIntent?> ReadPendingPruneIntentAsync(
		CheckpointLoad checkpoint,
		CancellationToken cancellationToken )
	{
		var paths = await _storage.ListAsync( _pruneIntentPrefix, cancellationToken );
		if ( paths.Count == 0 ) return null;
		if ( paths.Count != 1 )
			throw new PersistenceCorruptionException( "More than one checkpoint prune intent is pending." );
		var bytes = await _storage.ReadAsync( paths[0], cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint prune intent '{paths[0]}' disappeared." );
		var intent = Deserialize<CheckpointPruneIntent>( bytes, "checkpoint prune intent" );
		if ( intent.FormatVersion != CheckpointPruneIntent.CurrentFormatVersion ||
			intent.StoreId != _storeId || intent.AnchorSequence <= 0 || string.IsNullOrWhiteSpace( intent.AnchorAckHash ) ||
			intent.ObsoleteCompletions is null || intent.ObsoleteManifests is null || intent.ObsoleteBlobs is null )
			throw new PersistenceCorruptionException( $"Checkpoint prune intent '{paths[0]}' has invalid metadata." );
		var matchingAnchors = checkpoint.ValidSnapshots.Where( snapshot =>
			snapshot.Sequence == intent.AnchorSequence && snapshot.LastAckHash == intent.AnchorAckHash ).ToArray();
		if ( matchingAnchors.Length == 0 )
			throw new PersistenceCorruptionException(
				$"Checkpoint prune intent '{paths[0]}' has no valid anchor checkpoint." );
		foreach ( var path in intent.ObsoleteCompletions ) EnsurePathUnder( path, _checkpointCompletionPrefix );
		foreach ( var path in intent.ObsoleteManifests ) EnsurePathUnder( path, _checkpointManifestPrefix );
		foreach ( var path in intent.ObsoleteBlobs ) EnsurePathUnder( path, _checkpointBlobPrefix );
		return new PendingPruneIntent( paths[0], intent );
	}

	private async ValueTask<CheckpointCandidate> ReadCheckpointAsync(
		string completionPath,
		CancellationToken cancellationToken )
	{
		var completionBytes = await _storage.ReadAsync( completionPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint completion '{completionPath}' is missing." );
		var completion = Deserialize<CheckpointCompletion>( completionBytes, "checkpoint completion" );
		if ( completion.FormatVersion != CheckpointCompletion.CurrentFormatVersion || completion.Sequence < 0 )
			throw new PersistenceCorruptionException( $"Checkpoint completion '{completionPath}' has invalid metadata." );
		EnsurePathUnder( completion.ManifestPath, _checkpointManifestPrefix );
		var manifestBytes = await _storage.ReadAsync( completion.ManifestPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' is missing." );
		if ( ComputeHash( manifestBytes.Span ) != completion.ManifestHash )
			throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' failed verification." );
		var manifest = Deserialize<CheckpointManifest>( manifestBytes, "checkpoint manifest" );
		if ( manifest.FormatVersion != CheckpointManifest.CurrentFormatVersion || manifest.SchemaId != _options.SchemaId ||
			manifest.StoreId != _storeId || manifest.Sequence != completion.Sequence ||
			manifest.CompactionGeneration < 0 )
			throw new PersistenceCorruptionException( $"Checkpoint manifest '{completion.ManifestPath}' has invalid metadata." );
		EnsurePathUnder( manifest.BlobPath, _checkpointBlobPrefix );
		var blobBytes = await _storage.ReadAsync( manifest.BlobPath, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Checkpoint blob '{manifest.BlobPath}' is missing." );
		if ( ComputeHash( blobBytes.Span ) != manifest.BlobHash )
			throw new PersistenceCorruptionException( $"Checkpoint blob '{manifest.BlobPath}' failed verification." );
		var snapshot = Deserialize<CheckpointSnapshot>( blobBytes, "checkpoint snapshot" );
		if ( snapshot.FormatVersion != CheckpointSnapshot.CurrentFormatVersion || snapshot.StoreId != _storeId ||
			snapshot.Sequence != manifest.Sequence || snapshot.CompactionGeneration != manifest.CompactionGeneration ||
			snapshot.LastAckHash != manifest.LastAckHash || snapshot.Documents is null )
			throw new PersistenceCorruptionException( $"Checkpoint snapshot '{manifest.BlobPath}' has invalid metadata." );
		return new CheckpointCandidate( completionPath, completion.ManifestPath, manifest.BlobPath, snapshot );
	}

	private static void LoadCheckpointDocuments(
		CheckpointSnapshot snapshot,
		IDictionary<DocumentAddress, PersistedMutation> documents )
	{
		foreach ( var mutation in snapshot.Documents )
		{
			ValidateMutationShape( mutation );
			if ( mutation.IsDeleted )
				throw new PersistenceCorruptionException( "Compacted checkpoint contains a tombstone." );
			if ( mutation.Revision > snapshot.Sequence )
				throw new PersistenceCorruptionException(
					$"Checkpoint revision for '{mutation.Address}' exceeds its sequence." );
			if ( !documents.TryAdd( mutation.Address, mutation ) )
				throw new PersistenceCorruptionException( $"Checkpoint duplicates '{mutation.Address}'." );
		}
	}

	private static void ApplyWalFrame( WalFrame frame, IDictionary<DocumentAddress, PersistedMutation> documents )
	{
		var seen = new HashSet<DocumentAddress>();
		foreach ( var mutation in frame.Mutations )
		{
			ValidateMutationShape( mutation );
			if ( !seen.Add( mutation.Address ) )
				throw new PersistenceCorruptionException(
					$"WAL sequence {frame.Sequence} mutates '{mutation.Address}' twice." );
			documents.TryGetValue( mutation.Address, out var current );
			var expectedRevision = checked((current?.Revision ?? 0) + 1);
			if ( mutation.Revision != expectedRevision )
				throw new PersistenceCorruptionException(
					$"WAL sequence {frame.Sequence} gives '{mutation.Address}' revision {mutation.Revision}; expected {expectedRevision}." );
			documents[mutation.Address] = mutation;
		}
	}

	private static void ValidateMutationShape( PersistedMutation mutation )
	{
		if ( mutation.EnvelopeVersion != PersistedDocumentEnvelope.CurrentEnvelopeVersion || mutation.Revision <= 0 )
			throw new PersistenceCorruptionException( "Persisted mutation has incompatible envelope metadata." );
		_ = mutation.Address;
		_ = new PersistedTypeKey( mutation.PersistedType );
		if ( mutation.TypeVersion <= 0 || mutation.IsDeleted == mutation.Payload.HasValue )
			throw new PersistenceCorruptionException( $"Persisted mutation '{mutation.Address}' has invalid shape." );
	}

	private async ValueTask PruneAfterVerifiedCheckpointAsync( CancellationToken cancellationToken )
	{
		var loaded = await LoadCheckpointAsync( cancellationToken );
		var pending = await ReadPendingPruneIntentAsync( loaded, cancellationToken );
		if ( pending is not null ) await ApplyPruneIntentAsync( pending, cancellationToken );

		var completionPaths = await _storage.ListAsync( _checkpointCompletionPrefix, cancellationToken );
		var valid = new List<CheckpointCandidate>();
		foreach ( var path in completionPaths )
		{
			var capture = await AsyncOperation.Capture( () => ReadCheckpointAsync( path, cancellationToken ) );
			if ( capture.Succeeded ) valid.Add( capture.Value! );
		}
		var retained = valid.OrderByDescending( value => value.Snapshot.Sequence )
			.ThenByDescending( value => value.Snapshot.CompactionGeneration )
			.Take( _options.RetainedCheckpointGenerations ).ToArray();
		var retainedCompletions = retained.Select( value => value.CompletionPath ).ToHashSet( StringComparer.Ordinal );
		var retainedManifests = retained.Select( value => value.ManifestPath ).ToHashSet( StringComparer.Ordinal );
		var retainedBlobs = retained.Select( value => value.BlobPath ).ToHashSet( StringComparer.Ordinal );
		var pruneThroughSequence = retained.Length >= 2 ? retained.Min( value => value.Snapshot.Sequence ) : 0;
		if ( pruneThroughSequence <= 0 )
		{
			await RecalculateWalUsageAsync( cancellationToken );
			return;
		}
		var anchor = retained
			.Where( value => value.Snapshot.Sequence == pruneThroughSequence )
			.OrderByDescending( value => value.Snapshot.CompactionGeneration )
			.First().Snapshot;
		var intent = new CheckpointPruneIntent
		{
			FormatVersion = CheckpointPruneIntent.CurrentFormatVersion,
			StoreId = _storeId,
			AnchorSequence = anchor.Sequence,
			AnchorAckHash = anchor.LastAckHash,
			ObsoleteCompletions = completionPaths.Where( path => !retainedCompletions.Contains( path ) )
				.OrderBy( path => path, StringComparer.Ordinal ).ToArray(),
			ObsoleteManifests = (await _storage.ListAsync( _checkpointManifestPrefix, cancellationToken ))
				.Where( path => !retainedManifests.Contains( path ) )
				.OrderBy( path => path, StringComparer.Ordinal ).ToArray(),
			ObsoleteBlobs = (await _storage.ListAsync( _checkpointBlobPrefix, cancellationToken ))
				.Where( path => !retainedBlobs.Contains( path ) )
				.OrderBy( path => path, StringComparer.Ordinal ).ToArray()
		};
		var intentBytes = JsonSerializer.SerializeToUtf8Bytes( intent, _jsonOptions );
		var intentPath = $"{_pruneIntentPrefix}/{intent.AnchorSequence:D20}-{ComputeHash( intentBytes )}.intent";
		await WriteAndVerifyImmutableAsync( intentPath, intentBytes, cancellationToken );
		await ApplyPruneIntentAsync( new PendingPruneIntent( intentPath, intent ), cancellationToken );
	}

	private async ValueTask ApplyPruneIntentAsync(
		PendingPruneIntent pending,
		CancellationToken cancellationToken )
	{
		var intent = pending.Intent;
		foreach ( var path in (await _storage.ListAsync( _commitHeadPrefix, cancellationToken ))
			.Select( path => (Path: path, Sequence: ParseSequenceFileName( path, ".head", hasHashSuffix: true )) )
			.Where( entry => entry.Sequence <= intent.AnchorSequence )
			.OrderBy( entry => entry.Sequence ) )
			await _storage.DeleteAsync( path.Path, cancellationToken );
		foreach ( var path in (await _storage.ListAsync( _ackPrefix, cancellationToken ))
			.Select( path => (Path: path, Sequence: ParseSequenceFileName( path, ".ack" )) )
			.Where( entry => entry.Sequence <= intent.AnchorSequence )
			.OrderBy( entry => entry.Sequence ) )
			await _storage.DeleteAsync( path.Path, cancellationToken );
		foreach ( var path in (await _storage.ListAsync( _framePrefix, cancellationToken ))
			.Select( path => (Path: path, Sequence: ParseSequenceFileName( path, ".frame", hasHashSuffix: true )) )
			.Where( entry => entry.Sequence <= intent.AnchorSequence )
			.OrderBy( entry => entry.Sequence ) )
			await _storage.DeleteAsync( path.Path, cancellationToken );
		foreach ( var path in intent.ObsoleteCompletions ) await _storage.DeleteAsync( path, cancellationToken );
		foreach ( var path in intent.ObsoleteManifests ) await _storage.DeleteAsync( path, cancellationToken );
		foreach ( var path in intent.ObsoleteBlobs ) await _storage.DeleteAsync( path, cancellationToken );
		await RecalculateWalUsageAsync( cancellationToken );
		await _storage.DeleteAsync( pending.Path, cancellationToken );
	}

	private async ValueTask RecalculateWalUsageAsync( CancellationToken cancellationToken )
	{
		long bytes = 0;
		var count = 0;
		foreach ( var path in await _storage.ListAsync( _ackPrefix, cancellationToken ) )
		{
			var content = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"WAL acknowledgement '{path}' disappeared." );
			bytes = checked(bytes + content.Length);
			count++;
		}
		foreach ( var path in await _storage.ListAsync( _framePrefix, cancellationToken ) )
		{
			var content = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"WAL frame '{path}' disappeared." );
			bytes = checked(bytes + content.Length);
		}
		foreach ( var path in await _storage.ListAsync( _commitHeadPrefix, cancellationToken ) )
		{
			var content = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"WAL commit head '{path}' disappeared." );
			bytes = checked(bytes + content.Length);
		}
		_retainedWalBytes = bytes;
		_retainedCommitCount = count;
	}

	private async ValueTask RemoveAbandonedStagingFilesAsync( CancellationToken cancellationToken )
	{
		foreach ( var path in await _storage.ListAsync( RootPath, cancellationToken ) )
			if ( path.EndsWith( ".staging", StringComparison.Ordinal ) )
				await _storage.DeleteAsync( path, cancellationToken );
	}

	private async ValueTask WriteAndVerifyImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken )
	{
		if ( !await _storage.TryWriteImmutableAsync( path, content, cancellationToken ) )
		{
			var collision = await _storage.ReadAsync( path, cancellationToken )
				?? throw new PersistenceCorruptionException( $"Immutable path '{path}' exists but cannot be read." );
			if ( !collision.Span.SequenceEqual( content.Span ) )
				throw new PersistenceCorruptionException( $"Immutable path collision at '{path}'." );
		}
		var verified = await _storage.ReadAsync( path, cancellationToken )
			?? throw new PersistenceCorruptionException( $"Immutable path '{path}' disappeared after write." );
		if ( !verified.Span.SequenceEqual( content.Span ) )
			throw new PersistenceCorruptionException( $"Immutable path '{path}' failed read-after-write verification." );
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
		foreach ( var value in hash ) builder.Append( value.ToString( "x2", CultureInfo.InvariantCulture ) );
		return builder.ToString();
	}

	private static long ParseSequenceFileName( string path, string extension, bool hasHashSuffix = false )
	{
		var name = path[(path.LastIndexOf( '/' ) + 1)..];
		if ( !name.EndsWith( extension, StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException( $"Unexpected WAL file '{path}'." );
		var stem = name[..^extension.Length];
		if ( hasHashSuffix )
		{
			var separator = stem.IndexOf( '-', StringComparison.Ordinal );
			if ( separator != 20 ) throw new PersistenceCorruptionException( $"Invalid WAL frame name '{path}'." );
			stem = stem[..separator];
		}
		if ( stem.Length != 20 || !long.TryParse( stem, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence ) || sequence <= 0 )
			throw new PersistenceCorruptionException( $"Invalid WAL sequence file '{path}'." );
		return sequence;
	}

	private void EnsurePathUnder( string path, string prefix )
	{
		var invalidSegment = !string.IsNullOrWhiteSpace( path ) &&
			path.Split( '/' ).Any( segment => segment is "" or "." or ".." );
		if ( string.IsNullOrWhiteSpace( path ) || path.Contains( '\\' ) || invalidSegment ||
			!path.StartsWith( prefix + "/", StringComparison.Ordinal ) )
			throw new PersistenceCorruptionException( $"Persistence path '{path}' escapes '{prefix}'." );
	}

	private async ValueTask ReleaseLeaseAsync()
	{
		var lease = Interlocked.CompareExchange( ref _lease, null, null );
		if ( lease is null ) return;
		try
		{
			await lease.DisposeAsync();
		}
		finally
		{
			if ( lease.IsReleased ) Interlocked.CompareExchange( ref _lease, null, lease );
		}
		if ( !lease.IsReleased )
			throw new InvalidOperationException( "Persistence lease disposal completed without releasing ownership." );
	}

	private string Path( string suffix ) => $"{RootPath}/{suffix}";

	private sealed record AcknowledgementEntry(
		string Path,
		WalAcknowledgement Acknowledgement,
		string Hash,
		int Length );
	private sealed record CommitHeadEntry( string Path, WalCommitHead Head );
	private sealed record CheckpointCandidate(
		string CompletionPath,
		string ManifestPath,
		string BlobPath,
		CheckpointSnapshot Snapshot );
	private sealed record CheckpointLoad(
		CheckpointSnapshot? Snapshot,
		bool RecoveredFromFallback,
		string? Detail,
		IReadOnlyList<CheckpointSnapshot> ValidSnapshots );
	private sealed record PendingPruneIntent( string Path, CheckpointPruneIntent Intent );
	private sealed record PendingCommitHead( string Path, ReadOnlyMemory<byte> Content );
	private readonly record struct ChainAnchor( long Sequence, string Hash );
}
