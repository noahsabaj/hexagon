using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Tests.Kernel;

[TestClass]
public sealed class SchemaCompilerTests
{
	[TestMethod]
	public void CompileOrdersModulesDeterministicallyAndResolvesDefinitions()
	{
		var schema = new DelegateSchema("test_schema", builder =>
		{
			builder.AddModule(new DelegateModule("zeta", _ => { }));
			builder.AddModule(new DelegateModule("gamma", module => module.DependsOn("beta")));
			builder.AddModule(new DelegateModule("beta", module =>
			{
				module.DependsOn("alpha");
				module.RegisterPermission(new PermissionDefinition("admin"));
				module.RegisterCommand(new CommandDefinition("ban", "admin"));
			}));
			builder.AddModule(new DelegateModule("alpha", module =>
			{
				module.RegisterFaction(new FactionDefinition("citizen", true, "worker"));
				module.RegisterClass(new ClassDefinition("worker", "citizen"));
			}));
		});

		var result = SchemaCompiler.Compile(schema);

		Assert.IsTrue(result.Succeeded, result.Error?.Message);
		CollectionAssert.AreEqual(
			new[] { "alpha", "beta", "gamma", "zeta" },
			result.Value.Modules.Select(x => x.Id).ToArray());
		Assert.AreEqual("ban", result.Value.Commands.Require("ban").Value.Id);
	}

	[TestMethod]
	public void ValidationReportsCyclesMissingReferencesAndDuplicateDefinitions()
	{
		var schema = new DelegateSchema("test_schema", builder =>
		{
			builder.RegisterAction(new ActionDefinition("use"));
			builder.RegisterAction(new ActionDefinition("use"));
			builder.RegisterClass(new ClassDefinition("unit", "missing_faction"));
			builder.RegisterItem(ItemDefinition.Create("broken_item", true, null, "missing_action"));
			builder.AddModule(new DelegateModule("first", module => module.DependsOn("second")));
			builder.AddModule(new DelegateModule("second", module => module.DependsOn("first")));
			builder.AddModule(new DelegateModule("third", module => module.DependsOn("absent")));
		});

		var report = SchemaCompiler.Validate(schema);

		Assert.IsFalse(report.IsValid);
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.DependencyCycle));
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.MissingDependency));
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.DuplicateRegistration));
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.UnknownDefinition));
		Assert.IsTrue(report.Issues.Any(x => x.Message.Contains("world model", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void UnknownDefinitionAndConfigTypeMismatchFailClosed()
	{
		var schema = new DelegateSchema("test_schema", builder =>
		{
			builder.RegisterAction(new ActionDefinition("use"));
			builder.RegisterConfig(new ConfigDefinition<int>("max_characters", 5, ConfigCodecs.Int32));
		});
		var compiled = SchemaCompiler.Compile(schema).Value;

		var missing = compiled.Actions.Require("equip");
		var wrongType = compiled.Configs.Require<long>("max_characters");

		Assert.IsTrue(missing.Failed);
		Assert.AreEqual(ErrorCode.UnknownDefinition, missing.Error!.Code);
		Assert.IsTrue(wrongType.Failed);
		Assert.AreEqual(ErrorCode.ConfigurationTypeMismatch, wrongType.Error!.Code);
	}

	[TestMethod]
	public void PersistedRegistrationsRequireUniqueConcreteTypesAndPositiveVersions()
	{
		var schema = new DelegateSchema("test_schema", builder =>
		{
			builder.RegisterPersistedType(new PersistedTypeRegistration("first", typeof(Payload), 1));
			builder.RegisterPersistedType(new PersistedTypeRegistration("second", typeof(Payload), 1));
			builder.RegisterPersistedType(new PersistedTypeRegistration("abstract", typeof(AbstractPayload), 0));
		});

		var report = SchemaCompiler.Validate(schema);

		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.DuplicateRegistration));
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.PersistedTypeInvalid &&
			x.Message.Contains("positive version", StringComparison.Ordinal)));
		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.PersistedTypeInvalid &&
			x.Message.Contains("closed concrete", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void InvalidConfigDefaultFailsSchemaConformance()
	{
		var schema = new DelegateSchema("test_schema", builder =>
			builder.RegisterConfig(new ConfigDefinition<int>("slots", 0, ConfigCodecs.Int32,
				value => value > 0
					? OperationResult.Success()
					: OperationResult.Failure(ErrorCode.ConfigurationInvalid, "Slots must be positive."))));

		var report = SchemaCompiler.Validate(schema);

		Assert.IsTrue(report.Issues.Any(x => x.Code == ErrorCode.ConfigurationInvalid));
	}

	[TestMethod]
	public void CharacterFieldKindsRejectMismatchedDefaultsWithoutReflection()
	{
		var schema = new DelegateSchema("test_schema", builder =>
			builder.RegisterCharacterField(new CharacterFieldDefinition(
				"age", CharacterFieldValueKind.Integer, true, true, "not-an-integer")));

		var report = SchemaCompiler.Validate(schema);

		Assert.IsTrue(report.Issues.Any(x => x.Path == "character_fields.age" &&
			x.Message.Contains("does not match Integer", StringComparison.Ordinal)));
	}

	private sealed record Payload(string Value);
	private abstract record AbstractPayload;

	private sealed class DelegateSchema : IHexSchema
	{
		private readonly Action<SchemaBuilder> _configure;

		public DelegateSchema(string id, Action<SchemaBuilder> configure)
		{
			Id = id;
			_configure = configure;
		}

		public string Id { get; }
		public void Configure(SchemaBuilder builder) => _configure(builder);
	}

	private sealed class DelegateModule : IHexModule
	{
		private readonly Action<ModuleBuilder> _configure;

		public DelegateModule(string id, Action<ModuleBuilder> configure)
		{
			Id = id;
			_configure = configure;
		}

		public string Id { get; }
		public void Configure(ModuleBuilder builder) => _configure(builder);
	}
}
