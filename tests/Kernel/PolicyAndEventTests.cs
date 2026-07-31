using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Tests.Kernel;

[TestClass]
public sealed class PolicyAndEventTests
{
	[TestMethod]
	public void PolicyPipelineRequiresEveryHandlerToAllowInDeterministicOrder()
	{
		var calls = new List<string>();
		var pipeline = new PolicyPipeline<string>(
			new PolicyHandler<string>("built_in", new DelegatePolicy<string>(_ =>
			{
				calls.Add("built_in");
				return PolicyDecision.Allow();
			})),
			new[]
			{
				new PolicyHandler<string>("last", new DelegatePolicy<string>(_ =>
				{
					calls.Add("last");
					return PolicyDecision.Allow();
				}), 20),
				new PolicyHandler<string>("deny", new DelegatePolicy<string>(_ =>
				{
					calls.Add("deny");
					return PolicyDecision.Deny("No access.", ErrorCode.Unauthorized);
				}), 10)
			});

		var result = pipeline.Evaluate("context");

		Assert.IsTrue(result.Failed);
		Assert.AreEqual(ErrorCode.Unauthorized, result.Error!.Code);
		CollectionAssert.AreEqual(new[] { "built_in", "deny" }, calls);
	}

	[TestMethod]
	public void PolicyExceptionFailsClosedAndProducesDiagnostic()
	{
		PolicyDiagnostic? diagnostic = null;
		var pipeline = new PolicyPipeline<string>(
			new PolicyHandler<string>("built_in",
				new DelegatePolicy<string>(_ => throw new InvalidOperationException("boom"))),
			diagnostics: value => diagnostic = value);

		var result = pipeline.Evaluate("context");

		Assert.IsTrue(result.Failed);
		Assert.AreEqual(ErrorCode.PolicyFailed, result.Error!.Code);
		Assert.AreEqual("built_in", diagnostic!.HandlerId);
	}

	[TestMethod]
	public void SchemaRejectsCustomPolicyWithoutExactlyOneBuiltInPolicy()
	{
		var schema = new DelegateSchema(builder =>
			builder.RegisterPolicy("custom", new DelegatePolicy<string>(_ => PolicyDecision.Allow())));

		var report = SchemaCompiler.Validate(schema);

		Assert.IsTrue(report.Issues.Any(x => x.Message.Contains("Exactly one built-in", StringComparison.Ordinal)));
	}

	[TestMethod]
	public void PostCommitEventBusIsolatesFailureAndInvokesLaterHandlers()
	{
		var calls = new List<string>();
		var bus = new PostCommitEventBus<string>(new[]
		{
			new EventHandlerRegistration<string>("broken", new DelegateEventHandler<string>(_ =>
			{
				calls.Add("broken");
				throw new InvalidOperationException("boom");
			}), 0),
			new EventHandlerRegistration<string>("healthy", new DelegateEventHandler<string>(_ =>
				calls.Add("healthy")), 1)
		});

		var report = bus.Publish("committed");

		Assert.AreEqual(2, report.InvokedCount);
		Assert.HasCount(1, report.Failures);
		CollectionAssert.AreEqual(new[] { "broken", "healthy" }, calls);
	}

	[TestMethod]
	public void CompiledSchemaBuildsRegisteredPolicyAndEventPipelines()
	{
		var observed = string.Empty;
		var schema = new DelegateSchema(builder =>
		{
			builder.RegisterBuiltInPolicy("core", new DelegatePolicy<string>(_ => PolicyDecision.Allow()));
			builder.RegisterEventHandler("observer", new DelegateEventHandler<string>(value => observed = value));
		});
		var compiled = SchemaCompiler.Compile(schema).Value;

		var policy = compiled.CreatePolicyPipeline<string>();
		var eventReport = compiled.CreateEventBus<string>().Publish("done");

		Assert.IsTrue(policy.Succeeded);
		Assert.IsTrue(policy.Value.Evaluate("context").Succeeded);
		Assert.IsTrue(eventReport.Succeeded);
		Assert.AreEqual("done", observed);
	}

