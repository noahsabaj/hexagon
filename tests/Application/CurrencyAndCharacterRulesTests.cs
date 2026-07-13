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
