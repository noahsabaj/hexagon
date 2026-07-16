using Hexagon.V2.Persistence;
using System.Threading.Tasks;

namespace Hexagon.V2.Tests.Persistence;

[TestClass]
public sealed class InMemoryPersistenceProviderTests
{
	[TestMethod]
	public async Task CommittedIdentityIsCanonicalAndEditorsAreIsolated()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<CollectionTestDocument>( "characters" );

		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "alyx", new CollectionTestDocument { Name = "Alyx", Tags = ["citizen"] } );
			Assert.IsNull( repository.Find( "alyx" ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}

		var first = repository.Find( "alyx" )!;
		var second = repository.Find( "alyx" )!;
		Assert.AreSame( first.Value, second.Value );

		await using var update = provider.BeginUnitOfWork();
		var editor = update.Edit( repository, first )!;
		Assert.AreNotSame( first.Value, editor.Value );
		editor.Replace( editor.Value with { Tags = editor.Value.Tags.Append( "resistance" ).ToArray() } );
		update.Save( editor );
		CollectionAssert.AreEqual( new[] { "citizen" }, repository.Find( "alyx" )!.Value.Tags.ToArray() );

		var result = await update.CommitAsync();

		Assert.IsTrue( result.Succeeded );
		var committed = repository.Find( "alyx" )!;
		Assert.AreEqual( 2L, committed.Revision.Value );
		Assert.AreNotSame( first.Value, committed.Value );
		CollectionAssert.AreEqual( new[] { "citizen", "resistance" }, committed.Value.Tags.ToArray() );
	}

	[TestMethod]
	public async Task RevisionConflictAbortsEveryDocumentInTheUnit()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "a", new TestDocument( "A", 1 ) );
			seed.Create( repository, "b", new TestDocument( "B", 1 ) );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}

		await using var stale = provider.BeginUnitOfWork();
		var staleA = stale.Edit( repository, repository.Find( "a" )! )!;
		var staleB = stale.Edit( repository, repository.Find( "b" )! )!;
		staleA.Replace( staleA.Value with { Score = 2 } );
		staleB.Replace( staleB.Value with { Score = 2 } );
		stale.Save( staleA );
		stale.Save( staleB );

		await using ( var winner = provider.BeginUnitOfWork() )
		{
			var editor = winner.Edit( repository, repository.Find( "a" )! )!;
			editor.Replace( editor.Value with { Score = 3 } );
			winner.Save( editor );
			Assert.IsTrue( (await winner.CommitAsync()).Succeeded );
		}

		var conflict = await stale.CommitAsync();

		Assert.IsFalse( conflict.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, conflict.Error!.Code );
		Assert.AreEqual( 3, repository.Find( "a" )!.Value.Score );
		Assert.AreEqual( 1, repository.Find( "b" )!.Value.Score );
	}

	[TestMethod]
	public async Task ObservationThatLosesBeforeEditorAcquisitionCannotBeMutated()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}

		var observed = repository.Find( "alyx" )!;
		await using ( var winner = provider.BeginUnitOfWork() )
		{
			var winnerEditor = winner.Edit( repository, observed )!;
			winnerEditor.Replace( winnerEditor.Value with { Score = 2 } );
			winner.Save( winnerEditor );
			Assert.IsTrue( (await winner.CommitAsync()).Succeeded );
		}

		await using var stale = provider.BeginUnitOfWork();
		var staleEditor = stale.Edit( repository, observed );

		Assert.IsNull( staleEditor );
		Assert.AreEqual( 2, repository.Find( "alyx" )!.Value.Score );
	}

	[TestMethod]
	public async Task ObservationThatLosesBeforeDeleteCommitConflicts()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}

		var observed = repository.Find( "alyx" )!;
		await using ( var winner = provider.BeginUnitOfWork() )
		{
			var winnerEditor = winner.Edit( repository, observed )!;
			winnerEditor.Replace( winnerEditor.Value with { Score = 2 } );
			winner.Save( winnerEditor );
			Assert.IsTrue( (await winner.CommitAsync()).Succeeded );
		}

		await using var stale = provider.BeginUnitOfWork();
		stale.Delete( repository, observed );
		var conflict = await stale.CommitAsync();

		Assert.IsFalse( conflict.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, conflict.Error!.Code );
		Assert.AreEqual( 2, repository.Find( "alyx" )!.Value.Score );
	}

	[TestMethod]
	public async Task DeleteIsImmediatelyVisibleAndRecreateAdvancesTombstoneRevision()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );

		await using ( var create = provider.BeginUnitOfWork() )
		{
			create.Create( repository, "barney", new TestDocument( "Barney", 1 ) );
			Assert.IsTrue( (await create.CommitAsync()).Succeeded );
		}
		await using ( var delete = provider.BeginUnitOfWork() )
		{
			delete.Delete( repository, repository.Find( "barney" )! );
			var receipt = await delete.CommitAsync();
			Assert.AreEqual( 2L, receipt.Value!.Documents.Single().Revision.Value );
		}

		Assert.IsNull( repository.Find( "barney" ) );
		Assert.IsEmpty( repository.All() );

		await using ( var recreate = provider.BeginUnitOfWork() )
		{
			recreate.Create( repository, "barney", new TestDocument( "Barney", 2 ) );
			var receipt = await recreate.CommitAsync();
			Assert.AreEqual( 3L, receipt.Value!.Documents.Single().Revision.Value );
		}

		Assert.AreEqual( 3L, repository.Find( "barney" )!.Revision.Value );
	}

	[TestMethod]
	public async Task RequireUnchangedIsARevisionGuardWithoutCreatingAMutation()
	{
		await using var provider = new InMemoryPersistenceProvider( PersistenceTestSupport.CreateRegistry() );
		await provider.InitializeAsync();
		var repository = provider.Repository<TestDocument>( "characters" );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "alyx", new TestDocument( "Alyx", 1 ) );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}

		var observed = repository.Find( "alyx" )!;
		var sequenceBeforeGuard = provider.Health.Sequence;
		await using ( var guard = provider.BeginUnitOfWork() )
		{
			guard.RequireUnchanged( repository, observed );
			var unchanged = await guard.CommitAsync();
			Assert.IsTrue( unchanged.Succeeded, unchanged.Error?.Message );
			Assert.AreEqual( sequenceBeforeGuard, unchanged.Value!.Sequence );
			Assert.IsEmpty( unchanged.Value.Documents );
		}
		Assert.AreEqual( sequenceBeforeGuard, provider.Health.Sequence );
		Assert.AreEqual( observed.Revision, repository.Find( "alyx" )!.Revision );

		await using var staleGuard = provider.BeginUnitOfWork();
		staleGuard.RequireUnchanged( repository, observed );
		await using ( var winner = provider.BeginUnitOfWork() )
		{
			var editor = winner.Edit( repository, observed )!;
			editor.Replace( editor.Value with { Score = 2 } );
			winner.Save( editor );
			Assert.IsTrue( (await winner.CommitAsync()).Succeeded );
		}

		var conflict = await staleGuard.CommitAsync();
		Assert.AreEqual( PersistenceErrorCode.RevisionConflict, conflict.Error!.Code );
		Assert.AreEqual( 2, repository.Find( "alyx" )!.Value.Score );
	}

	[TestMethod]
	public async Task IncrementalInvariantPublishesOnlyAfterCommittedStateIsVisible()
	{
		var invariants = new RecordingIncrementalInvariantSet();
		await using var provider = new InMemoryPersistenceProvider(
			PersistenceTestSupport.CreateRegistry(),
			invariants );
		await provider.InitializeAsync();
		var repository = provider.Repository<CollectionTestDocument>( "characters" );
		invariants.ProviderStateIsPublished = () => repository.Find( "alyx" ) is not null;

		await using var create = provider.BeginUnitOfWork();
		create.Create( repository, "alyx", new CollectionTestDocument { Name = "Alyx" } );
		var result = await create.CommitAsync();

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 1, invariants.FullValidationCalls );
		Assert.AreEqual( 1, invariants.RebuildCalls );
		Assert.AreEqual( 1, invariants.PrepareCalls );
		Assert.AreEqual( 1, invariants.PublishCalls );
		Assert.IsTrue( invariants.ProviderStateWasPublished );
	}

	[TestMethod]
	public async Task IncrementalInvariantRejectionDoesNotPublishOrExposeCandidateState()
	{
		var invariants = new RecordingIncrementalInvariantSet { RejectPreparation = true };
		await using var provider = new InMemoryPersistenceProvider(
			PersistenceTestSupport.CreateRegistry(),
			invariants );
		await provider.InitializeAsync();
		var repository = provider.Repository<CollectionTestDocument>( "characters" );

		await using var create = provider.BeginUnitOfWork();
		create.Create( repository, "alyx", new CollectionTestDocument { Name = "Alyx" } );
		var result = await create.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.InvariantViolation, result.Error!.Code );
		Assert.AreEqual( 1, invariants.PrepareCalls );
		Assert.AreEqual( 0, invariants.PublishCalls );
		Assert.IsNull( repository.Find( "alyx" ) );
		Assert.AreEqual( PersistenceProviderState.Ready, provider.State );
	}

	[TestMethod]
	public async Task IncrementalInvariantPublishFailureFaultsProviderAfterDurableCommit()
	{
		var invariants = new RecordingIncrementalInvariantSet { ThrowOnPublish = true };
		await using var provider = new InMemoryPersistenceProvider(
			PersistenceTestSupport.CreateRegistry(),
			invariants );
		await provider.InitializeAsync();
		var repository = provider.Repository<CollectionTestDocument>( "characters" );

		await using var create = provider.BeginUnitOfWork();
		create.Create( repository, "alyx", new CollectionTestDocument { Name = "Alyx" } );
		var result = await create.CommitAsync();

		Assert.IsFalse( result.Succeeded );
		Assert.AreEqual( PersistenceErrorCode.DurabilityFailed, result.Error!.Code );
		Assert.AreEqual( 1, invariants.PrepareCalls );
		Assert.AreEqual( 1, invariants.PublishCalls );
		Assert.AreEqual( PersistenceProviderState.Faulted, provider.State );
		Assert.AreEqual( PersistenceHealthStatus.Fatal, provider.Health.Status );
	}

	private sealed class RecordingIncrementalInvariantSet : IIncrementalPersistenceInvariantSet
	{
		public bool RejectPreparation { get; init; }
		public bool ThrowOnPublish { get; init; }
		public Func<bool>? ProviderStateIsPublished { get; set; }
		public bool ProviderStateWasPublished { get; private set; }
		public int FullValidationCalls { get; private set; }
		public int RebuildCalls { get; private set; }
		public int PrepareCalls { get; private set; }
		public int PublishCalls { get; private set; }

		public IReadOnlyList<PersistenceInvariantIssue> Validate( PersistenceInvariantContext context )
		{
			FullValidationCalls++;
			return Array.Empty<PersistenceInvariantIssue>();
		}

		public PersistenceInvariantPreparation Prepare( PersistenceInvariantContext context )
		{
			PrepareCalls++;
			return RejectPreparation
				? new PersistenceInvariantPreparation(
					[new PersistenceInvariantIssue( "test.reject", "characters/alyx", "Rejected for testing." )] )
				: new PersistenceInvariantPreparation( Array.Empty<PersistenceInvariantIssue>(), new object() );
		}

		public void Publish( PersistenceInvariantPreparation preparation )
		{
			PublishCalls++;
			ProviderStateWasPublished = ProviderStateIsPublished?.Invoke() ?? false;
			if ( ThrowOnPublish ) throw new InvalidOperationException( "Incremental invariant publication failed." );
		}

		public void Rebuild( PersistenceInvariantContext context )
		{
			RebuildCalls++;
		}
	}
}
