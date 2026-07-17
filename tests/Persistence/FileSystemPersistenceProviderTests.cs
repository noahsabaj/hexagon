using System.Text.Json;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Persistence;

[TestClass]
public sealed class FileSystemPersistenceProviderTests
{
	private const string Root = "hexagon/persistence/v3/test-schema";
	private const string FramePrefix = Root + "/wal/frames";
	private const string CompletionPrefix = Root + "/checkpoints/complete";
	private const string ManifestPrefix = Root + "/checkpoints/manifests";
	private const string BlobPrefix = Root + "/checkpoints/blobs";

	[TestMethod]
	public async Task StorageAdapterRetainsImmutableContent()
	{
		var storage = new FaultInjectingStorage();
		Assert.IsTrue( await storage.TryWriteImmutableAsync( "test/value", new byte[] { 1, 2, 3 } ) );
		var content = await storage.ReadAsync( "test/value" );
		Assert.IsNotNull( content );
		Assert.AreEqual( 3, content.Value.Length );
	}

	[TestMethod]
	public void FormatManifestSerializesToJson()
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes( new PersistenceFormatManifest
		{
			Format = PersistenceFormatManifest.ExpectedFormat,
			Version = PersistenceFormatManifest.CurrentVersion,
			SchemaId = "test-schema",
			StoreId = Guid.NewGuid(),
			CreatedAtUtc = DateTimeOffset.UtcNow
		}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } );

		Assert.IsGreaterThan( 0, bytes.Length );
	}

	[TestMethod]
	public async Task ProviderWritesNonemptyFormatManifest()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var content = await storage.ReadAsync( Root + "/format.json" );
		Assert.IsNotNull( content );
		Assert.IsGreaterThan( 0, content.Value.Length );
	}

	[TestMethod]
	public async Task WalRoundTripReplaysEachCommitExactlyOnce()
	{
		var storage = new FaultInjectingStorage();
		await using var first = PersistenceTestSupport.CreateFileProvider( storage );
		await first.InitializeAsync();
		var repository = first.Repository<TestDocument>( "characters" );
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		await using ( var update = first.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await first.ShutdownAsync()).IsClean );

		await using var recovered = PersistenceTestSupport.CreateFileProvider( storage );
		await recovered.InitializeAsync();
		var recoveredDocument = recovered.Repository<TestDocument>( "characters" ).Find( "alyx" )!;

		Assert.AreEqual( 2L, recovered.Health.Sequence );
		Assert.AreEqual( 2L, recoveredDocument.Revision.Value );
		Assert.AreEqual( 2, recoveredDocument.Value.Score );
	}

	[TestMethod]
	public async Task CanonicalCollectionReadsCannotBeMutatedOrDivergeFromRecovery()
	{
		var storage = new FaultInjectingStorage();
		await using var first = PersistenceTestSupport.CreateFileProvider( storage );
		await first.InitializeAsync();
		var repository = first.Repository<CollectionTestDocument>( "characters" );
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new CollectionTestDocument { Name = "Alyx", Tags = ["citizen"] } );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}

		var canonical = repository.Find( "alyx" )!;
		Assert.AreSame( canonical.Value, repository.Find( "alyx" )!.Value );
		var exposedTags = (System.Collections.Generic.IList<string>)canonical.Value.Tags;
		Assert.IsTrue( exposedTags.IsReadOnly );
		Assert.Throws<NotSupportedException>( () => exposedTags.Add( "forged" ) );
		Assert.IsTrue( (await first.ShutdownAsync()).IsClean );

		await using var recovered = PersistenceTestSupport.CreateFileProvider( storage );
		await recovered.InitializeAsync();
		var recoveredDocument = recovered.Repository<CollectionTestDocument>( "characters" ).Find( "alyx" )!;

		Assert.AreEqual( canonical.Revision, recoveredDocument.Revision );
		CollectionAssert.AreEqual( new[] { "citizen" }, recoveredDocument.Value.Tags.ToArray() );
	}

	[TestMethod]
	public async Task PartialFinalWalFrameIsTruncatedBeforeNewCommits()
	{
		var storage = new FaultInjectingStorage();
		await using var first = PersistenceTestSupport.CreateFileProvider( storage );
		await first.InitializeAsync();
		var repository = first.Repository<TestDocument>( "characters" );
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		var orphan = FramePrefix + "/00000000000000000002-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.frame";
		await storage.SeedAsync( orphan, new byte[] { (byte)'H', (byte)'X', (byte)'W' } );
		Assert.IsTrue( (await first.ShutdownAsync()).IsClean );

		await using var repaired = PersistenceTestSupport.CreateFileProvider( storage );
		await repaired.InitializeAsync();
		Assert.IsFalse( await storage.ExistsAsync( orphan ) );
		var repairedRepository = repaired.Repository<TestDocument>( "characters" );
		await using ( var create = repaired.BeginUnitOfWork() )
		{
			create.Create( repairedRepository, "barney", new TestDocument( "Barney", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await repaired.ShutdownAsync()).IsClean );

		await using var verified = PersistenceTestSupport.CreateFileProvider( storage );
		await verified.InitializeAsync();
		Assert.HasCount( 2, verified.Repository<TestDocument>( "characters" ).All() );
	}

	[TestMethod]
	public async Task ChecksumCorruptionFailsRecoveryInsteadOfPublishingPartialState()
	{
		var storage = new FaultInjectingStorage();
		await using var first = PersistenceTestSupport.CreateFileProvider( storage );
		await first.InitializeAsync();
		var repository = first.Repository<TestDocument>( "characters" );
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "barney", new TestDocument( "Barney", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await first.ShutdownAsync()).IsClean );
		var firstFrame = (await storage.ListAsync( FramePrefix )).OrderBy( value => value, StringComparer.Ordinal ).First();
		await storage.CorruptByteAsync( firstFrame, 10 );

		await using var corrupted = PersistenceTestSupport.CreateFileProvider( storage );
		await Assert.ThrowsAsync<PersistenceCorruptionException>( async () => await corrupted.InitializeAsync() );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, corrupted.Health.Status );
	}

	[TestMethod]
	public async Task CheckpointRetentionKeepsTwoCompleteGenerations()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );

		for ( var score = 1; score <= 3; score++ )
		{
			await using var unit = provider.BeginUnitOfWork();
			unit.Put( repository, "alyx", new TestDocument( "Alyx", score ) );
			Assert.IsTrue( (await unit.CommitAsync()).Succeeded );
			Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
		}

		Assert.HasCount( 2, await storage.ListAsync( CompletionPrefix ) );
		Assert.HasCount( 2, await storage.ListAsync( ManifestPrefix ) );
		Assert.HasCount( 2, await storage.ListAsync( BlobPrefix ) );
	}

	[TestMethod]
	public async Task InvalidNewestCheckpointFallsBackThenReplaysWal()
	{
		var storage = new FaultInjectingStorage();
		await using var first = PersistenceTestSupport.CreateFileProvider( storage );
		await first.InitializeAsync();
		var repository = first.Repository<TestDocument>( "characters" );
		await using ( var create = first.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await first.CheckpointAsync()).Succeeded );
		await using ( var update = first.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( (await first.CheckpointAsync()).Succeeded );

		var completionPaths = await storage.ListAsync( CompletionPrefix );
		var latestCompletionPath = completionPaths.OrderByDescending( path => path, StringComparer.Ordinal ).First();
		var completionBytes = await storage.ReadAsync( latestCompletionPath ) ?? throw new InvalidOperationException();
		using var completionJson = JsonDocument.Parse( completionBytes );
		var manifestPath = completionJson.RootElement.GetProperty( "manifestPath" ).GetString()!;
		var manifestBytes = await storage.ReadAsync( manifestPath ) ?? throw new InvalidOperationException();
		using var manifestJson = JsonDocument.Parse( manifestBytes );
		var blobPath = manifestJson.RootElement.GetProperty( "blobPath" ).GetString()!;
		await storage.CorruptByteFromEndAsync( blobPath, 1 );

		await using ( var update = first.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 3 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}
		storage.FailNextImmutableWrite = path => path.Contains( "/checkpoints/complete/", StringComparison.Ordinal );
		var shutdown = await first.ShutdownAsync();
		Assert.IsFalse( shutdown.IsClean );
		Assert.IsTrue( shutdown.IsRecoverable );

		await using var recovered = PersistenceTestSupport.CreateFileProvider( storage );
		await recovered.InitializeAsync();

		Assert.IsTrue( recovered.Health.RecoveredFromCheckpointFallback );
		Assert.AreEqual( 3L, recovered.Health.Sequence );
		Assert.AreEqual( 3, recovered.Repository<TestDocument>( "characters" ).Find( "alyx" )!.Value.Score );
	}

	[TestMethod]
	public async Task CheckpointFailureIsRetryableButCommitFailureIsFatalAndUnpublished()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}

		storage.FailNextImmutableWrite = path => path.Contains( "/checkpoints/blobs/", StringComparison.Ordinal );
		var failedCheckpoint = await provider.CheckpointAsync();
		Assert.IsFalse( failedCheckpoint.Succeeded );
		Assert.IsTrue( provider.Health.CheckpointRetryPending );
		Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
		Assert.AreEqual( PersistenceHealthStatus.Healthy, provider.Health.Status );

		storage.FailNextImmutableWrite = path => path.Contains( "/wal/frames/", StringComparison.Ordinal );
		await using var failedCommit = provider.BeginUnitOfWork();
		failedCommit.Create( repository, "barney", new TestDocument( "Barney", 1 ) );
		var result = await failedCommit.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.DurabilityFailed, result.Error!.Code );
		Assert.AreEqual( 1L, provider.Health.Sequence );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, provider.Health.Status );
	}

	[TestMethod]
	public async Task AutomaticCheckpointFailureRetriesAfterTheNextCommit()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage, checkpointEveryCommits: 1 );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		storage.FailNextImmutableWrite = path => path.Contains( "/checkpoints/blobs/", StringComparison.Ordinal );

		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		Assert.IsTrue( provider.Health.CheckpointRetryPending );

		await using ( var update = provider.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}

		Assert.AreEqual( PersistenceHealthStatus.Healthy, provider.Health.Status );
		Assert.AreEqual( 2L, provider.Health.CheckpointSequence );
	}

	[TestMethod]
	public async Task AllReturnsACachedSnapshotUntilTheCollectionMutates()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var characters = provider.Repository<TestDocument>( "characters" );
		var items = provider.Repository<CollectionTestDocument>( "items" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( characters, "alyx", new TestDocument( "Alyx", 1 ) );
			seed.Create( items, "crowbar", new CollectionTestDocument { Name = "crowbar" } );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}

		var first = characters.All();
		Assert.AreSame( first, characters.All(),
			"Repeated listings between commits must reuse the cached snapshot." );

		await using ( var unrelated = provider.BeginUnitOfWork() )
		{
			var editor = unrelated.Edit( items, items.Find( "crowbar" )! )!;
			editor.Replace( editor.Value with { Name = "stunstick" } );
			unrelated.Save( editor );
			Assert.IsTrue( (await unrelated.CommitAsync()).Succeeded );
		}
		Assert.AreSame( first, characters.All(),
			"A commit to an unrelated collection must not invalidate this collection's snapshot." );

		await using ( var update = provider.BeginUnitOfWork() )
		{
			var editor = update.Edit( characters, characters.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}
		var second = characters.All();
		Assert.AreNotSame( first, second );
		Assert.AreEqual( 2, second[0].Value.Score );
	}

	[TestMethod]
	public async Task ScheduledAutomaticCheckpointDoesNotRunOnTheCommittingCaller()
	{
		var storage = new FaultInjectingStorage();
		var scheduled = new List<Func<Task>>();
		await using var provider = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( "test-schema" )
			{
				CheckpointEveryCommits = 1,
				ScheduleBackgroundCheckpoint = work =>
				{
					scheduled.Add( work );
					return true;
				}
			},
			PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );

		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}

		Assert.HasCount( 1, scheduled );
		Assert.AreEqual( 0L, provider.Health.CheckpointSequence );
		Assert.IsEmpty(
			await storage.ListAsync( CompletionPrefix ),
			"The threshold-crossing commit must return without awaiting checkpoint I/O." );

		await using ( var update = provider.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}
		Assert.HasCount( 1, scheduled, "One background checkpoint may be in flight at a time." );

		await scheduled[0]();
		Assert.AreEqual( 2L, provider.Health.CheckpointSequence );
		Assert.HasCount( 1, await storage.ListAsync( CompletionPrefix ) );

		await using ( var final = provider.BeginUnitOfWork() )
		{
			var editor = final.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 3 } );
			final.Save( editor );
			Assert.IsTrue( (await final.CommitAsync()).Succeeded );
		}
		Assert.HasCount( 2, scheduled, "A completed checkpoint re-arms the scheduling latch." );
	}

	[TestMethod]
	public async Task RefusedAutomaticCheckpointScheduleRearmsOnTheNextCommit()
	{
		var storage = new FaultInjectingStorage();
		var attempts = 0;
		await using var provider = new FileSystemPersistenceProvider(
			storage,
			new FileSystemPersistenceOptions( "test-schema" )
			{
				CheckpointEveryCommits = 1,
				ScheduleBackgroundCheckpoint = _ =>
				{
					attempts++;
					return false;
				}
			},
			PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );

		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		await using ( var update = provider.BeginUnitOfWork() )
		{
			var editor = update.Edit( repository, repository.Find( "alyx" )! )!;
			editor.Replace( editor.Value with { Score = 2 } );
			update.Save( editor );
			Assert.IsTrue( (await update.CommitAsync()).Succeeded );
		}

		Assert.AreEqual( 2, attempts, "A refused schedule re-arms so the next commit retries." );
		Assert.AreEqual( 0L, provider.Health.CheckpointSequence );

		var shutdown = await provider.ShutdownAsync();
		Assert.IsTrue( shutdown.IsClean, "Shutdown takes its own final checkpoint regardless of scheduling." );
		Assert.AreEqual( 2L, shutdown.CheckpointSequence );
	}

	[TestMethod]
	public async Task CheckpointRetryIsIdempotentAfterManifestWasAlreadyWritten()
	{
		var storage = new FaultInjectingStorage();
		await using var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}

		storage.FailNextImmutableWrite = path => path.Contains( "/checkpoints/complete/", StringComparison.Ordinal );
		Assert.IsFalse( (await provider.CheckpointAsync()).Succeeded );
		Assert.IsTrue( (await provider.CheckpointAsync()).Succeeded );
		Assert.AreEqual( 1L, provider.Health.CheckpointSequence );
	}

	[TestMethod]
	public async Task DisposeWaitsForAnInFlightDurableFrameWrite()
	{
		var storage = new FaultInjectingStorage();
		var provider = PersistenceTestSupport.CreateFileProvider( storage );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		var immutableStarted = storage.BlockNextImmutableWrite();
		var unit = provider.BeginUnitOfWork();
		unit.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
		var commitTask = unit.CommitAsync().AsTask();
		await immutableStarted;

		var disposeTask = provider.DisposeAsync().AsTask();
		Assert.IsFalse( disposeTask.IsCompleted );
		storage.ReleaseBlockedImmutableWrite();
		var result = await commitTask;
		await disposeTask;
		await unit.DisposeAsync();

		Assert.IsTrue( result.Succeeded );
		Assert.AreEqual( PersistenceHealthStatus.Disposed, provider.Health.Status );
	}
}
