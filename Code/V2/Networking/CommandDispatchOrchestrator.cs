#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

/// <summary>
/// Completion disposition of one dispatched client command, resolved by the host
/// against the actor's current connection/character lease.
/// </summary>
public enum CommandCompletionStatus
{
	Current = 0,
	Stale = 1,
	Disconnected = 2
}

/// <summary>
/// Admission costs charged per framework client command before scope resolution. The RPC
/// entry points must reference these constants so the table stays pinned by neutral tests.
/// </summary>
public static class ClientCommandCosts
{
	public const int CharacterList = 1;
	public const int CreateCharacter = 4;
	public const int LoadCharacter = 4;
	public const int DeleteCharacter = 4;
	public const int UnloadCharacter = 1;
	public const int MoveItem = 4;
	public const int ItemAction = 4;
	public const int DropItem = 4;
	public const int PickupItem = 4;
	public const int Chat = 2;
	public const int CancelAction = 1;
	public const int BeginInteraction = 2;
	public const int ContinueInteraction = 1;
	public const int CloseInteraction = 1;
	public const int SchemaCommandFallback = 8;
}

/// <summary>
/// Host hooks consumed by <see cref="CommandDispatchOrchestrator"/>. The engine adapter
/// constructs one instance per RPC invocation carrying the derived caller identity; every
/// decision about ordering and fail-closed results stays in the orchestrator.
/// </summary>
public interface ICommandDispatchHost<TActor>
{
	/// <summary>The engine-derived caller exists and carries a non-zero platform identity.</summary>
	bool CallerIsAuthenticated { get; }

	/// <summary>The host application is present and able to execute commands.</summary>
	bool IsExecutionAvailable { get; }

	CommandAdmissionResult TryBeginCommand( CommandRequestId requestId, int cost );
	OperationResult<TActor> ResolveActor( ClientSessionScope scope, bool requireStableCharacter );
	void FinishRejectedCommand( CommandRequestId requestId );
	bool IsCommandLeaseCurrent( TActor actor );
	bool TryStartHostOperation( string name, Func<Task> operation );
	CancellationToken CommandCancellation( TActor actor );
	ValueTask<OperationResult> ExecuteAsync( TActor actor, ClientCommand command, CancellationToken cancellationToken );
	CommandCompletionStatus CompleteCommand( TActor actor, CommandRequestId requestId );

	/// <summary>Sends a result before an actor could be resolved; no-op when the caller is gone.</summary>
	void SendResultToCaller( ClientSessionScope scope, CommandRequestId requestId, OperationResult result );

	/// <summary>Sends a result to the resolved actor's own connection and session scope.</summary>
	void SendResult( TActor actor, CommandRequestId requestId, OperationResult result );

	void OnMalformedPayload( Exception exception );
	void OnExecutionFault( CommandRequestId requestId, Exception exception );
	void OnCompletionFault( CommandRequestId requestId, Exception exception );
}

