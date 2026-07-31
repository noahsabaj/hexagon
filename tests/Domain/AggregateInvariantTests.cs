using System.Text.Json;
using Hexagon.V2.Domain;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.V2.Tests.Domain;

[TestClass]
public sealed class AggregateInvariantTests
{
	[TestMethod]
	public void BalanceCannotBecomeNegative()
	{
		var character = CreateCharacter();
		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => character.WithBalance( -1 ) );
	}

	[TestMethod]
	public void LocationIndexRejectsDuplicateParenting()
	{
		var itemId = ItemId.New();
		var first = CreateInventory( itemId );
		var second = CreateInventory( itemId );
		var index = new ItemLocationIndex();

		Assert.ThrowsExactly<InvalidOperationException>( () => index.Build( [first, second], [] ) );
	}

	[TestMethod]
	public void ItemCloneOwnsTraitJson()
	{
		using var document = JsonDocument.Parse( "{\"value\":7}" );
		var item = new ItemRecord
		{
			Id = ItemId.New(),
			Definition = new DefinitionId( "test.item" ),
			Traits = new Dictionary<string, TypedPayload>
			{
				["test.trait"] = new()
				{
					TypeId = new PersistedTypeId( "test.trait" ),
					TypeVersion = 1,
					Data = document.RootElement.Clone()
				}
			}
		};

		var clone = item.DeepCopy();
		Assert.AreEqual( 7, clone.Traits["test.trait"].Data.GetProperty( "value" ).GetInt32() );
	}

	private static CharacterRecord CreateCharacter()
	{
		using var document = JsonDocument.Parse( "{}" );
		return new CharacterRecord
		{
			Id = CharacterId.New(),
			AccountId = new AccountId( 1 ),
			Slot = 0,
			Name = "Test",
			Description = "Test character description",
			Model = new DefinitionId( "test.model" ),
			Faction = new FactionId( "test.faction" ),
			Balance = 0,
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

	private static InventoryRecord CreateInventory( ItemId itemId ) => new()
	{
		Id = InventoryId.New(),
		Owner = InventoryOwner.Character( CharacterId.New() ),
		Width = 4,
		Height = 4,
		Placements = [new InventoryPlacement( itemId, 0, 0 )]
	};
}