	[TestMethod]
	public void CompiledSchemaComposesRuntimePoliciesAfterMandatoryBuiltInInDeterministicOrder()
	{
		var calls = new List<string>();
		var schema = new DelegateSchema(builder =>
		{
			builder.RegisterBuiltInPolicy("core", new DelegatePolicy<string>(_ =>
			{
				calls.Add("core");
				return PolicyDecision.Allow();
			}), order: 1_000);
			builder.RegisterPolicy("schema.guard", new DelegatePolicy<string>(_ =>
			{
				calls.Add("schema.guard");
				return PolicyDecision.Allow();
			}), order: 10);
		});
		var compiled = SchemaCompiler.Compile(schema).Value;

		var pipeline = compiled.CreatePolicyPipeline<string>(new[]
		{
			new PolicyHandler<string>("runtime.later", new DelegatePolicy<string>(_ =>
			{
				calls.Add("runtime.later");
				return PolicyDecision.Allow();
			}), 30),
			new PolicyHandler<string>("runtime.authorization", new DelegatePolicy<string>(_ =>
			{
				calls.Add("runtime.authorization");
				return PolicyDecision.Deny("Runtime authorization denied.", ErrorCode.Unauthorized);
			}), 20)
		});

		Assert.IsTrue(pipeline.Succeeded, pipeline.Error?.Message);
		var result = pipeline.Value.Evaluate("context");

		Assert.IsTrue(result.Failed);
		Assert.AreEqual(ErrorCode.Unauthorized, result.Error!.Code);
		CollectionAssert.AreEqual(
			new[] { "core", "schema.guard", "runtime.authorization" },
			calls);
	}

	[TestMethod]
	public void CompiledSchemaRejectsInvalidOrDuplicateRuntimePolicyIdentifiers()
	{
		var schema = new DelegateSchema(builder =>
		{
			builder.RegisterBuiltInPolicy("core", new DelegatePolicy<string>(_ => PolicyDecision.Allow()));
			builder.RegisterPolicy("schema.guard", new DelegatePolicy<string>(_ => PolicyDecision.Allow()));
		});
		var compiled = SchemaCompiler.Compile(schema).Value;
		var allow = new DelegatePolicy<string>(_ => PolicyDecision.Allow());

		var invalid = compiled.CreatePolicyPipeline<string>(new[]
		{
			new PolicyHandler<string>("Runtime Bad", allow)
		});
		var schemaCollision = compiled.CreatePolicyPipeline<string>(new[]
		{
			new PolicyHandler<string>("schema.guard", allow)
		});
		var runtimeCollision = compiled.CreatePolicyPipeline<string>(new[]
		{
			new PolicyHandler<string>("runtime.guard", allow),
			new PolicyHandler<string>("runtime.guard", allow)
		});

		Assert.AreEqual(ErrorCode.InvalidIdentifier, invalid.Error!.Code);
		Assert.AreEqual(ErrorCode.DuplicateRegistration, schemaCollision.Error!.Code);
		Assert.AreEqual(ErrorCode.DuplicateRegistration, runtimeCollision.Error!.Code);
	}

	private sealed class DelegatePolicy<T> : IPolicy<T>
	{
		private readonly Func<T, PolicyDecision> _evaluate;
		public DelegatePolicy(Func<T, PolicyDecision> evaluate) => _evaluate = evaluate;
		public PolicyDecision Evaluate(T context) => _evaluate(context);
	}

	private sealed class DelegateEventHandler<T> : IEventHandler<T>
	{
		private readonly Action<T> _handle;
		public DelegateEventHandler(Action<T> handle) => _handle = handle;
		public void Handle(T @event) => _handle(@event);
	}

	private sealed class DelegateSchema : IHexSchema
	{
		private readonly Action<SchemaBuilder> _configure;
		public DelegateSchema(Action<SchemaBuilder> configure) => _configure = configure;
		public string Id => "test_schema";
		public void Configure(SchemaBuilder builder) => _configure(builder);
	}
}
