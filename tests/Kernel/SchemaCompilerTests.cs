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
		Assert.AreEqual( CommandCostClass.Standard, result.Value.Commands.Require( "ban" ).Value.Cost );
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

	[TestMethod]
	public void CommandAdmissionCostMustBeADeclaredWeight()
	{
		var schema = new DelegateSchema( "test_schema", builder =>
			builder.RegisterCommand( new CommandDefinition(
				"invalid", null, (CommandCostClass)16 ) ) );

		var report = SchemaCompiler.Validate( schema );

		Assert.IsTrue( report.Issues.Any( issue =>
			issue.Code == ErrorCode.SchemaInvalid && issue.Path == "commands.invalid.cost" ) );
	}

	[TestMethod]
	public void ChatChannelRangeMustBeFiniteAndPositiveWhenGiven()
	{
		var report = SchemaCompiler.Validate( new DelegateSchema( "test_schema", builder =>
		{
			builder.RegisterChatChannel( new ChatChannelDefinition( "nan", Range: float.NaN ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "infinite", Range: float.PositiveInfinity ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "zero", Range: 0f ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "negative", Range: -1f ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "fine", Range: 300f ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "global" ) );
		} ) );

		CollectionAssert.AreEquivalent(
			new[]
			{
				"chat_channels.infinite.range", "chat_channels.nan.range",
				"chat_channels.negative.range", "chat_channels.zero.range"
			},
			report.Issues.Select( issue => issue.Path ).ToArray() );
		Assert.IsTrue( report.Issues.All( issue => issue.Code == ErrorCode.SchemaInvalid ) );
	}

	[TestMethod]
	public void ChatChannelPrefixesMustBeUniqueAcrossChannelsAndMustNotShadowCommands()
	{
		var report = SchemaCompiler.Validate( new DelegateSchema( "test_schema", builder =>
		{
			builder.RegisterCommand( new CommandDefinition( "roll" ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "ic", Prefixes: new[] { "ic", "say" } ) );
			// Same prefix as "ic" in another case: the composer matches ignoring case.
			builder.RegisterChatChannel( new ChatChannelDefinition( "looc", Prefixes: new[] { "looc", "SAY" } ) );
			// Would shadow the "roll" command for every player.
			builder.RegisterChatChannel( new ChatChannelDefinition( "dice", Prefixes: new[] { "Roll" } ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "broken", Prefixes: new[] { "", "two words" } ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "ooc", Prefixes: new[] { "ooc" } ) );
		} ) );

		Assert.IsFalse( report.IsValid );
		Assert.IsTrue( report.Issues.Any( issue =>
			issue.Code == ErrorCode.DuplicateRegistration && issue.Path == "chat_channels.looc.prefixes" ) );
		Assert.IsTrue( report.Issues.Any( issue =>
			issue.Code == ErrorCode.SchemaInvalid && issue.Path == "chat_channels.dice.prefixes" &&
			issue.Message.Contains( "roll" ) ) );
		Assert.AreEqual( 2, report.Issues.Count( issue => issue.Path == "chat_channels.broken.prefixes" ) );
		Assert.IsFalse( report.Issues.Any( issue => issue.Path.StartsWith( "chat_channels.ic." ) ) );
		Assert.IsFalse( report.Issues.Any( issue => issue.Path.StartsWith( "chat_channels.ooc." ) ) );
	}

	[TestMethod]
	public void DefinitionListsAreCopiedSoCallerMutationCannotReachTheRegistry()
	{
		var actions = new List<string> { "use" };
		var prefixes = new List<string> { "ic" };
		var compiled = SchemaCompiler.Compile( new DelegateSchema( "test_schema", builder =>
		{
			builder.RegisterAction( new ActionDefinition( "use" ) );
			builder.RegisterItem( new ItemDefinition( "tool", actions ) );
			builder.RegisterChatChannel( new ChatChannelDefinition( "ic", Prefixes: prefixes ) );
		} ) );
		Assert.IsTrue( compiled.Succeeded, compiled.Error?.Message );

		actions.Add( "missing_action" );
		prefixes.Clear();

		CollectionAssert.AreEqual( new[] { "use" }, compiled.Value.Items.Require( "tool" ).Value.ActionIds.ToArray() );
		CollectionAssert.AreEqual( new[] { "ic" }, compiled.Value.ChatChannels.Require( "ic" ).Value.Prefixes!.ToArray() );

		// The same holds for a list assigned through "with".
		var replacement = new List<string> { "use" };
		var rewritten = new ItemDefinition( "tool", Array.Empty<string>() ) with { ActionIds = replacement };
		replacement.Clear();
		CollectionAssert.AreEqual( new[] { "use" }, rewritten.ActionIds.ToArray() );
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
