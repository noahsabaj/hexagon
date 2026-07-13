using System.Text.Json;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Persistence;

[TestClass]
public sealed class PersistedTypeRegistryTests
{
	[TestMethod]
	public void MarkerAttributeDoesNotCauseRuntimeDiscovery()
	{
		var registry = new PersistedTypeRegistry();

		Assert.Throws<KeyNotFoundException>( () =>
			registry.Resolve( new PersistedTypeKey( "test.marked" ) ) );
	}

	[TestMethod]
	public void RegistrationRejectsAbstractAndDuplicateContracts()
	{
		Assert.Throws<ArgumentException>( () =>
			new PersistedTypeRegistry().Register<AbstractTestDocument>( new PersistedTypeKey( "test.abstract" ), 1 ) );

		var registry = new PersistedTypeRegistry()
			.Register<TestDocument>( new PersistedTypeKey( "test.document" ), 1,
				PersistedValuePublication.Immutable );

		Assert.Throws<InvalidOperationException>( () =>
			registry.Register<CollectionTestDocument>( new PersistedTypeKey( "test.document" ), 1,
				static value => value with
				{
					Tags = PersistedValuePublication.ReadOnlyList( value.Tags )
				} ) );
		Assert.Throws<InvalidOperationException>( () =>
			registry.Register<TestDocument>( new PersistedTypeKey( "test.other" ), 1,
				PersistedValuePublication.Immutable ) );
		Assert.Throws<ArgumentException>( () =>
			new PersistedTypeRegistry().Register<MutablePersistedTestDocument>(
				new PersistedTypeKey( "test.mutable" ), 1 ) );
		Assert.Throws<ArgumentException>( () => registry.Resolve( default ) );
	}

	[TestMethod]
	public void VersionedCodecRequiresAndAppliesContiguousMigrations()
	{
		var codec = new JsonPersistedTypeCodec<TestDocument>(
			new PersistedTypeKey( "test.document" ),
			2,
			PersistedValuePublication.Immutable,
			upgrades: new Dictionary<int, Func<JsonElement, JsonElement>>
			{
				[1] = payload => JsonSerializer.SerializeToElement( new
				{
					name = payload.GetProperty( "name" ).GetString(),
					score = 7
				} )
			} );
		using var oldJson = JsonDocument.Parse( """{"name":"Alyx"}""" );

		var migrated = codec.Deserialize( oldJson.RootElement, 1 );

		Assert.AreEqual( new TestDocument( "Alyx", 7 ), migrated );
		Assert.Throws<InvalidOperationException>( () => codec.Deserialize( oldJson.RootElement, 3 ) );

		var missingMigration = new JsonPersistedTypeCodec<TestDocument>(
			new PersistedTypeKey( "test.missing" ), 2, PersistedValuePublication.Immutable );
		Assert.Throws<InvalidOperationException>( () => missingMigration.Deserialize( oldJson.RootElement, 1 ) );
	}

	[TestMethod]
	public void TypedConfigNeverUsesRuntimeObjectConversion()
	{
		var definition = new ConfigDefinition<int>( "starting_tokens", 25, validator: value => value >= 0 );
		var encoded = definition.Serialize( 100 );

		Assert.AreEqual( 100, definition.Deserialize( encoded ) );
		Assert.Throws<ArgumentException>( () => definition.Serialize( -1 ) );
		Assert.Throws<ArgumentException>( () => ((IConfigDefinition)definition).SerializeObject( "100" ) );
	}
}
