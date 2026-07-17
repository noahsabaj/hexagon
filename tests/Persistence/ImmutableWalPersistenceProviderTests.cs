using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Persistence;

[TestClass]
public sealed class ImmutableWalPersistenceProviderTests
{
	private const string Root = "hexagon/persistence/v3/test-schema";
	private const string Frames = Root + "/wal/frames";
	private const string Acks = Root + "/wal/acks";
	private const string CommitHeads = Root + "/wal/commit-heads";
	private const string Completions = Root + "/checkpoints/complete";
	private const string LeaseProbeRole = "HEXAGON_LEASE_PROBE_ROLE";
	private const string LeaseProbeRoot = "HEXAGON_LEASE_PROBE_ROOT";
	private const string LeaseProbeSignal = "HEXAGON_LEASE_PROBE_SIGNAL";

	[TestMethod]
	[Timeout( 60_000, CooperativeCancellation = true )]
	public async Task TwoChildProcessesEnforceLeaseAndCrashRelease()
	{
		using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 50 ) );
		var cancellationToken = timeout.Token;
		var role = Environment.GetEnvironmentVariable( LeaseProbeRole );
		if ( role is not null )
		{
			await RunLeaseProbeChildAsync( role, cancellationToken );
			return;
		}

		var physicalRoot = Path.GetFullPath( Path.Combine(
			Path.GetTempPath(), "hexagon-v3-lease-tests", Guid.NewGuid().ToString( "N" ) ) );
		var expectedParent = Path.GetFullPath( Path.Combine( Path.GetTempPath(), "hexagon-v3-lease-tests" ) )
			.TrimEnd( Path.DirectorySeparatorChar ) + Path.DirectorySeparatorChar;
		Assert.IsTrue( physicalRoot.StartsWith( expectedParent, StringComparison.OrdinalIgnoreCase ) );
		Directory.CreateDirectory( physicalRoot );
		var signal = Path.Combine( physicalRoot, "holder.ready" );
		Process? holder = null;
		try
		{
			holder = StartLeaseProbe( "holder", physicalRoot, signal );
			await WaitForFileAsync( signal, holder, cancellationToken );

			using ( var contender = StartLeaseProbe( "contender", physicalRoot, signal ) )
			{
				await contender.WaitForExitAsync( cancellationToken );
				var output = await contender.StandardOutput.ReadToEndAsync( cancellationToken );
				var error = await contender.StandardError.ReadToEndAsync( cancellationToken );
				Assert.AreEqual( 0, contender.ExitCode, output + error );
			}
			var physicalStorage = new PhysicalFilePersistenceStorage( physicalRoot );
			Assert.HasCount( 1, await physicalStorage.ListAsync( Frames, cancellationToken ) );
			Assert.HasCount( 1, await physicalStorage.ListAsync( Acks, cancellationToken ) );

			holder.Kill( entireProcessTree: true );
			await holder.WaitForExitAsync( cancellationToken );
			holder.Dispose();
			holder = null;

			using var successor = StartLeaseProbe( "successor", physicalRoot, signal );
			await successor.WaitForExitAsync( cancellationToken );
			var successorOutput = await successor.StandardOutput.ReadToEndAsync( cancellationToken );
			var successorError = await successor.StandardError.ReadToEndAsync( cancellationToken );
			Assert.AreEqual( 0, successor.ExitCode, successorOutput + successorError );
			Assert.HasCount( 2, await physicalStorage.ListAsync( Frames, cancellationToken ) );
			Assert.HasCount( 2, await physicalStorage.ListAsync( Acks, cancellationToken ) );
		}
		finally
		{
			if ( holder is not null )
			{
				if ( !holder.HasExited ) holder.Kill( entireProcessTree: true );
				holder.Dispose();
			}
			if ( Directory.Exists( physicalRoot ) ) Directory.Delete( physicalRoot, recursive: true );
		}
	}

	[TestMethod]
	public async Task LifetimeLeaseRejectsSecondWriterAndShutdownReleasesIt()
	{
		var storage = new InMemoryPersistenceStorage();
		await using var first = Create( storage );
		await first.InitializeAsync();

		await using var second = Create( storage );
		await Assert.ThrowsAsync<PersistenceLeaseUnavailableException>( async () => await second.InitializeAsync() );
		Assert.AreEqual( 0L, first.Health.Sequence );

		var shutdown = await first.ShutdownAsync();
		Assert.IsTrue( shutdown.IsClean );
		Assert.IsTrue( shutdown.LeaseReleased );

		await using var successor = Create( storage );
		await successor.InitializeAsync();
		Assert.AreEqual( first.StoreId, successor.StoreId );
		Assert.AreNotEqual( first.WriterEpoch, successor.WriterEpoch );
	}

	[TestMethod]
	public async Task FailedLeaseDisposalRetainsOwnershipAndASecondShutdownRetriesRelease()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		storage.FailNextLeaseDispose = true;

		var failed = await provider.ShutdownAsync();

		Assert.IsFalse( failed.LeaseReleased );
		Assert.IsFalse( failed.IsClean );
		await using ( var blocked = Create( storage ) )
		{
			await Assert.ThrowsAsync<PersistenceLeaseUnavailableException>(
				async () => await blocked.InitializeAsync() );
		}

		var retried = await provider.ShutdownAsync();
		Assert.IsTrue( retried.LeaseReleased );
		Assert.IsTrue( retried.IsClean );
		Assert.AreSame( retried, await provider.ShutdownAsync() );

		await using var successor = Create( storage );
		await successor.InitializeAsync();
	}

	[TestMethod]
	public async Task CommitPublishesOnlyAfterVerifiedFrameAndAcknowledgement()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );

		storage.FailNextImmutableWrite = path => path.Contains( "/wal/acks/", StringComparison.Ordinal );
		await using var unit = provider.BeginUnitOfWork();
		unit.Create( repository, "one", new TestDocument( "one", 1 ) );
		var result = await unit.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( 0L, provider.Health.Sequence );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, provider.Health.Status );
		Assert.HasCount( 1, await storage.ListAsync( Frames ) );
		Assert.HasCount( 0, await storage.ListAsync( Acks ) );
	}

	[TestMethod]
	public async Task NonIdempotentPublicationPreparationRejectsCommitBeforeWalOrSequence()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = CreateWithRegistry( storage, CreateIncrementingScoreRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using var unit = provider.BeginUnitOfWork();
		unit.Create( repository, "one", new TestDocument( "one", 1 ) );

		var result = await unit.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.SerializationFailed, result.Error!.Code );
		StringAssert.Contains( result.Error.Message, "not idempotent" );
		Assert.AreEqual( 0L, provider.Health.Sequence );
		Assert.AreEqual( PersistenceProviderState.Ready, provider.State );
		Assert.AreEqual( PersistenceHealthStatus.Healthy, provider.Health.Status );
		Assert.IsNull( repository.Find( "one" ) );
		Assert.IsEmpty( await storage.ListAsync( Frames ) );
		Assert.IsEmpty( await storage.ListAsync( Acks ) );
		Assert.IsEmpty( await storage.ListAsync( CommitHeads ) );
	}

	[TestMethod]
	public async Task CommitHeadWriteFailureReturnsCommittedDegradedAndRepairsBeforeNextCommit()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );

		storage.FailNextImmutableWrite = path => path.Contains( "/wal/commit-heads/", StringComparison.Ordinal );
		await using ( var first = provider.BeginUnitOfWork() )
		{
			first.Create( repository, "one", new TestDocument( "one", 1 ) );
			var committed = await first.CommitAsync();
			Assert.IsTrue( committed.Succeeded );
			Assert.AreEqual( 1L, committed.Value!.Sequence );
		}

		Assert.AreEqual( 1, repository.Find( "one" )!.Value.Score );
		Assert.AreEqual( PersistenceHealthStatus.Degraded, provider.Health.Status );
		Assert.IsTrue( provider.Health.CommitMetadataRepairPending );
		Assert.HasCount( 1, await storage.ListAsync( Acks ) );
		Assert.HasCount( 0, await storage.ListAsync( CommitHeads ) );

		await using ( var second = provider.BeginUnitOfWork() )
		{
			second.Put( repository, "two", new TestDocument( "two", 2 ) );
			var committed = await second.CommitAsync();
			Assert.IsTrue( committed.Succeeded );
			Assert.AreEqual( 2L, committed.Value!.Sequence );
		}

		Assert.AreEqual( PersistenceHealthStatus.Healthy, provider.Health.Status );
		Assert.IsFalse( provider.Health.CommitMetadataRepairPending );
		Assert.HasCount( 2, await storage.ListAsync( CommitHeads ) );
		Assert.AreEqual( 1L, repository.Find( "one" )!.Revision.Value );
	}

	[TestMethod]
	public async Task CommitHeadRereadFailureReturnsCommittedAndShutdownRepairsIt()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );

		storage.FailNextRead = path => path.Contains( "/wal/commit-heads/", StringComparison.Ordinal );
		await using ( var unit = provider.BeginUnitOfWork() )
		{
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			var committed = await unit.CommitAsync();
			Assert.IsTrue( committed.Succeeded );
		}

		Assert.AreEqual( PersistenceHealthStatus.Degraded, provider.Health.Status );
		Assert.IsTrue( provider.Health.CommitMetadataRepairPending );
		Assert.HasCount( 1, await storage.ListAsync( CommitHeads ) );

		var shutdown = await provider.ShutdownAsync();
		Assert.IsTrue( shutdown.IsClean );
		Assert.IsTrue( shutdown.IsRecoverable );
		Assert.AreEqual( 1L, shutdown.DurableSequence );

		await using var recovered = Create( storage );
		await recovered.InitializeAsync();
		Assert.AreEqual( 1, recovered.Repository<TestDocument>( "documents" ).Find( "one" )!.Value.Score );
	}

	[TestMethod]
	public async Task RecoveryDeletesOnlyUnacknowledgedOrphanFrames()
	{
		var storage = new FaultInjectingStorage();
		await using ( var first = Create( storage ) )
		{
			await first.InitializeAsync();
			var repository = first.Repository<TestDocument>( "documents" );
			await using var unit = first.BeginUnitOfWork();
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
			Assert.IsTrue( (await first.ShutdownAsync()).IsClean );
		}

		var orphan = Frames + "/00000000000000000002-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.frame";
		Assert.IsTrue( await storage.TryWriteImmutableAsync( orphan, new byte[] { 1, 2, 3 } ) );
		await using var recovered = Create( storage );
		await recovered.InitializeAsync();

		Assert.IsFalse( await storage.ExistsAsync( orphan ) );
		Assert.IsNotNull( recovered.Repository<TestDocument>( "documents" ).Find( "one" ) );
	}

	[TestMethod]
	public async Task NonIdempotentPublicationPreparationFaultsRecoveryAndReleasesLeaseBeforeShutdown()
	{
		var storage = new FaultInjectingStorage();
		await using ( var first = Create( storage ) )
		{
			await first.InitializeAsync();
			var repository = first.Repository<TestDocument>( "documents" );
			await using var unit = first.BeginUnitOfWork();
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
			Assert.IsTrue( (await first.ShutdownAsync()).IsClean );
		}

		await using var recovered = CreateWithRegistry( storage, CreateIncrementingScoreRegistry() );
		var exception = await Assert.ThrowsAsync<PersistenceCorruptionException>( async () =>
			await recovered.InitializeAsync() );

		StringAssert.Contains( exception.ToString(), "not idempotent" );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
		Assert.IsFalse( recovered.IsInitialized );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, recovered.Health.Status );
		Assert.AreEqual( 0L, recovered.Health.Sequence );
		Assert.Throws<InvalidOperationException>( () => recovered.Repository<TestDocument>( "documents" ) );

		await using var successor = Create( storage );
		await successor.InitializeAsync();
		Assert.AreEqual( PersistenceProviderState.Ready, successor.State,
			"A CompleteInitialization failure must release the lifetime lease before shutdown or disposal." );
		var shutdown = await recovered.ShutdownAsync();
		Assert.IsTrue( shutdown.LeaseReleased );
		Assert.AreSame( shutdown, await recovered.ShutdownAsync() );
	}

	[TestMethod]
	public async Task MissingAcknowledgementCreatesFatalHashChainGap()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 2 );
		foreach ( var completion in await storage.ListAsync( Completions ) ) await storage.DeleteAsync( completion );
		var firstAck = (await storage.ListAsync( Acks )).OrderBy( value => value, StringComparer.Ordinal ).First();
		await storage.DeleteAsync( firstAck );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
		Assert.Throws<InvalidOperationException>( () => recovered.Repository<TestDocument>( "documents" ) );
		await using var lease = await storage.AcquireExclusiveLeaseAsync( Root + "/lease.lock" );
		Assert.IsFalse( lease.IsReleased, "Failed recovery must release its lifetime lease immediately." );
	}

	[TestMethod]
	public async Task MissingAcknowledgementCoveredByOnlyCheckpointIsStillFatal()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 2 );
		Assert.HasCount( 1, await storage.ListAsync( Completions ) );
		var firstAck = (await storage.ListAsync( Acks )).OrderBy( value => value, StringComparer.Ordinal ).First();
		await storage.DeleteAsync( firstAck );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
	}

	[TestMethod]
	public async Task LostSoleTailAcknowledgementIsDetectedByCommitHead()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 1 );
		foreach ( var completion in await storage.ListAsync( Completions ) ) await storage.DeleteAsync( completion );
		foreach ( var acknowledgement in await storage.ListAsync( Acks ) ) await storage.DeleteAsync( acknowledgement );
		Assert.HasCount( 1, await storage.ListAsync( CommitHeads ) );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
	}

	[TestMethod]
	public async Task PreAcknowledgementCrashFrameRemainsARecoverableOrphan()
	{
		var storage = new FaultInjectingStorage();
		var failed = Create( storage );
		await failed.InitializeAsync();
		var repository = failed.Repository<TestDocument>( "documents" );
		storage.FailNextImmutableWrite = path => path.Contains( "/wal/acks/", StringComparison.Ordinal );
		await using ( var unit = failed.BeginUnitOfWork() )
		{
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsFalse( (await unit.CommitAsync()).Succeeded );
		}
		await failed.ShutdownAsync();
		await failed.DisposeAsync();
		Assert.HasCount( 1, await storage.ListAsync( Frames ) );
		Assert.IsEmpty( await storage.ListAsync( CommitHeads ) );

		await using var recovered = Create( storage );
		await recovered.InitializeAsync();
		Assert.IsEmpty( await storage.ListAsync( Frames ) );
		Assert.IsNull( recovered.Repository<TestDocument>( "documents" ).Find( "one" ) );
		Assert.AreEqual( 1, recovered.Health.DiscardedUnacknowledgedFrames );
		Assert.AreEqual(
			PersistenceHealthStatus.Healthy,
			recovered.Health.Status,
			"Discarding an unacknowledged frame is normal crash recovery, not a degraded store." );
	}

	[TestMethod]
	public async Task CorruptAcknowledgedFrameIsFatalEvenWithCheckpoint()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 2 );
		var frame = (await storage.ListAsync( Frames )).OrderBy( value => value, StringComparer.Ordinal ).First();
		await storage.CorruptByteAsync( frame, 0 );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
	}

	[TestMethod]
	public async Task CorruptAcknowledgementIsFatalAndNeverTreatedAsAnOrphan()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 2 );
		var acknowledgement = (await storage.ListAsync( Acks )).OrderBy( value => value, StringComparer.Ordinal ).First();
		await storage.CorruptByteFromEndAsync( acknowledgement, 4 );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
		Assert.AreEqual( PersistenceProviderState.Faulted, recovered.State );
	}

	[TestMethod]
	public async Task TamperedAcknowledgementFramePathFailsExactNamingContract()
	{
		var storage = new FaultInjectingStorage();
		await WriteCommitsAndShutdownAsync( storage, 1 );
		var acknowledgementPaths = await storage.ListAsync( Acks );
		Assert.HasCount( 1, acknowledgementPaths );
		var acknowledgementPath = acknowledgementPaths[0];
		var acknowledgementBytes = await storage.ReadAsync( acknowledgementPath ) ?? throw new InvalidOperationException();
		var acknowledgement = JsonSerializer.Deserialize<WalAcknowledgement>( acknowledgementBytes.Span,
			new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } )!;
		var frameBytes = await storage.ReadAsync( acknowledgement.FramePath ) ?? throw new InvalidOperationException();
		var tamperedFramePath = acknowledgement.FramePath.Replace(
			"00000000000000000001-", "00000000000000000009-", StringComparison.Ordinal );
		Assert.IsTrue( await storage.TryWriteImmutableAsync( tamperedFramePath, frameBytes ) );
		var tampered = acknowledgement with { FramePath = tamperedFramePath };
		await storage.OverwriteAsync( acknowledgementPath, JsonSerializer.SerializeToUtf8Bytes( tampered,
			new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } ) );

		await using var recovered = Create( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await recovered.InitializeAsync() );
	}

	[TestMethod]
	public async Task RecoveryRemovesAbandonedAtomicPublicationStagingFiles()
	{
		var storage = new InMemoryPersistenceStorage();
		var staging = Root + "/wal/acks/.00000000000000000001.abc.staging";
		Assert.IsTrue( await storage.TryWriteImmutableAsync( staging, new byte[] { 1, 2, 3 } ) );

		await using var provider = Create( storage );
		await provider.InitializeAsync();

		Assert.IsFalse( await storage.ExistsAsync( staging ) );
		Assert.AreEqual( PersistenceProviderState.Ready, provider.State );
	}

	[TestMethod]
	public async Task CommitAndRecoveryUseTheSameInvariantSet()
	{
		var invariant = new RejectForbiddenNameInvariant();
		var volatileProvider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry(), invariant );
		await using ( volatileProvider )
		{
			await volatileProvider.InitializeAsync();
			var repository = volatileProvider.Repository<TestDocument>( "documents" );
			await using var rejected = volatileProvider.BeginUnitOfWork();
			rejected.Create( repository, "one", new TestDocument( "forbidden", 1 ) );
			var result = await rejected.CommitAsync();
			Assert.AreEqual( PersistenceErrorCode.InvariantViolation, result.Error!.Code );
			Assert.IsNull( repository.Find( "one" ) );
		}

		var storage = new FaultInjectingStorage();
		await using ( var writer = Create( storage ) )
		{
			await writer.InitializeAsync();
			var repository = writer.Repository<TestDocument>( "documents" );
			await using var unit = writer.BeginUnitOfWork();
			unit.Create( repository, "one", new TestDocument( "forbidden", 1 ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
			Assert.IsTrue( (await writer.ShutdownAsync()).IsClean );
		}
		await using var reader = Create( storage, invariant );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await reader.InitializeAsync() );
	}

	[TestMethod]
	public async Task ZeroMutationCommitStillRechecksPreconditions()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		await using var unit = provider.BeginUnitOfWork();
		unit.Require( new RejectingPrecondition() );
		var result = await unit.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, result.Error!.Code );
		Assert.AreEqual( 0L, provider.Health.Sequence );
	}

	[TestMethod]
	public async Task ZeroMutationProofCommitSkipsFullInvariantValidationAfterCheckingPreconditions()
	{
		var storage = new FaultInjectingStorage();
		var invariants = new CountingInvariantSet();
		await using var provider = Create( storage, invariants: invariants );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}
		var observed = repository.Find( "one" )!;
		invariants.Reset();
		var precondition = new CountingPrecondition();

		await using var proof = provider.BeginUnitOfWork();
		proof.RequireUnchanged( repository, observed );
		proof.Require( precondition );
		var result = await proof.CommitAsync();

		Assert.IsTrue( result.Succeeded );
		Assert.AreEqual( 1, precondition.ValidationCount );
		Assert.AreEqual( 0, invariants.ValidationCount );
		Assert.AreEqual( 1L, result.Value!.Sequence );
	}

	[TestMethod]
	public void InvariantContextCachesTenThousandDocumentCollectionIndexes()
	{
		var documents = Enumerable.Range( 0, 10_000 )
			.Select( index => new DocumentAddress(
				index % 2 == 0 ? "documents" : "other", $"{index:D5}" ) )
			.ToDictionary(
			address => address,
			address => new PersistenceCandidateDocument(
				address,
				new DocumentRevision( 1 ),
				new PersistedTypeKey( "test.document" ),
				1,
				new TestDocument( address.Key, 1 ) ) );
		var context = new PersistenceInvariantContext( 1, 0, documents );

		var firstCollection = context.Collection( "documents" );
		var firstDocuments = context.Documents;
		for ( var iteration = 0; iteration < 1_000; iteration++ )
		{
			Assert.AreSame( firstCollection, context.Collection( "documents" ) );
			Assert.AreSame( firstDocuments, context.Documents );
		}
		Assert.HasCount( 5_000, firstCollection );
	}

	[TestMethod]
	public async Task TombstoneCompactionAdvancesGenerationAndInvalidatesOpenWork()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		var stale = provider.BeginUnitOfWork();
		await using ( var delete = provider.BeginUnitOfWork() )
		{
			delete.Delete( repository, repository.Find( "one" )! );
			Assert.IsTrue( (await delete.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
		Assert.AreEqual( 1L, provider.CompactionGeneration );

		var result = await stale.CommitAsync();
		await stale.DisposeAsync();
		Assert.AreEqual( PersistenceErrorCode.StaleTransaction, result.Error!.Code );
	}

	[TestMethod]
	public async Task VerifiedCheckpointAdvancesGenerationEvenWhenBestEffortPruningFails()
	{
		var storage = new FaultInjectingStorage();
		var log = new List<string>();
		await using var provider = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( "test-schema" )
			{
				CheckpointEveryCommits = 0,
				Log = log.Add
			},
			PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
		var stale = provider.BeginUnitOfWork();
		await using ( var delete = provider.BeginUnitOfWork() )
		{
			delete.Delete( repository, repository.Find( "one" )! );
			Assert.IsTrue( (await delete.CommitAsync()).Succeeded );
		}
		storage.FailNextDelete = path => path.Contains( "/wal/acks/", StringComparison.Ordinal );

		var checkpoint = await provider.CheckpointAsync();

		Assert.IsTrue( checkpoint.Succeeded, checkpoint.Error?.Message );
		Assert.AreEqual( 1L, provider.CompactionGeneration );
		Assert.AreEqual( PersistenceHealthStatus.Degraded, provider.Health.Status );
		Assert.IsTrue( provider.Health.CheckpointRetryPending );
		Assert.IsTrue( log.Any( entry => entry.StartsWith( "HEXAGON_COMPACTION_DEGRADED", StringComparison.Ordinal ) ) );
		var staleResult = await stale.CommitAsync();
		await stale.DisposeAsync();
		Assert.AreEqual( PersistenceErrorCode.StaleTransaction, staleResult.Error!.Code );
	}

	[TestMethod]
	public async Task EveryPruneDeletionPhaseIsRestartSafe()
	{
		var failureSelectors = new Func<string, bool>[]
		{
			path => path.Contains( "/wal/commit-heads/00000000000000000002-", StringComparison.Ordinal ),
			path => path.Contains( "/wal/acks/00000000000000000002.ack", StringComparison.Ordinal ),
			path => path.Contains( "/wal/frames/00000000000000000002-", StringComparison.Ordinal ),
			path => path.Contains( "/checkpoints/complete/00000000000000000001-", StringComparison.Ordinal ),
			path => path.Contains( "/checkpoints/manifests/00000000000000000001-", StringComparison.Ordinal ),
			path => path.Contains( "/checkpoints/blobs/", StringComparison.Ordinal ),
			path => path.Contains( "/checkpoints/prune-intents/", StringComparison.Ordinal )
		};
		foreach ( var selector in failureSelectors )
		{
			var storage = new FaultInjectingStorage();
			var provider = new FileSystemPersistenceProvider(
				storage,
				new FileSystemPersistenceOptions( "test-schema" ) { CheckpointEveryCommits = 0 },
				PersistenceTestSupport.CreateRegistry() );
			await provider.InitializeAsync();
			var repository = provider.Repository<TestDocument>( "documents" );
			for ( var sequence = 1; sequence <= 2; sequence++ )
			{
				await using var unit = provider.BeginUnitOfWork();
				unit.Put( repository, "one", new TestDocument( "one", sequence ) );
				Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
				Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
			}
			await using ( var third = provider.BeginUnitOfWork() )
			{
				third.Put( repository, "one", new TestDocument( "one", 3 ) );
				Assert.IsTrue( (await third.CommitAsync()).Succeeded );
			}
			storage.FailNextDelete = selector;
			Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
			storage.FailNextDelete = selector;
			var shutdown = await provider.ShutdownAsync();
			Assert.IsFalse( shutdown.IsClean );
			Assert.IsTrue( shutdown.IsRecoverable );
			StringAssert.Contains( shutdown.Detail!, "cleanup remains pending" );
			await provider.DisposeAsync();
			storage.FailNextDelete = null;

			await using var recovered = Create( storage );
			await recovered.InitializeAsync();
			Assert.AreEqual( 3, recovered.Repository<TestDocument>( "documents" ).Find( "one" )!.Value.Score );
			Assert.IsEmpty( await storage.ListAsync( Root + "/checkpoints/prune-intents" ) );
		}
	}

	[TestMethod]
	public async Task FallbackAcrossCompactionAllowsRevisionOneKeyReuse()
	{
		var storage = new FaultInjectingStorage();
		var options = new FileSystemPersistenceOptions( "test-schema" )
		{
			CheckpointEveryCommits = 0,
			RetainedCheckpointGenerations = 3
		};
		await using ( var provider = new FileSystemPersistenceProvider(
			storage, options, PersistenceTestSupport.CreateRegistry() ) )
		{
			await provider.InitializeAsync();
			var repository = provider.Repository<TestDocument>( "documents" );
			await using ( var create = provider.BeginUnitOfWork() )
			{
				create.Create( repository, "one", new TestDocument( "one", 1 ) );
				Assert.IsTrue( (await create.CommitAsync()).Succeeded );
			}
			Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
			await using ( var delete = provider.BeginUnitOfWork() )
			{
				delete.Delete( repository, repository.Find( "one" )! );
				Assert.IsTrue( (await delete.CommitAsync()).Succeeded );
			}
			Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
			await using ( var recreate = provider.BeginUnitOfWork() )
			{
				recreate.Create( repository, "one", new TestDocument( "recreated", 3 ) );
				Assert.IsTrue( (await recreate.CommitAsync()).Succeeded );
			}
			Assert.IsTrue( (await provider.ShutdownAsync()).IsClean );
		}
		var completions = (await storage.ListAsync( Completions ))
			.OrderByDescending( path => path, StringComparer.Ordinal ).ToArray();
		Assert.HasCount( 3, completions );
		await storage.CorruptByteAsync( completions[0], 0 );
		await storage.CorruptByteAsync( completions[1], 0 );

		await using var recovered = new FileSystemPersistenceProvider(
			storage, options, PersistenceTestSupport.CreateRegistry() );
		await recovered.InitializeAsync();

		var document = recovered.Repository<TestDocument>( "documents" ).Find( "one" );
		Assert.IsNotNull( document );
		Assert.AreEqual( 1L, document.Revision.Value );
		Assert.AreEqual( "recreated", document.Value.Name );
		Assert.IsTrue( recovered.Health.RecoveredFromCheckpointFallback );
	}

	[TestMethod]
	public async Task VerifiedCheckpointsBoundRetainedWalAndCommitCount()
	{
		var storage = new FaultInjectingStorage();
		var options = new FileSystemPersistenceOptions( "test-schema" )
		{
			CheckpointEveryCommits = 2,
			RetainedCheckpointGenerations = 2,
			SoftCheckpointBytes = 1_024 * 1_024,
			MaximumRetainedWalBytes = 4 * 1_024 * 1_024,
			MaximumFrameBytes = 256 * 1_024,
			MaximumRetainedCommits = 16
		};
		await using var provider = new FileSystemPersistenceProvider(
			storage, options, PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		for ( var value = 1; value <= 12; value++ )
		{
			await using var unit = provider.BeginUnitOfWork();
			unit.Put( repository, "one", new TestDocument( "one", value ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
		}

		Assert.IsLessThanOrEqualTo( 2, (await storage.ListAsync( Acks )).Count );
		Assert.IsLessThanOrEqualTo( 2, (await storage.ListAsync( Frames )).Count );
		Assert.HasCount( 2, await storage.ListAsync( Completions ) );
	}

	[TestMethod]
	public async Task OversizedFrameIsRejectedBeforeAnyDurableFileIsCreated()
	{
		var storage = new InMemoryPersistenceStorage();
		var options = new FileSystemPersistenceOptions( "test-schema" )
		{
			CheckpointEveryCommits = 0,
			SoftCheckpointBytes = 4_096,
			MaximumRetainedWalBytes = 8_192,
			MaximumFrameBytes = 1_024,
			MaximumRetainedCommits = 16
		};
		await using var provider = new FileSystemPersistenceProvider(
			storage, options, PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using var unit = provider.BeginUnitOfWork();
		unit.Create( repository, "large", new TestDocument( new string( 'x', 4_096 ), 1 ) );

		var result = await unit.CommitAsync();

		Assert.AreEqual( PersistenceErrorCode.StorageLimitExceeded, result.Error!.Code );
		Assert.IsEmpty( await storage.ListAsync( Frames ) );
		Assert.IsEmpty( await storage.ListAsync( Acks ) );
		Assert.AreEqual( PersistenceHealthStatus.Healthy, provider.Health.Status );
	}

	[TestMethod]
	public async Task DegradedShutdownIsRecoverableAndReleasesLease()
	{
		var storage = new FaultInjectingStorage();
		var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		await using ( var unit = provider.BeginUnitOfWork() )
		{
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
		}
		storage.FailNextImmutableWrite = path => path.Contains( "/checkpoints/complete/", StringComparison.Ordinal );

		var shutdown = await provider.ShutdownAsync();
		Assert.IsFalse( shutdown.IsClean );
		Assert.IsTrue( shutdown.IsRecoverable );
		Assert.IsTrue( shutdown.LeaseReleased );
		Assert.IsFalse( shutdown.Checkpoint.Succeeded );
		Assert.AreSame( shutdown, await provider.ShutdownAsync() );

		await using var recovered = Create( storage );
		await recovered.InitializeAsync();
		Assert.IsNotNull( recovered.Repository<TestDocument>( "documents" ).Find( "one" ) );
	}

	[TestMethod]
	public async Task FaultedProviderReleasesLeaseButRemainsTerminalAndNotRecoverable()
	{
		var storage = new FaultInjectingStorage();
		var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		storage.FailNextImmutableWrite = path => path.Contains( "/wal/acks/", StringComparison.Ordinal );
		await using ( var unit = provider.BeginUnitOfWork() )
		{
			unit.Create( repository, "one", new TestDocument( "one", 1 ) );
			Assert.IsFalse( (await unit.CommitAsync()).Succeeded );
		}
		Assert.AreEqual( PersistenceProviderState.Faulted, provider.State );

		var shutdown = await provider.ShutdownAsync();

		Assert.IsFalse( shutdown.IsClean );
		Assert.IsFalse( shutdown.IsRecoverable );
		Assert.IsTrue( shutdown.LeaseReleased );
		Assert.AreEqual( PersistenceProviderState.Faulted, provider.State );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, provider.Health.Status );
		await provider.DisposeAsync();
		Assert.AreEqual( PersistenceProviderState.Faulted, provider.State );
	}

	[TestMethod]
	[Timeout( 120_000, CooperativeCancellation = true )]
	public async Task TenThousandCommitChurnKeepsWalCheckpointAndTombstonesBounded()
	{
		var storage = new InMemoryPersistenceStorage();
		await using ( var provider = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( "test-schema" ),
			PersistenceTestSupport.CreateRegistry() ) )
		{
			await provider.InitializeAsync();
			var repository = provider.Repository<TestDocument>( "documents" );
			for ( var index = 0; index < 10_000; index++ )
			{
				var key = $"churn-{index / 2:D5}";
				await using var unit = provider.BeginUnitOfWork();
				if ( (index & 1) == 0 ) unit.Create( repository, key, new TestDocument( key, index ) );
				else unit.Delete( repository, repository.Find( key )! );
				var committed = await unit.CommitAsync();
				Assert.IsTrue( committed.Succeeded, committed.Error?.Message );
			}
			Assert.IsEmpty( repository.All() );
			Assert.IsTrue( (await provider.ShutdownAsync()).IsClean );
		}

		Assert.IsLessThanOrEqualTo( 256, (await storage.ListAsync( Acks )).Count );
		Assert.IsLessThanOrEqualTo( 256, (await storage.ListAsync( Frames )).Count );
		Assert.HasCount( 2, await storage.ListAsync( Completions ) );
		await using var recovered = Create( storage );
		await recovered.InitializeAsync();
		Assert.IsEmpty( recovered.Repository<TestDocument>( "documents" ).All() );
		Assert.AreEqual( 10_000L, recovered.Health.Sequence );
	}

	private static FileSystemPersistenceProvider Create(
		IPersistenceStorage storage,
		IPersistenceInvariantSet? invariants = null ) => new(
		storage,
		new FileSystemPersistenceOptions( "test-schema" ) { CheckpointEveryCommits = 0 },
		PersistenceTestSupport.CreateRegistry(),
		invariants );

	private static FileSystemPersistenceProvider CreateWithRegistry(
		IPersistenceStorage storage,
		PersistedTypeRegistry registry ) => new(
		storage,
		new FileSystemPersistenceOptions( "test-schema" ) { CheckpointEveryCommits = 0 },
		registry );

	private static PersistedTypeRegistry CreateIncrementingScoreRegistry() => new PersistedTypeRegistry()
		.Register<TestDocument>(
			new PersistedTypeKey( "test.document" ),
			1,
			static value => value with { Score = checked(value.Score + 1) } );

	private static async Task WriteCommitsAndShutdownAsync( FaultInjectingStorage storage, int count )
	{
		await using var provider = Create( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "documents" );
		for ( var index = 1; index <= count; index++ )
		{
			await using var unit = provider.BeginUnitOfWork();
			unit.Put( repository, "one", new TestDocument( "one", index ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await provider.ShutdownAsync()).IsClean );
	}

	private static Process StartLeaseProbe( string role, string root, string signal )
	{
		var project = FindTestProject();
		var start = new ProcessStartInfo( "dotnet" )
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		start.ArgumentList.Add( "test" );
		start.ArgumentList.Add( project );
		start.ArgumentList.Add( "--no-build" );
		start.ArgumentList.Add( "--no-restore" );
		start.ArgumentList.Add( "-c" );
		start.ArgumentList.Add( "Release" );
		start.ArgumentList.Add( "--filter" );
		start.ArgumentList.Add(
			"FullyQualifiedName=Hexagon.V2.Tests.Persistence.ImmutableWalPersistenceProviderTests.TwoChildProcessesEnforceLeaseAndCrashRelease" );
		start.Environment[LeaseProbeRole] = role;
		start.Environment[LeaseProbeRoot] = root;
		start.Environment[LeaseProbeSignal] = signal;
		return Process.Start( start ) ?? throw new InvalidOperationException( "Could not start lease probe." );
	}

	private static string FindTestProject()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null )
		{
			var candidate = Path.Combine( directory.FullName, "Hexagon.V2.Tests.csproj" );
			if ( File.Exists( candidate ) ) return candidate;
			directory = directory.Parent;
		}
		throw new FileNotFoundException( "Could not locate Hexagon.V2.Tests.csproj." );
	}

	private static async Task WaitForFileAsync(
		string path,
		Process holder,
		CancellationToken cancellationToken )
	{
		while ( !File.Exists( path ) )
		{
			if ( holder.HasExited )
				throw new InvalidOperationException(
					$"Lease holder exited early: {await holder.StandardError.ReadToEndAsync( cancellationToken )}" );
			await Task.Delay( 25, cancellationToken );
		}
	}

	private static async Task RunLeaseProbeChildAsync( string role, CancellationToken cancellationToken )
	{
		var root = Environment.GetEnvironmentVariable( LeaseProbeRoot )
			?? throw new InvalidOperationException( "Lease probe root is missing." );
		var signal = Environment.GetEnvironmentVariable( LeaseProbeSignal )
			?? throw new InvalidOperationException( "Lease probe signal is missing." );
		var storage = new PhysicalFilePersistenceStorage( root );
		if ( role == "contender" )
		{
			await using var contender = Create( storage );
			await Assert.ThrowsAsync<PersistenceLeaseUnavailableException>( async () =>
				await contender.InitializeAsync( cancellationToken ) );
			return;
		}

		await using var provider = Create( storage );
		await provider.InitializeAsync( cancellationToken );
		var repository = provider.Repository<TestDocument>( "documents" );
		if ( role == "successor" )
		{
			var recovered = repository.Find( "holder" );
			Assert.IsNotNull( recovered );
			Assert.AreEqual( 1, recovered.Value.Score );
			await using var unit = provider.BeginUnitOfWork();
			unit.Create( repository, "successor", new TestDocument( "successor", 2 ) );
			Assert.IsTrue( (await unit.CommitAsync( cancellationToken )).Succeeded );
			Assert.IsTrue( (await provider.ShutdownAsync( cancellationToken )).IsClean );
			return;
		}
		if ( role != "holder" ) throw new InvalidOperationException( $"Unknown lease probe role '{role}'." );
		await using ( var unit = provider.BeginUnitOfWork() )
		{
			unit.Create( repository, "holder", new TestDocument( "holder", 1 ) );
			Assert.IsTrue( (await unit.CommitAsync( cancellationToken )).Succeeded );
		}
		await File.WriteAllTextAsync( signal, "ready", cancellationToken );
		await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
	}

	private sealed class RejectForbiddenNameInvariant : IPersistenceInvariantSet
	{
		public IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context ) =>
			context.Documents.Any( value => value.Value is TestDocument { Name: "forbidden" } )
				? [new PersistenceInvariantIssue( "test.forbidden", "documents", "Forbidden names are invalid." )]
				: Array.Empty<PersistenceInvariantIssue>();
	}

	private sealed class RejectingPrecondition : ICommitPrecondition
	{
		public PersistenceInvariantIssue? Validate( CommitPreconditionContext context ) =>
			new( "test.stale", "capability", "Capability revision changed." );
	}

	private sealed class CountingInvariantSet : IPersistenceInvariantSet
	{
		public int ValidationCount { get; private set; }
		public IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context )
		{
			ValidationCount++;
			return Array.Empty<PersistenceInvariantIssue>();
		}
		public void Reset() => ValidationCount = 0;
	}

	private sealed class CountingPrecondition : ICommitPrecondition
	{
		public int ValidationCount { get; private set; }
		public PersistenceInvariantIssue? Validate( CommitPreconditionContext context )
		{
			ValidationCount++;
			return null;
		}
	}
}
