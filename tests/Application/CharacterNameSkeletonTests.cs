using Hexagon.V2.Application;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class CharacterNameSkeletonTests
{
	[TestMethod]
	public void WithinScriptLookAlikesCollapseOntoTheNameTheyImitate()
	{
		var alice = CharacterNameSkeleton.Of( "Alice" );
		// Digit-for-letter and the i/l/1 shape collision.
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "A1ice" ) );
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "Alice" ) );
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "AIice" ) );
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "4lice" ) );
		// Separators and punctuation carry no identity.
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "A l i c e" ) );
		Assert.AreEqual( alice, CharacterNameSkeleton.Of( "A-l-i-c-e" ) );
		// Accents are decoration, not identity.
		Assert.AreEqual( CharacterNameSkeleton.Of( "Jose" ), CharacterNameSkeleton.Of( "Jos\u00E9" ) );
		// The classic digraph pair.
		Assert.AreEqual( CharacterNameSkeleton.Of( "Bemard" ), CharacterNameSkeleton.Of( "Bernard" ) );
		Assert.AreEqual( CharacterNameSkeleton.Of( "Walker" ), CharacterNameSkeleton.Of( "VValker" ) );
	}

	[TestMethod]
	public void WholeNameWrittenInAnotherScriptCollapsesOntoTheLatinNameItImitates()
	{
		// The script profile permits these - they are single-script names. Only the skeleton
		// sees that they are drawn to read as "Alice" and "Petro".
		// Cyrillic es-o-o-er-ie-ghe, which draws "Cooper" without one Latin letter in it.
		Assert.AreEqual(
			CharacterNameSkeleton.Of( "Cooper" ),
			CharacterNameSkeleton.Of( "\u0441\u043E\u043E\u0440\u0435\u0433" ) );
		// Greek rho-omicron-rho-omicron-nu, which draws "Popov".
		Assert.AreEqual(
			CharacterNameSkeleton.Of( "Popov" ),
			CharacterNameSkeleton.Of( "\u03C1\u03BF\u03C1\u03BF\u03BD" ) );
	}

	[TestMethod]
	public void FoldingFollowsUnicodeRatherThanLocalIntuition()
	{
		// A documented residual, asserted so a table bump surfaces it rather than hiding it:
		// UTS #39 judges Cyrillic em confusable with a turned w, NOT with Latin m, so a Cyrillic
		// name using it does not collapse onto the Latin name it arguably resembles. The script
		// profile still blocks the mixed-script form, which is the reachable attack.
		Assert.AreNotEqual(
			CharacterNameSkeleton.Of( "Maxwell" ),
			CharacterNameSkeleton.Of( "\u043C\u0430\u0445\u051D\u0435\u0456\u0456" ) );

		// Latin i and l stay distinct - the standard folds the digit 1, capital I and the bar
		// onto l, but never merges lowercase i with it.
		Assert.AreEqual( CharacterNameSkeleton.Of( "Alice" ), CharacterNameSkeleton.Of( "A1ice" ) );
		Assert.AreNotEqual( CharacterNameSkeleton.Of( "Bili" ), CharacterNameSkeleton.Of( "Bill" ) );
	}

	[TestMethod]
	public void DistinctNamesKeepDistinctSkeletons()
	{
		Assert.AreNotEqual( CharacterNameSkeleton.Of( "Alice" ), CharacterNameSkeleton.Of( "Alicia" ) );
		Assert.AreNotEqual( CharacterNameSkeleton.Of( "Alice" ), CharacterNameSkeleton.Of( "Alison" ) );
		Assert.AreNotEqual( CharacterNameSkeleton.Of( "Barney" ), CharacterNameSkeleton.Of( "Gordon" ) );
		Assert.AreNotEqual( CharacterNameSkeleton.Of( "Judith Mossman" ), CharacterNameSkeleton.Of( "Eli Vance" ) );
		// A name in a script with no Latin look-alikes is left alone rather than flattened.
		Assert.AreNotEqual(
			CharacterNameSkeleton.Of( "\u0410\u043B\u0438\u0441\u0430" ),      // Cyrillic "Alisa"
			CharacterNameSkeleton.Of( "\u0411\u043E\u0440\u0438\u0441" ) );    // Cyrillic "Boris"
	}

	[TestMethod]
	public void NameWithNoIdentityBearingCharactersHasAnEmptySkeleton()
	{
		Assert.AreEqual( string.Empty, CharacterNameSkeleton.Of( "..." ) );
		Assert.AreEqual( string.Empty, CharacterNameSkeleton.Of( "---" ) );
		Assert.AreEqual( string.Empty, CharacterNameSkeleton.Of( string.Empty ) );
	}
}