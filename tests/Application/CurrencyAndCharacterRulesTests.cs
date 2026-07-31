using System.Text.Json;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class CurrencyAndCharacterRulesTests
{
	[TestMethod]
	public void CurrencyRejectsUnderflowAndOverflow()
	{
		var character = CreateCharacter( 10 );
		Assert.IsTrue( CurrencyService.Debit( character, 11 ).Failed );
		Assert.IsTrue( CurrencyService.Credit( character with { Balance = long.MaxValue }, 1 ).Failed );
	}

	[TestMethod]
	public void SlotAllocatorUsesLowestGap()
	{
		var characters = new[] { CreateCharacter( 0 ) with { Slot = 0 }, CreateCharacter( 0 ) with { Slot = 2 } };
		Assert.AreEqual( 1, CharacterRules.FindLowestFreeSlot( characters ) );
	}

	[TestMethod]
	public void SlotAllocatorReturnsMinusOneWhenEverySlotIsOccupied()
	{
		var characters = Enumerable.Range( 0, CharacterRules.MaximumSlots )
			.Select( slot => CreateCharacter( 0 ) with { Slot = slot } )
			.ToArray();
		Assert.AreEqual( -1, CharacterRules.FindLowestFreeSlot( characters ) );

		var oneFree = characters.Where( character => character.Slot != CharacterRules.MaximumSlots - 1 ).ToArray();
		Assert.AreEqual( CharacterRules.MaximumSlots - 1, CharacterRules.FindLowestFreeSlot( oneFree ) );
	}

	[TestMethod]
	public void CreationRejectsUnregisteredFields()
	{
		var request = new CharacterCreationRequest
		{
			Name = "Valid Name",
			Description = "A sufficiently long character description.",
			Model = new DefinitionId( "test.model" ),
			Faction = new FactionId( "test.faction" ),
			Fields = new Dictionary<string, CreationValue> { ["money"] = CreationValue.Integer( long.MaxValue ) }
		};

		Assert.IsTrue( CharacterRules.ValidateCreationRequest( request, new HashSet<string>() ).Failed );
	}

	[TestMethod]
	public void IdentityTextRejectsControlAndFormatCharacters()
	{
		// A name reaches other players verbatim through recognition records and nameplates, so
		// spoofing payloads must not survive the creation boundary. These are written as escapes
		// on purpose: as literals they are invisible in an editor and lost by copy/paste.
		foreach ( var name in new[]
		{
			"Bob\nCombine Overwatch", // U+000A interior line break
			"Bob\u0007Smith",         // C0 control
			"Bob\u202Eelbat",         // right-to-left override
			"Bo\u200Bb Smith",        // zero-width space
			"Bob\u200DSmith",         // zero-width joiner
			"Bob\u2066Smith\u2069",   // bidirectional isolates
			"Bob\uFEFFSmith"          // zero-width no-break space
		} )
		{
			Assert.IsTrue(
				CharacterRules.NormalizeName( name ).Failed,
				$"Name '{name.Replace( "\n", "\\n" )}' should have been rejected." );
			Assert.IsTrue( CharacterRules.ValidateCreationRequest( Request( name: name ), NoFields ).Failed );
		}

		// Zero-width characters are not whitespace, so they survive Trim: a length check
		// alone can never catch a name built entirely from them.
		Assert.IsTrue( CharacterRules.NormalizeName( "\u200B\u200B\u200B\u200B" ).Failed );
	}

	[TestMethod]
	public void DescriptionKeepsLineBreaksButRejectsOtherControlCharacters()
	{
		var prose = "A tall citizen in a worn jacket.\nHe keeps his hands in his pockets.";
		Assert.AreEqual( prose, CharacterRules.NormalizeDescription( prose ).Value );
		Assert.IsTrue( CharacterRules.NormalizeDescription( "A citizen\u202Ewith a reversed tail." ).Failed );
		Assert.IsTrue( CharacterRules.NormalizeDescription( "A citizen with a\u0000 null byte." ).Failed );
	}

	[TestMethod]
	public void IdentityTextIsCanonicalizedSoValidationAndStorageAgree()
	{
		// Combining and precomposed acutes render identically, so they must not be
		// storable as two distinct names.
		var decomposed = CharacterRules.NormalizeName( "  Ame\u0301lie  " );
		Assert.IsTrue( decomposed.Succeeded );
		Assert.AreEqual( "Am\u00E9lie", decomposed.Value );
		Assert.AreEqual( decomposed.Value, CharacterRules.NormalizeName( "Am\u00E9lie" ).Value );

		// An unpaired surrogate is rejected rather than thrown out of Normalize.
		Assert.IsTrue( CharacterRules.NormalizeName( "Bob\uD800Smith" ).Failed );

		// Ordinary names still pass.
		Assert.IsTrue( CharacterRules.NormalizeName( "Valid Name" ).Succeeded );
		Assert.IsTrue( CharacterRules.NormalizeName( "Baßmann-O'Neill" ).Succeeded );
		Assert.IsTrue( CharacterRules.ValidateCreationRequest( Request(), NoFields ).Succeeded );
	}

	[TestMethod]
	public void NameRejectsMixedScriptImpersonation()
	{
		// Each of these renders as ordinary Latin text but smuggles one letter from another
		// script, which is exactly how a name is made to look like someone else's.
		Assert.IsTrue( CharacterRules.NormalizeName( "\u0410lice" ).Failed );  // Cyrillic A
		Assert.IsTrue( CharacterRules.NormalizeName( "Alic\u0435" ).Failed );  // Cyrillic e
		Assert.IsTrue( CharacterRules.NormalizeName( "\u03A1eter" ).Failed );  // Greek Rho
		Assert.IsTrue( CharacterRules.NormalizeName( "\u13AAlice" ).Failed );  // Cherokee
		Assert.IsTrue( CharacterRules.ValidateCreationRequest( Request( name: "\u0410lice" ), NoFields ).Failed );

		// A wholly Cyrillic or Greek name is a real name, not an impersonation.
		Assert.IsTrue( CharacterRules.NormalizeName( "Алиса" ).Succeeded );
		Assert.IsTrue( CharacterRules.NormalizeName( "Γεωργος" ).Succeeded );
	}

	[TestMethod]
	public void NameAllowsTheScriptCombinationsRealNamesNeed()
	{
		// Japanese mixes Han, Hiragana and Katakana by nature, and may carry Latin.
		Assert.IsTrue( CharacterRules.NormalizeName( "田中 ひろし" ).Succeeded );
		Assert.IsTrue( CharacterRules.NormalizeName( "タナカ Tanaka" ).Succeeded );
		// Korean mixes Hangul with Latin.
		Assert.IsTrue( CharacterRules.NormalizeName( "김철수 Kim" ).Succeeded );
		// Digits, spaces and punctuation are script-neutral and never constrain a name.
		Assert.IsTrue( CharacterRules.NormalizeName( "Dr. Judith Mossman II" ).Succeeded );
		// Greek alongside Latin is not a combination any real name needs.
		Assert.IsTrue( CharacterRules.NormalizeName( "Alice Γεω" ).Failed );
	}

	[TestMethod]
	public void NameFoldsCompatibilityLookAlikesOntoWhatTheyImitate()
	{
		// Fullwidth Latin is Latin script, so mixed-script detection cannot see it. NFKC folding
		// is what stops it being a second, visually identical spelling of an existing name.
		var fullwidth = CharacterRules.NormalizeName( "\uFF21lice" );
		Assert.IsTrue( fullwidth.Succeeded );
		Assert.AreEqual( "Alice", fullwidth.Value );

		// A description is prose, not identity: it keeps compatibility characters and may mix
		// scripts freely. Only its control- and format-character rules apply.
		Assert.IsTrue( CharacterRules.NormalizeDescription(
			"A citizen who mutters Алиса under their breath." ).Succeeded );
		Assert.IsTrue( CharacterRules.NormalizeDescription(
			"A citizen wearing a \uFF21-series jumpsuit, collar upturned." ).Succeeded );
	}

	[TestMethod]
	public void TwoUnenumeratedScriptsAreStillTwoScripts()
	{
		// Neither Coptic nor Runic is in the named script table. They must not share one "other"
		// bucket, or a name could mix two writing systems the profile never actually looked at.
		Assert.IsTrue( CharacterRules.NormalizeName( "\u2C81\u2C83\u2C85" ).Succeeded );  // Coptic alone
		Assert.IsTrue( CharacterRules.NormalizeName( "\u16A0\u16A2\u16A6" ).Succeeded );  // Runic alone
		Assert.IsTrue( CharacterRules.NormalizeName( "\u2C81\u2C83\u16A0" ).Failed );     // the two mixed
		Assert.IsTrue( CharacterRules.NormalizeName( "\u2C81\u2C83Ab" ).Failed );         // one with Latin
	}
	private static IReadOnlySet<string> NoFields => new HashSet<string>();

	private static CharacterCreationRequest Request( string name = "Valid Name" ) =>
		new()
		{
			Name = name,
			Description = "A sufficiently long character description.",
			Model = new DefinitionId( "test.model" ),
			Faction = new FactionId( "test.faction" ),
			Fields = new Dictionary<string, CreationValue>()
		};

	private static CharacterRecord CreateCharacter( long balance )
	{
		using var document = JsonDocument.Parse( "{}" );
		return new CharacterRecord
		{
			Id = CharacterId.New(),
			AccountId = new AccountId( 1 ),
			Slot = 0,
			Name = "Test",
			Description = "A sufficiently long description.",
			Model = new DefinitionId( "test.model" ),
			Faction = new FactionId( "test.faction" ),
			Balance = balance,
			CreatedAt = DateTimeOffset.UtcNow,
			LastPlayedAt = DateTimeOffset.UtcNow,
			SchemaState = new TypedPayload
			{
				TypeId = new PersistedTypeId( "test.character" ),
				TypeVersion = 1,
				Data = document.RootElement.Clone()
			}
		};
	}
}
