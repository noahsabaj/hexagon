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
}
