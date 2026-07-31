#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class CommandDispatchOrchestratorTests
{
	[TestMethod]
	public void UnauthenticatedCallerIsRejectedBeforeAnyChargeOrResolution()
	{
		var host = new FakeDispatchHost { Authenticated = false };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.IsEmpty( host.Calls, "Unauthenticated traffic must never reach the per-connection bucket." );
		Assert.HasCount( 1, host.Sent );
		Assert.AreEqual( "caller", host.Sent[0].Target );
		Assert.AreEqual( ErrorCode.Unauthorized, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void ChargeHappensBeforeResolutionAndCarriesTheDeclaredCost()
	{
		var host = new FakeDispatchHost();

		CommandDispatchOrchestrator.Dispatch( host, Header(), 3, static () => new RequestCharacterListCommand() );

		Assert.AreEqual( "charge:3", host.Calls[0], "The caller is charged before its scope is inspected." );
		Assert.AreEqual( "resolve:initial", host.Calls[1] );
	}

	[TestMethod]
	public void RateLimitedAdmissionCarriesRetryAfterAndSkipsResolution()
	{
		var host = new FakeDispatchHost
		{
			Admission = CommandAdmissionResult.Reject(
				CommandAdmissionFailure.RateLimited, TimeSpan.FromSeconds( 2 ) )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 4, static () => new RequestCharacterListCommand() );

		CollectionAssert.AreEqual( new[] { "charge:4" }, host.Calls );
		Assert.HasCount( 1, host.Sent );
		Assert.AreEqual( ErrorCode.RateLimited, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void DuplicateRequestIdsConflict()
	{
		var host = new FakeDispatchHost
		{
			Admission = CommandAdmissionResult.Reject( CommandAdmissionFailure.Duplicate )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.AreEqual( ErrorCode.Conflict, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void ResolutionFailureIsForwardedAndTheAcceptedChargeIsClosed()
	{
		var host = new FakeDispatchHost
		{
			Resolution = OperationResult<string>.Failure( ErrorCode.Unauthorized, "scope mismatch" )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		CollectionAssert.AreEqual(
			new[] { "charge:1", "resolve:initial", "finish-rejected" },
			host.Calls );
		Assert.AreEqual( "scope mismatch", host.Sent[0].Result.Error!.Message );
	}

	[TestMethod]
	public void EmptyRequestIdCompletesWithInvalidArgument()
	{
		var host = new FakeDispatchHost();
		// Wire deserialization can materialize a default header; the constructor itself
		// rejects empty request ids, so the guard is only reachable through default structs.
		var header = default( ClientCommandHeader );

		CommandDispatchOrchestrator.Dispatch( host, header, 1, static () => new RequestCharacterListCommand() );

		CollectionAssert.Contains( host.Calls, "complete" );
		Assert.HasCount( 1, host.Sent );
		Assert.AreEqual( "actor", host.Sent[0].Target );
		Assert.AreEqual( ErrorCode.InvalidArgument, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void ThrowingCommandFactoryFailsClosedWithoutStartingAHostOperation()
	{
		var host = new FakeDispatchHost();

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1,
			static () => throw new FormatException( "bad wire payload" ) );

		Assert.HasCount( 1, host.MalformedPayloads );
		CollectionAssert.DoesNotContain( host.Calls, "host-operation" );
		Assert.AreEqual( ErrorCode.InvalidArgument, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void OversizedPayloadIsRejectedBeforeStableResolutionAndExecution()
	{
		var host = new FakeDispatchHost();

		CommandDispatchOrchestrator.Dispatch( host, Header(), 2,
			static () => new SendChatCommand( "ic", new string( 'a', 1_000_000 ) ) );

		CollectionAssert.DoesNotContain( host.Calls, "resolve:stable" );
		CollectionAssert.DoesNotContain( host.Calls, "host-operation" );
		Assert.HasCount( 1, host.Sent );
		Assert.IsFalse( host.Sent[0].Result.Succeeded );
	}

	[TestMethod]
	public void StableCharacterCommandsAreReResolvedAfterPayloadValidation()
	{
		var host = new FakeDispatchHost();

		CommandDispatchOrchestrator.Dispatch( host, Header(), 2,
			static () => new SendChatCommand( "ic", "hello" ) );

		CollectionAssert.AreEqual(
			new[]
			{
				"charge:2", "resolve:initial", "resolve:stable",
				"host-operation", "lease-check", "execute", "complete"
			},
			host.Calls );
		Assert.AreEqual( "stable-actor", host.Sent[0].Target,
			"Execution and completion must use the re-resolved stable actor." );
	}

	[TestMethod]
	public void StableResolutionFailureCompletesWithoutExecuting()
	{
		var host = new FakeDispatchHost
		{
			StableResolution = OperationResult<string>.Failure(
				ErrorCode.Unauthorized, "character changed" )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 2,
			static () => new SendChatCommand( "ic", "hello" ) );

		CollectionAssert.DoesNotContain( host.Calls, "execute" );
		Assert.AreEqual( "character changed", host.Sent[0].Result.Error!.Message );
	}

	[TestMethod]
	public void CharacterLifecycleCommandsSkipTheStableCharacterResolution()
	{
		foreach ( var factory in new Func<ClientCommand>[]
		{
			static () => new RequestCharacterListCommand(),
			static () => new LoadCharacterCommand( CharacterId.New() ),
			static () => new DeleteCharacterCommand( CharacterId.New() ),
			static () => new UnloadCharacterCommand()
		} )
		{
			var host = new FakeDispatchHost();
			CommandDispatchOrchestrator.Dispatch( host, Header(), 1, factory );
			CollectionAssert.DoesNotContain( host.Calls, "resolve:stable",
				$"{factory().GetType().Name} must dispatch without an active character." );
			Assert.AreEqual( "actor", host.Sent[0].Target );
		}
	}

	[TestMethod]
	public void HostOperationRefusalCompletesWithDrainingConflict()
	{
		var host = new FakeDispatchHost { AcceptHostOperation = false };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.AreEqual( ErrorCode.Conflict, host.Sent[0].Result.Error!.Code );
		StringAssert.Contains( host.StartedOperationName!, "command:" );
	}

	[TestMethod]
	public void StaleLeaseInsideTheHostOperationRejectsBeforeExecution()
	{
		var host = new FakeDispatchHost { LeaseCurrent = false };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		CollectionAssert.DoesNotContain( host.Calls, "execute" );
		Assert.AreEqual( ErrorCode.Unauthorized, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void MissingHostApplicationCompletesWithInternalError()
	{
		var host = new FakeDispatchHost { ExecutionAvailable = false };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		CollectionAssert.DoesNotContain( host.Calls, "execute" );
		Assert.AreEqual( ErrorCode.InternalError, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void ExecutionFaultBecomesInternalError()
	{
		var host = new FakeDispatchHost
		{
			Execution = static () => throw new InvalidOperationException( "handler exploded" )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.HasCount( 1, host.ExecutionFaults );
		Assert.AreEqual( ErrorCode.InternalError, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void ExecutionFaultUnderCancellationBecomesUnauthorized()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var host = new FakeDispatchHost
		{
			Cancellation = cancellation.Token,
			Execution = static () => throw new OperationCanceledException()
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.AreEqual( ErrorCode.Unauthorized, host.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void DisconnectedCompletionSendsNothing()
	{
		var host = new FakeDispatchHost { CompletionStatus = CommandCompletionStatus.Disconnected };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.IsEmpty( host.Sent );
	}

	[TestMethod]
	public void StaleCompletionKeepsASuccessfulExecutionResultButRejectsAFailedOne()
	{
		var success = new FakeDispatchHost { CompletionStatus = CommandCompletionStatus.Stale };
		CommandDispatchOrchestrator.Dispatch( success, Header(), 1, static () => new RequestCharacterListCommand() );
		Assert.IsTrue( success.Sent[0].Result.Succeeded,
			"A successful application result stays authoritative even under a stale lease." );

		var failure = new FakeDispatchHost
		{
			CompletionStatus = CommandCompletionStatus.Stale,
			Execution = static () => ValueTask.FromResult(
				OperationResult.Failure( ErrorCode.NotFound, "gone" ) )
		};
		CommandDispatchOrchestrator.Dispatch( failure, Header(), 1, static () => new RequestCharacterListCommand() );
		Assert.AreEqual( ErrorCode.Unauthorized, failure.Sent[0].Result.Error!.Code );
	}

	[TestMethod]
	public void SuccessfulExecutionForwardsTheApplicationResult()
	{
		var host = new FakeDispatchHost
		{
			Execution = static () => ValueTask.FromResult(
				OperationResult.Failure( ErrorCode.PolicyDenied, "application said no" ) )
		};

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.AreEqual( ErrorCode.PolicyDenied, host.Sent[0].Result.Error!.Code );
		Assert.AreEqual( "application said no", host.Sent[0].Result.Error!.Message );
	}

	[TestMethod]
	public void ThrowingCompletionHookIsObservedWithoutEscaping()
	{
		var host = new FakeDispatchHost { CompleteThrows = true };

		CommandDispatchOrchestrator.Dispatch( host, Header(), 1, static () => new RequestCharacterListCommand() );

		Assert.HasCount( 1, host.CompletionFaults );
		Assert.IsEmpty( host.Sent );
	}

	[TestMethod]
	public void CommandCostTableIsPinned()
	{
		var table = new Dictionary<string, int>( StringComparer.Ordinal )
		{
			[nameof( ClientCommandCosts.CharacterList )] = ClientCommandCosts.CharacterList,
			[nameof( ClientCommandCosts.CreateCharacter )] = ClientCommandCosts.CreateCharacter,
			[nameof( ClientCommandCosts.LoadCharacter )] = ClientCommandCosts.LoadCharacter,
			[nameof( ClientCommandCosts.DeleteCharacter )] = ClientCommandCosts.DeleteCharacter,
			[nameof( ClientCommandCosts.UnloadCharacter )] = ClientCommandCosts.UnloadCharacter,
			[nameof( ClientCommandCosts.MoveItem )] = ClientCommandCosts.MoveItem,
			[nameof( ClientCommandCosts.ItemAction )] = ClientCommandCosts.ItemAction,
			[nameof( ClientCommandCosts.DropItem )] = ClientCommandCosts.DropItem,
			[nameof( ClientCommandCosts.PickupItem )] = ClientCommandCosts.PickupItem,
			[nameof( ClientCommandCosts.Chat )] = ClientCommandCosts.Chat,
			[nameof( ClientCommandCosts.CancelAction )] = ClientCommandCosts.CancelAction,
			[nameof( ClientCommandCosts.BeginInteraction )] = ClientCommandCosts.BeginInteraction,
			[nameof( ClientCommandCosts.ContinueInteraction )] = ClientCommandCosts.ContinueInteraction,
			[nameof( ClientCommandCosts.CloseInteraction )] = ClientCommandCosts.CloseInteraction,
			[nameof( ClientCommandCosts.SchemaCommandFallback )] = ClientCommandCosts.SchemaCommandFallback
		};
		var expected = new Dictionary<string, int>( StringComparer.Ordinal )
		{
			["CharacterList"] = 1,
			["CreateCharacter"] = 4,
			["LoadCharacter"] = 4,
			["DeleteCharacter"] = 4,
			["UnloadCharacter"] = 1,
			["MoveItem"] = 4,
			["ItemAction"] = 4,
			["DropItem"] = 4,
			["PickupItem"] = 4,
			["Chat"] = 2,
			["CancelAction"] = 1,
			["BeginInteraction"] = 2,
			["ContinueInteraction"] = 1,
			["CloseInteraction"] = 1,
			["SchemaCommandFallback"] = 8
		};
		CollectionAssert.AreEquivalent( expected, table );
	}

	[TestMethod]
	public void OnlyCharacterLifecycleCommandsAreExemptFromTheStableCharacterRequirement()
	{
		Assert.IsFalse( CommandDispatchOrchestrator.RequiresStableCharacter( new RequestCharacterListCommand() ) );
		Assert.IsFalse( CommandDispatchOrchestrator.RequiresStableCharacter(
			new LoadCharacterCommand( CharacterId.New() ) ) );
		Assert.IsFalse( CommandDispatchOrchestrator.RequiresStableCharacter(
			new DeleteCharacterCommand( CharacterId.New() ) ) );
		Assert.IsFalse( CommandDispatchOrchestrator.RequiresStableCharacter( new UnloadCharacterCommand() ) );
		Assert.IsTrue( CommandDispatchOrchestrator.RequiresStableCharacter( new SendChatCommand( "ic", "hi" ) ) );
		Assert.IsTrue( CommandDispatchOrchestrator.RequiresStableCharacter(
			new CloseInteractionCommand( InteractionSessionId.New() ) ) );
	}

	private static ClientSessionScope Scope() =>
		new( ClientSessionNonce.New(), ConnectionEpoch.New() );

	private static ClientCommandHeader Header() =>
		new( Scope(), new CommandRequestId( Guid.NewGuid() ) );

	private sealed class FakeDispatchHost : ICommandDispatchHost<string>
	{
		public List<string> Calls { get; } = new();
		public bool Authenticated { get; set; } = true;
		public bool ExecutionAvailable { get; set; } = true;
		public CommandAdmissionResult Admission { get; set; } = CommandAdmissionResult.Success();
		public OperationResult<string> Resolution { get; set; } = OperationResult<string>.Success( "actor" );
		public OperationResult<string> StableResolution { get; set; } = OperationResult<string>.Success( "stable-actor" );
		public bool LeaseCurrent { get; set; } = true;
		public bool AcceptHostOperation { get; set; } = true;
		public bool CompleteThrows { get; set; }
		public Func<ValueTask<OperationResult>>? Execution { get; set; }
		public CancellationToken Cancellation { get; set; }
		public CommandCompletionStatus CompletionStatus { get; set; } = CommandCompletionStatus.Current;
		public List<(string Target, OperationResult Result)> Sent { get; } = new();
		public List<Exception> MalformedPayloads { get; } = new();
		public List<Exception> ExecutionFaults { get; } = new();
		public List<Exception> CompletionFaults { get; } = new();
		public string? StartedOperationName { get; private set; }

		public bool CallerIsAuthenticated => Authenticated;
		public bool IsExecutionAvailable => ExecutionAvailable;

		public CommandAdmissionResult TryBeginCommand( CommandRequestId requestId, int cost )
		{
			Calls.Add( $"charge:{cost}" );
			return Admission;
		}

		public OperationResult<string> ResolveActor( ClientSessionScope scope, bool requireStableCharacter )
		{
			Calls.Add( requireStableCharacter ? "resolve:stable" : "resolve:initial" );
			return requireStableCharacter ? StableResolution : Resolution;
		}

		public void FinishRejectedCommand( CommandRequestId requestId ) => Calls.Add( "finish-rejected" );

		public bool IsCommandLeaseCurrent( string actor )
		{
			Calls.Add( "lease-check" );
			return LeaseCurrent;
		}

		public bool TryStartHostOperation( string name, Func<Task> operation )
		{
			Calls.Add( "host-operation" );
			StartedOperationName = name;
			if ( !AcceptHostOperation ) return false;
			operation().GetAwaiter().GetResult();
			return true;
		}

		public CancellationToken CommandCancellation( string actor ) => Cancellation;

		public ValueTask<OperationResult> ExecuteAsync(
			string actor,
			ClientCommand command,
			CancellationToken cancellationToken )
		{
			Calls.Add( "execute" );
			return Execution?.Invoke() ?? ValueTask.FromResult( OperationResult.Success() );
		}

		public CommandCompletionStatus CompleteCommand( string actor, CommandRequestId requestId )
		{
			Calls.Add( "complete" );
			if ( CompleteThrows ) throw new InvalidOperationException( "completion registry exploded" );
			return CompletionStatus;
		}

		public void SendResultToCaller( ClientSessionScope scope, CommandRequestId requestId, OperationResult result ) =>
			Sent.Add( ("caller", result) );

		public void SendResult( string actor, CommandRequestId requestId, OperationResult result ) =>
			Sent.Add( (actor, result) );

		public void OnMalformedPayload( Exception exception ) => MalformedPayloads.Add( exception );
		public void OnExecutionFault( CommandRequestId requestId, Exception exception ) => ExecutionFaults.Add( exception );
		public void OnCompletionFault( CommandRequestId requestId, Exception exception ) => CompletionFaults.Add( exception );
	}
}
