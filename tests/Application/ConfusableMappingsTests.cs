using Hexagon.V2.Application;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class ConfusableMappingsTests
{
	[TestMethod]
	public void TableIsPinnedToAReviewedUnicodeRelease()
	{
		// The table decides who is allowed to impersonate whom, so it is pinned by version and by
		// the SHA-256 of the upstream file. Changing either is a reviewed act: rerun
		// tools/generate-confusables.ps1, which refuses to build a table from unpinned data.
		Assert.AreEqual( "16.0.0", ConfusableMappings.UnicodeVersion );
		Assert.AreEqual(
			"95BD0AAD6DCED5EBC63436F459C06AB21A8D107CD842FB57F5C3A1E91BCA8611",
			ConfusableMappings.SourceSha256 );
		Assert.AreEqual( 6355, ConfusableMappings.EntryCount );
	}

	[TestMethod]
	public void EveryPinnedEntryIsReachableThroughTheBinarySearch()
	{
		// Sweeping the whole code space proves the parallel arrays agree in length, that the
		// source array is sorted (an unsorted entry would be unreachable), and that every
		// sequence index resolves - the ways a hand-edited generated file goes wrong.
		var found = 0;
		for ( var codePoint = 0; codePoint <= 0x10FFFF; codePoint++ )
		{
			if ( !ConfusableMappings.TryMap( codePoint, out var single, out var sequence ) ) continue;
			found++;
			if ( sequence is not null ) Assert.IsGreaterThan( 0, sequence.Length );
			else Assert.IsTrue( single is >= 0 and <= 0x10FFFF );
		}

		Assert.AreEqual( ConfusableMappings.EntryCount, found );
	}

	[TestMethod]
	public void SpotChecksMatchTheUpstreamTable()
	{
		AssertMapsTo( 0x0430, "a" );   // CYRILLIC SMALL LETTER A
		AssertMapsTo( 0x0435, "e" );   // CYRILLIC SMALL LETTER IE
		AssertMapsTo( 0x0031, "l" );   // DIGIT ONE
		AssertMapsTo( 0x0049, "l" );   // LATIN CAPITAL LETTER I
		AssertMapsTo( 0x006D, "rn" );  // LATIN SMALL LETTER M - the standard expands, not collapses
		AssertMapsTo( 0x0030, "O" );   // DIGIT ZERO

		// Prototypes are not themselves sources, or folding would not terminate.
		Assert.IsFalse( ConfusableMappings.TryMap( 'a', out _, out _ ) );
		Assert.IsFalse( ConfusableMappings.TryMap( 'l', out _, out _ ) );
		Assert.IsFalse( ConfusableMappings.TryMap( 'i', out _, out _ ) );
	}

	private static void AssertMapsTo( int codePoint, string expected )
	{
		Assert.IsTrue(
			ConfusableMappings.TryMap( codePoint, out var single, out var sequence ),
			$"U+{codePoint:X4} should be a confusable source." );
		var actual = sequence ?? char.ConvertFromUtf32( single );
		Assert.AreEqual( expected, actual, $"U+{codePoint:X4}" );
	}
}