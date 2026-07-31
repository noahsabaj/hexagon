#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Application;

public sealed record SceneEntityStateMutationContext(
	AccountId ActorAccountId,
	CharacterId? ActorCharacterId,
	PersistentSceneEntityRecord Current,
	TypedPayload ProposedState );

public sealed record SceneEntityStateChangedEvent(
	PersistentSceneEntityRecord Before,
	PersistentSceneEntityRecord After,
	long CommitSequence );

/// <summary>
/// Framework-provided transactional mutation boundary for persistent scene-entity state.
/// This is deliberately public framework API with no in-repo runtime consumer: schema
/// packages that do not need bespoke post-commit behavior compose it directly, while
/// richer gamemodes (HL2RP's HL2RPSceneEntityBehaviorService, for example) implement
/// their own boundary with feature-specific events and policies instead of wrapping this
/// one. Unit tests pin its commit semantics.
/// </summary>
public sealed class SceneEntityStateService
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly PolicyPipeline<SceneEntityStateMutationContext> _policy;
	private readonly PostCommitEventBus<SceneEntityStateChangedEvent> _events;

	public SceneEntityStateService(
		DomainRepositories repositories,
		CompiledSchema schema,
		PolicyPipeline<SceneEntityStateMutationContext> policy,
		PostCommitEventBus<SceneEntityStateChangedEvent>? events = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_policy = policy ?? throw new ArgumentNullException( nameof(policy) );
		_events = events ?? new PostCommitEventBus<SceneEntityStateChangedEvent>();
	}

	public PersistentSceneEntityRecord? Find( SceneEntityId id ) =>
		_repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) )?.Value;

	public async ValueTask<OperationResult<PersistentSceneEntityRecord>> EnsureAsync(
		SceneEntityId id,
		string kind,
		TypedPayload initialState,
		CancellationToken cancellationToken = default )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( kind );
		ArgumentNullException.ThrowIfNull( initialState );
		var existing = Find( id );
		if ( existing is not null )
		{
			return string.Equals( existing.Kind, kind, StringComparison.Ordinal )
				? OperationResult<PersistentSceneEntityRecord>.Success( existing )
				: OperationResult<PersistentSceneEntityRecord>.Failure(
					ErrorCode.Conflict, "Scene entity ID is already bound to a different kind." );
		}
		var type = ValidatePayload( initialState );
		if ( type.Failed )
			return OperationResult<PersistentSceneEntityRecord>.Failure( type.Error!.Code, type.Error.Message );

		var record = new PersistentSceneEntityRecord
		{
			Id = id,
			Kind = kind,
			State = initialState.DeepCopy(),
			Revision = 0
		};
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		unitOfWork.Create( _repositories.SceneEntities, DomainKeys.SceneEntity( id ), record );
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		return committed.Succeeded
			? OperationResult<PersistentSceneEntityRecord>.Success( record )
			: PersistenceResultMapping.Failure<PersistentSceneEntityRecord>( committed.Error! );
	}

	public async ValueTask<OperationResult<PersistentSceneEntityRecord>> ReplaceStateAsync(
		AccountId actor,
		CharacterId? actorCharacter,
		SceneEntityId id,
		TypedPayload state,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( state );
		var type = ValidatePayload( state );
		if ( type.Failed )
			return OperationResult<PersistentSceneEntityRecord>.Failure( type.Error!.Code, type.Error.Message );
		var document = _repositories.SceneEntities.Find( DomainKeys.SceneEntity( id ) );
		if ( document is null )
			return OperationResult<PersistentSceneEntityRecord>.Failure( ErrorCode.NotFound, "Scene entity state was not found." );

		var before = document.Value;
		var policy = _policy.Evaluate( new SceneEntityStateMutationContext( actor, actorCharacter, before, state ) );
		if ( policy.Failed )
			return OperationResult<PersistentSceneEntityRecord>.Failure( policy.Error!.Code, policy.Error.Message );
		var after = before with { State = state.DeepCopy() };

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.SceneEntities, document );
		if ( editor is null )
		{
			await unitOfWork.DisposeAsync();
			return OperationResult<PersistentSceneEntityRecord>.Failure( ErrorCode.Conflict, "Scene entity state changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded )
			return PersistenceResultMapping.Failure<PersistentSceneEntityRecord>( committed.Error! );
		_events.Publish( new SceneEntityStateChangedEvent( before, after, committed.Value!.Sequence ) );
		return OperationResult<PersistentSceneEntityRecord>.Success( after );
	}

	private OperationResult ValidatePayload( TypedPayload payload )
	{
		if ( !_schema.PersistedTypes.TryGet( payload.TypeId.Value, out var registration ) ||
			payload.TypeVersion <= 0 || payload.TypeVersion > registration!.Version )
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Scene state type is not registered or compatible." );
		return OperationResult.Success();
	}
}
