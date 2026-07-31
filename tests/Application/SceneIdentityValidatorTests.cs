using Hexagon.V2.Application;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class SceneIdentityValidatorTests
{
	[TestMethod]
	public void ProvenanceClassifier_TrustsOnlyCapturedNonNetworkComponents()
	{
		var authored = Guid.NewGuid();
		var laterRuntime = Guid.NewGuid();
		var classifier = new SceneIdentityProvenanceClassifier();

		Assert.AreEqual(
			SceneIdentityProvenance.RuntimeOrNetwork,
			classifier.Classify( authored, hasActiveNetworkRoot: false ) );

		classifier.CaptureAuthoredSnapshot( new[] { authored } );

		Assert.IsTrue( classifier.HasAuthoredSnapshot );
		Assert.AreEqual( 1, classifier.AuthoredComponentCount );
		Assert.AreEqual(
			SceneIdentityProvenance.EditorAuthored,
			classifier.Classify( authored, hasActiveNetworkRoot: false ) );
		Assert.AreEqual(
			SceneIdentityProvenance.RuntimeOrNetwork,
			classifier.Classify( authored, hasActiveNetworkRoot: true ) );
		Assert.AreEqual(
			SceneIdentityProvenance.RuntimeOrNetwork,
			classifier.Classify( laterRuntime, hasActiveNetworkRoot: false ) );
	}

	[TestMethod]
	public void ProvenanceClassifier_NewSceneSnapshotReplacesPriorAuthority()
	{
		var firstScene = Guid.NewGuid();
		var secondScene = Guid.NewGuid();
		var classifier = new SceneIdentityProvenanceClassifier();
		classifier.CaptureAuthoredSnapshot( new[] { firstScene } );

		classifier.CaptureAuthoredSnapshot( new[] { secondScene } );

		Assert.AreEqual(
			SceneIdentityProvenance.RuntimeOrNetwork,
			classifier.Classify( firstScene, hasActiveNetworkRoot: false ) );
		Assert.AreEqual(
			SceneIdentityProvenance.EditorAuthored,
			classifier.Classify( secondScene, hasActiveNetworkRoot: false ) );
	}

	[TestMethod]
	public void EditorRepair_FillsMissingAndRepairsOnlyLaterDuplicate()
	{
		var duplicate = new SceneEntityId( Guid.Parse( "11111111-1111-1111-1111-111111111111" ) );
		var generated = new Queue<SceneEntityId>( new[]
		{
			new SceneEntityId( Guid.Parse( "22222222-2222-2222-2222-222222222222" ) ),
			new SceneEntityId( Guid.Parse( "33333333-3333-3333-3333-333333333333" ) )
		} );
		var result = SceneIdentityValidator.RepairForEditor( new[]
		{
			new SceneIdentityCandidate( "a", duplicate ),
			new SceneIdentityCandidate( "b", duplicate ),
			new SceneIdentityCandidate( "c", null )
		}, () => generated.Dequeue() );

		Assert.IsFalse( result[0].Repaired );
		Assert.IsTrue( result[1].Repaired );
		Assert.IsTrue( result[2].Repaired );
		Assert.AreNotEqual( result[0].EffectiveId, result[1].EffectiveId );
	}

	[TestMethod]
	public void RuntimeValidation_DisablesEveryAmbiguousOrBlankEntity()
	{
		var duplicate = new SceneEntityId( Guid.Parse( "11111111-1111-1111-1111-111111111111" ) );
		var result = SceneIdentityValidator.ValidateRuntime( new[]
		{
			new SceneIdentityCandidate( "a", duplicate ),
			new SceneIdentityCandidate( "b", duplicate ),
			new SceneIdentityCandidate( "c", null )
		} );

		Assert.IsTrue( result.All( value => !value.Enabled && value.FatalDiagnostic is not null ) );
	}

	[TestMethod]
	public void PersistentIndexEnumeratesTenThousandCandidatesOnceAndProvidesConstantTimeLookups()
	{
		var ids = Enumerable.Range( 0, 10_000 ).Select( _ => SceneEntityId.New() ).ToArray();
		var enumerated = 0;
		IEnumerable<SceneIdentityCandidate> Candidates()
		{
			for ( var index = 0; index < ids.Length; index++ )
			{
				enumerated++;
				yield return new SceneIdentityCandidate( $"entity/{index}", ids[index] );
			}
		}

		var index = PersistentSceneIdentityIndex.Build( Candidates() );

		Assert.AreEqual( 10_000, enumerated );
		Assert.AreEqual( 10_000, index.Count );
		for ( var candidate = 0; candidate < ids.Length; candidate++ )
		{
			Assert.IsTrue( index.TryResolveId( ids[candidate], out var byId ) );
			Assert.IsTrue( index.TryResolvePath( $"entity/{candidate}", out var byPath ) );
			Assert.AreSame( byId, byPath );
		}
	}

	[TestMethod]
	public void RuntimeNetworkDuplicateCannotPoisonEditorAuthoredIdentity()
	{
		var duplicate = SceneEntityId.New();
		var initial = PersistentSceneIdentityIndex.Build( new[]
		{
			new SceneIdentityCandidate( "first", duplicate )
		} );
		Assert.IsTrue( initial.TryResolveId( duplicate, out _ ) );

		var rebuilt = PersistentSceneIdentityIndex.Build( new[]
		{
			new SceneIdentityCandidate( "first", duplicate ),
			new SceneIdentityCandidate(
				"first", duplicate, SceneIdentityProvenance.RuntimeOrNetwork )
		} );

		Assert.IsTrue( rebuilt.TryResolveId( duplicate, out var resolved ) );
		Assert.AreEqual( "first", resolved.StablePath );
		Assert.IsTrue( rebuilt.Resolutions[0].Enabled );
		Assert.IsFalse( rebuilt.Resolutions[1].Enabled );
		Assert.AreEqual( SceneIdentityProvenance.RuntimeOrNetwork, rebuilt.Resolutions[1].Provenance );
		Assert.IsNull( rebuilt.Resolutions[1].EffectiveId );
		StringAssert.Contains( rebuilt.Resolutions[1].FatalDiagnostic, "runtime/network root" );
	}

	[TestMethod]
	public void TwoEditorAuthoredDuplicatesStillFailClosed()
	{
		var duplicate = SceneEntityId.New();
		var rebuilt = PersistentSceneIdentityIndex.Build( new[]
		{
			new SceneIdentityCandidate( "first", duplicate ),
			new SceneIdentityCandidate( "second", duplicate )
		} );

		Assert.IsFalse( rebuilt.TryResolveId( duplicate, out _ ) );
		Assert.IsTrue( rebuilt.Resolutions.All( resolution => !resolution.Enabled ) );
	}
}
