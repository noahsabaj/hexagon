using Hexagon.V2.Application;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class SceneIdentityValidatorTests
{
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
	public void RebuildAfterDynamicDuplicateFailsClosedForBothIdentities()
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
			new SceneIdentityCandidate( "dynamic", duplicate )
		} );

		Assert.IsFalse( rebuilt.TryResolveId( duplicate, out _ ) );
		Assert.IsTrue( rebuilt.Resolutions.All( resolution => !resolution.Enabled ) );
	}
}
