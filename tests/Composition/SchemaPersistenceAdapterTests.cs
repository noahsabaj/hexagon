using Hexagon.V2.Composition;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using KernelConfigDefinition = Hexagon.V2.Kernel.Configuration.ConfigDefinition<int>;

namespace Hexagon.V2.Tests.Composition;

[TestClass]
public sealed class SchemaPersistenceAdapterTests
{
	[TestMethod]
	public void BindValidatesAndRegistersSchemaCodecAndTypedConfigs()
	{
		var compiled = CompileSchema(2);
		var registry = new PersistedTypeRegistry();
		var codec = new JsonPersistedTypeCodec<Payload>(new PersistedTypeKey("schema.payload"), 2,
			PersistedValuePublication.Immutable,
			upgrades: new Dictionary<int, Func<System.Text.Json.JsonElement, System.Text.Json.JsonElement>>
			{
				[1] = value => value
			});

		var result = SchemaPersistenceAdapter.Bind(compiled, registry, new[] { codec });

		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		Assert.AreSame(registry, result.Value.Types);
		Assert.AreEqual(typeof(Payload), registry.Resolve(new PersistedTypeKey("schema.payload")).ClrType);
		Assert.AreEqual(4, result.Value.Configs.Require<int>("slots").Value.DefaultValue);
		Assert.HasCount(1, result.Value.PersistenceConfigs);
		var persistedConfig = result.Value.PersistenceConfigs["slots"];
		var encodedConfig = persistedConfig.SerializeObject(7);
		Assert.AreEqual(7, persistedConfig.DeserializeObject(encodedConfig));
	}

	[TestMethod]
	public void VersionMismatchFailsBeforeMutatingRegistry()
	{
		var compiled = CompileSchema(2);
		var registry = new PersistedTypeRegistry();
		var codec = new JsonPersistedTypeCodec<Payload>(new PersistedTypeKey("schema.payload"), 1,
			PersistedValuePublication.Immutable);

		var result = SchemaPersistenceAdapter.Bind(compiled, registry, new[] { codec });

		Assert.IsTrue(result.Failed);
		Assert.IsEmpty(registry.Codecs);
		StringAssert.Contains(result.Error!.Message, "does not match schema version");
	}

	[TestMethod]
	public void MissingAndUnknownCodecsFailClosed()
	{
		var compiled = CompileSchema(1);
		var missingRegistry = new PersistedTypeRegistry();
		var unknownRegistry = new PersistedTypeRegistry();

		var missing = SchemaPersistenceAdapter.Bind(compiled, missingRegistry,
			Array.Empty<IPersistedTypeCodec>());
		var unknown = SchemaPersistenceAdapter.Bind(compiled, unknownRegistry,
			new IPersistedTypeCodec[]
			{
				new JsonPersistedTypeCodec<OtherPayload>(new PersistedTypeKey("schema.other"), 1,
					PersistedValuePublication.Immutable)
			});

		Assert.IsTrue(missing.Failed);
		Assert.IsTrue(unknown.Failed);
		Assert.IsEmpty(missingRegistry.Codecs);
		Assert.IsEmpty(unknownRegistry.Codecs);
	}

	[TestMethod]
	public void ExistingCollisionFailsBeforeAddingAnySchemaCodec()
	{
		var compiled = CompileSchema(1);
		var registry = new PersistedTypeRegistry()
			.Register<OtherPayload>(new PersistedTypeKey("schema.payload"), 1,
				PersistedValuePublication.Immutable);
		var codec = new JsonPersistedTypeCodec<Payload>(new PersistedTypeKey("schema.payload"), 1,
			PersistedValuePublication.Immutable);

		var result = SchemaPersistenceAdapter.Bind(compiled, registry, new[] { codec });

		Assert.IsTrue(result.Failed);
		Assert.HasCount(1, registry.Codecs);
		Assert.AreEqual(typeof(OtherPayload), registry.Codecs.Single().ClrType);
	}

	[TestMethod]
	public void ConfigurationWithoutStablePersistenceTypeFailsBinding()
	{
		var compiled = SchemaCompiler.Compile( new UnsupportedConfigurationSchema() );
		Assert.IsTrue( compiled.Succeeded, compiled.Error?.Message );
		var result = SchemaPersistenceAdapter.Bind(
			compiled.Value,
			new PersistedTypeRegistry(),
			Array.Empty<IPersistedTypeCodec>() );

		Assert.IsTrue( result.Failed );
		Assert.AreEqual( Hexagon.V2.Kernel.ErrorCode.ConfigurationInvalid, result.Error!.Code );
		StringAssert.Contains( result.Error.Message, "stable persistence identity" );
	}

	private static CompiledSchema CompileSchema(int version)
	{
		var result = SchemaCompiler.Compile(new TestSchema(version));
		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		return result.Value;
	}

	private sealed class TestSchema : IHexSchema
	{
		private readonly int _version;
		public TestSchema(int version) => _version = version;
		public string Id => "test_schema";

		public void Configure(SchemaBuilder builder)
		{
			builder.RegisterPersistedType(
				new PersistedTypeRegistration("schema.payload", typeof(Payload), _version));
			builder.RegisterConfig(new KernelConfigDefinition("slots", 4, ConfigCodecs.Int32));
		}
	}

	private sealed record Payload(string Value);
	private sealed record OtherPayload(string Value);

	private sealed class UnsupportedConfigurationSchema : IHexSchema
	{
		public string Id => "unsupported_config";

		public void Configure( SchemaBuilder builder ) => builder.RegisterConfig(
			new Hexagon.V2.Kernel.Configuration.ConfigDefinition<DateTimeOffset>(
				"unsupported",
				DateTimeOffset.UnixEpoch,
				new UnsupportedConfigurationCodec() ) );
	}

	private sealed class UnsupportedConfigurationCodec : IConfigCodec<DateTimeOffset>
	{
		public Hexagon.V2.Kernel.OperationResult<string> Encode( DateTimeOffset value ) =>
			Hexagon.V2.Kernel.OperationResult<string>.Success( value.ToString( "O" ) );

		public Hexagon.V2.Kernel.OperationResult<DateTimeOffset> Decode( string encoded ) =>
			Hexagon.V2.Kernel.OperationResult<DateTimeOffset>.Success( DateTimeOffset.Parse( encoded ) );
	}
}
