#nullable enable

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Hexagon.V2.Composition;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using KernelConfigDefinition = Hexagon.V2.Kernel.Configuration.ConfigDefinition<int>;

namespace Hexagon.V2.Tests.Composition;

[TestClass]
public sealed class TypedConfigurationStoreTests
{
	[TestMethod]
	public async Task DefaultsAndUpdatesRoundTripExactlyThroughWalRecovery()
	{
		var storage = new InMemoryPersistenceStorage();
		var compiled = CompileSchema();
		var firstBindings = Bind( compiled, new PersistedTypeRegistry().RegisterHexagonDomainTypes() );
		await using ( var first = Provider( storage, firstBindings.Types ) )
		{
			await first.InitializeAsync();
			var configuration = new TypedConfigurationStore( first, firstBindings.PersistenceConfigs );
			var initialized = await configuration.InitializeAsync();
			Assert.IsTrue( initialized.Succeeded, initialized.Error?.Message );
			var defaultValue = configuration.Get<int>( "starting_tokens" );
			Assert.IsTrue( defaultValue.Succeeded, defaultValue.Error?.Message );
			Assert.AreEqual( 25, defaultValue.Value.Value );

			var changed = await configuration.SetAsync(
				"starting_tokens", 41, defaultValue.Value.Revision );
			Assert.IsTrue( changed.Succeeded, changed.Error?.Message );
			Assert.AreEqual( 41, changed.Value.Value );
			Assert.AreEqual( 2L, changed.Value.Revision.Value );
			var raw = first.Repository<PersistedConfigRecord>( DomainCollections.Configuration )
				.Find( "starting_tokens" )!;
			Assert.AreEqual( "starting_tokens", raw.Value.Key );
			Assert.AreEqual( "int32", raw.Value.ValueTypeId );
			Assert.AreEqual( "41", raw.Value.EncodedValue.GetString() );
			var snapshot = configuration.Snapshot();
			Assert.HasCount( 1, snapshot );
			Assert.AreEqual( "starting_tokens", snapshot[0].Key );
			Assert.AreEqual( "int32", snapshot[0].ValueTypeId );
			Assert.AreEqual( 2L, snapshot[0].Revision.Value );
			Assert.AreEqual( "\"41\"", snapshot[0].CanonicalEncodedValue );
			Assert.IsTrue( (await first.ShutdownAsync()).IsClean );
		}

		var recoveredBindings = Bind( compiled, new PersistedTypeRegistry().RegisterHexagonDomainTypes() );
		await using var recovered = Provider( storage, recoveredBindings.Types );
		await recovered.InitializeAsync();
		var recoveredConfiguration = new TypedConfigurationStore(
			recovered, recoveredBindings.PersistenceConfigs );
		var recoveredInitialization = await recoveredConfiguration.InitializeAsync();
		Assert.IsTrue( recoveredInitialization.Succeeded, recoveredInitialization.Error?.Message );
		var recoveredValue = recoveredConfiguration.Get<int>( "starting_tokens" );
		Assert.IsTrue( recoveredValue.Succeeded, recoveredValue.Error?.Message );
		Assert.AreEqual( 41, recoveredValue.Value.Value );
		Assert.AreEqual( 2L, recoveredValue.Value.Revision.Value );
	}

	[TestMethod]
	public async Task StaleRevisionAndWrongGenericTypeFailClosedWithoutChangingValue()
	{
		var bindings = Bind( CompileSchema(), new PersistedTypeRegistry().RegisterHexagonDomainTypes() );
		await using var provider = new InMemoryPersistenceProvider( bindings.Types );
		await provider.InitializeAsync();
		var configuration = new TypedConfigurationStore( provider, bindings.PersistenceConfigs );
		Assert.IsTrue( (await configuration.InitializeAsync()).Succeeded );
		var initial = configuration.Get<int>( "starting_tokens" ).Value;
		var first = await configuration.SetAsync( "starting_tokens", 30, initial.Revision );
		var stale = await configuration.SetAsync( "starting_tokens", 31, initial.Revision );
		var wrongType = configuration.Get<long>( "starting_tokens" );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.IsTrue( stale.Failed );
		Assert.AreEqual( ErrorCode.Conflict, stale.Error!.Code );
		Assert.IsTrue( wrongType.Failed );
		Assert.AreEqual( ErrorCode.ConfigurationTypeMismatch, wrongType.Error!.Code );
		Assert.AreEqual( 30, configuration.Get<int>( "starting_tokens" ).Value.Value );
	}

	[TestMethod]
	public async Task UnknownMalformedAndRetypedDocumentsAreRejectedAtStartup()
	{
		var bindings = Bind( CompileSchema(), new PersistedTypeRegistry().RegisterHexagonDomainTypes() );
		await using var provider = new InMemoryPersistenceProvider( bindings.Types );
		await provider.InitializeAsync();
		var repository = provider.Repository<PersistedConfigRecord>( DomainCollections.Configuration );
		await using ( var seed = provider.BeginUnitOfWork() )
		{
			seed.Create( repository, "unknown", new PersistedConfigRecord
			{
				Key = "unknown",
				ValueTypeId = "int32",
				EncodedValue = JsonSerializer.SerializeToElement( "1" )
			} );
			Assert.IsTrue( (await seed.CommitAsync()).Succeeded );
		}
		var unknown = new TypedConfigurationStore( provider, bindings.PersistenceConfigs );
		var unknownResult = await unknown.InitializeAsync();
		Assert.IsTrue( unknownResult.Failed );
		Assert.AreEqual( ErrorCode.UnknownDefinition, unknownResult.Error!.Code );

		await using ( var replace = provider.BeginUnitOfWork() )
		{
			replace.Delete( repository, repository.Find( "unknown" )! );
			replace.Create( repository, "starting_tokens", new PersistedConfigRecord
			{
				Key = "starting_tokens",
				ValueTypeId = "int64",
				EncodedValue = JsonSerializer.SerializeToElement( "not-an-integer" )
			} );
			Assert.IsTrue( (await replace.CommitAsync()).Succeeded );
		}
		var malformed = new TypedConfigurationStore( provider, bindings.PersistenceConfigs );
		var malformedResult = await malformed.InitializeAsync();
		Assert.IsTrue( malformedResult.Failed );
		Assert.AreEqual( ErrorCode.ConfigurationTypeMismatch, malformedResult.Error!.Code );
	}

	private static CompiledSchema CompileSchema()
	{
		var result = SchemaCompiler.Compile( new ConfigurationSchema() );
		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		return result.Value;
	}

	private static SchemaPersistenceBindings Bind(
		CompiledSchema schema,
		PersistedTypeRegistry registry )
	{
		var result = SchemaPersistenceAdapter.Bind(
			schema, registry, Array.Empty<IPersistedTypeCodec>() );
		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		return result.Value;
	}

	private static FileSystemPersistenceProvider Provider(
		IPersistenceStorage storage,
		PersistedTypeRegistry registry ) => new(
		storage,
		new FileSystemPersistenceOptions( "config-test" ),
		registry );

	private sealed class ConfigurationSchema : IHexSchema
	{
		public string Id => "config_test";

		public void Configure( SchemaBuilder builder ) => builder.RegisterConfig(
			new KernelConfigDefinition(
				"starting_tokens",
				25,
				ConfigCodecs.Int32,
				value => value is >= 0 and <= 100
					? OperationResult.Success()
					: OperationResult.Failure( ErrorCode.ConfigurationInvalid, "Out of range." ) ) );
	}
}