/// <summary>
/// Engine-neutral client-command dispatch pipeline: charge before resolve, payload
/// validation before execution, stable-character re-resolution, tracked host operation,
/// lease re-check inside the operation, and completion that converts every failure into a
/// transported result. The engine adapter contributes only caller derivation, transport
/// sends, and logging, so this ordering is compiled and pinned by the neutral test suite.
/// </summary>
public static class CommandDispatchOrchestrator
{
	public static void Dispatch<TActor>(
		ICommandDispatchHost<TActor> host,
		ClientCommandHeader header,
		int cost,
		Func<ClientCommand> commandFactory )
	{
		ArgumentNullException.ThrowIfNull( host );
		ArgumentNullException.ThrowIfNull( commandFactory );
		var requestId = header.RequestId;
		var ingress = CommandIngressAdmission.Evaluate(
			host.CallerIsAuthenticated,
			() => host.TryBeginCommand( requestId, cost ),
			() => host.ResolveActor( header.Scope, requireStableCharacter: false ),
			() => host.FinishRejectedCommand( requestId ) );
		if ( !ingress.Authenticated )
		{
			host.SendResultToCaller( header.Scope, requestId,
				OperationResult.Failure( ErrorCode.Unauthorized, "RPC caller is not authenticated." ) );
			return;
		}
		var admitted = ingress.Admission;
		if ( !admitted.Accepted )
		{
			var result = admitted.Failure switch
			{
				CommandAdmissionFailure.RateLimited => OperationResultWireContract.RateLimited(
					"The connection command budget is exhausted.", admitted.RetryAfter ),
				CommandAdmissionFailure.Duplicate => OperationResult.Failure(
					ErrorCode.Conflict, "The command request ID is a duplicate." ),
				_ => OperationResult.Failure(
					ErrorCode.Unauthorized, "The host connection session is unavailable." )
			};
			host.SendResultToCaller( header.Scope, requestId, result );
			return;
		}
		var resolved = ingress.Session!.Value;
		if ( resolved.Failed )
		{
			host.SendResultToCaller( header.Scope, requestId,
				OperationResult.Failure( resolved.Error!.Code, resolved.Error.Message ) );
			return;
		}
		var actor = resolved.Value;
		if ( requestId.Value == Guid.Empty )
		{
			Complete( host, actor, requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InvalidArgument, "The command request ID is empty." ) ) );
			return;
		}

		ClientCommand command;
		OperationResult payload;
		try
		{
			command = commandFactory();
			payload = ClientPayloadLimits.Validate( command );
		}
		catch ( Exception exception )
		{
			host.OnMalformedPayload( exception );
			Complete( host, actor, requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InvalidArgument, "The command payload is malformed." ) ) );
			return;
		}
		if ( payload.Failed )
		{
			Complete( host, actor, requestId,
				OperationOutcome<OperationResult>.Success( payload ) );
			return;
		}
		if ( RequiresStableCharacter( command ) )
		{
			var stableActor = host.ResolveActor( header.Scope, requireStableCharacter: true );
			if ( stableActor.Failed )
			{
				Complete( host, actor, requestId,
					OperationOutcome<OperationResult>.Success(
						OperationResult.Failure( stableActor.Error!.Code, stableActor.Error.Message ) ) );
				return;
			}
			actor = stableActor.Value;
		}
		if ( !host.TryStartHostOperation(
			$"command:{requestId.Value:D}",
			() => ExecuteAsync( host, actor, requestId, command ) ) )
		{
			Complete(
				host,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.Conflict, "The host is draining and no longer accepts commands." ) ) );
		}
	}

	public static bool RequiresStableCharacter( ClientCommand command ) => command is not (
		RequestCharacterListCommand or
		CreateCharacterCommand or
		LoadCharacterCommand or
		DeleteCharacterCommand or
		UnloadCharacterCommand );

	private static async Task ExecuteAsync<TActor>(
		ICommandDispatchHost<TActor> host,
		TActor actor,
		CommandRequestId requestId,
		ClientCommand command )
	{
		if ( !host.IsCommandLeaseCurrent( actor ) )
		{
			Complete(
				host,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure(
						ErrorCode.Unauthorized,
						"The connection or active character changed before command execution." ) ) );
			return;
		}

		if ( !host.IsExecutionAvailable )
		{
			Complete(
				host,
				actor,
				requestId,
				OperationOutcome<OperationResult>.Success(
					OperationResult.Failure( ErrorCode.InternalError, "Host application is not ready." ) ) );
			return;
		}

		var outcome = await AsyncOperation.Capture(
			() => host.ExecuteAsync( actor, command, host.CommandCancellation( actor ) ) );
		Complete( host, actor, requestId, outcome, executionStarted: true );
	}

	private static void Complete<TActor>(
		ICommandDispatchHost<TActor> host,
		TActor actor,
		CommandRequestId requestId,
		OperationOutcome<OperationResult> outcome,
		bool executionStarted = false )
	{
		try
		{
			var status = host.CompleteCommand( actor, requestId );
			if ( status == CommandCompletionStatus.Disconnected ) return;

			OperationResult result;
			var executionSucceeded = executionStarted && outcome.Succeeded && outcome.Value.Succeeded;
			if ( CommandCompletionPolicy.ShouldRejectAsStale(
				status == CommandCompletionStatus.Current,
				executionStarted,
				executionSucceeded ) )
			{
				result = OperationResult.Failure( ErrorCode.Unauthorized, "The connection or active character changed before the command completed." );
			}
			else if ( !outcome.Succeeded )
			{
				host.OnExecutionFault( requestId, outcome.Exception! );
				result = host.CommandCancellation( actor ).IsCancellationRequested
					? OperationResult.Failure( ErrorCode.Unauthorized, "The command session ended before completion." )
					: OperationResult.Failure( ErrorCode.InternalError, "The host command failed unexpectedly." );
			}
			else
			{
				result = outcome.Value;
			}

			host.SendResult( actor, requestId, result );
		}
		catch ( Exception exception )
		{
			host.OnCompletionFault( requestId, exception );
		}
	}
}
