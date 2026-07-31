#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Events;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

public enum CharacterMutationKind
{
	TouchLastPlayed,
	ReplaceSchemaState,
	SetBan,
	Credit,
	Debit
}

public sealed record CharacterMutationContext(
	AccountId ActorAccountId,
	CharacterRecord Character,
	CharacterMutationKind Kind );

public sealed record CharacterChangedEvent(
	CharacterRecord Before,
	CharacterRecord After,
	CharacterMutationKind Kind,
	long CommitSequence );

/// <summary>
/// Provider-issued proof of one durable character aggregate mutation. Callers
/// use the commit receipt directly when updating receipt-driven projections.
/// </summary>
public sealed record CharacterMutationReceipt(
	CharacterRecord Before,
	CharacterRecord After,
	CharacterMutationKind Kind,
	CommitReceipt Commit );

/// <summary>
/// Validated character mutation that can be staged into a caller-owned unit of
/// work. Only the creating service can stage and complete the plan, and event
/// publication remains impossible until a matching provider receipt is supplied.
/// </summary>
public sealed class PreparedCharacterMutation
{
	private int _state;

	internal PreparedCharacterMutation(
		AggregateMutationService owner,
		DocumentSnapshot<CharacterRecord> document,
		CharacterRecord after,
		CharacterMutationKind kind )
	{
		Owner = owner;
		Document = document;
		Before = document.Value;
		After = after;
		Kind = kind;
	}

	public CharacterRecord Before { get; }
	public CharacterRecord After { get; }
	public CharacterMutationKind Kind { get; }
	internal AggregateMutationService Owner { get; }
	internal DocumentSnapshot<CharacterRecord> Document { get; }
	internal bool IsStaged => Interlocked.CompareExchange( ref _state, 0, 0 ) == 1;
	internal bool IsCompleted => Interlocked.CompareExchange( ref _state, 0, 0 ) == 2;
	internal bool TryBeginStage() => Interlocked.CompareExchange( ref _state, 1, 0 ) == 0;
	internal void CancelStage() => Interlocked.CompareExchange( ref _state, 0, 1 );
	internal bool TryComplete() => Interlocked.CompareExchange( ref _state, 2, 1 ) == 1;
}

public sealed record ItemTraitMutationContext(
	AccountId ActorAccountId,
	CharacterId ActorCharacterId,
	ItemRecord Item,
	string TraitId );

public sealed record ItemTraitChangedEvent(
	ItemRecord Before,
	ItemRecord After,
	string TraitId,
	long CommitSequence );

/// <summary>
/// The only framework mutation surface for character metadata and item traits.
/// Every edit is isolated and revision-checked by the provider.
/// </summary>
public sealed class AggregateMutationService
{
	private readonly DomainRepositories _repositories;
	private readonly CompiledSchema _schema;
	private readonly PolicyPipeline<CharacterMutationContext> _characterPolicy;
	private readonly PolicyPipeline<ItemTraitMutationContext> _traitPolicy;
	private readonly PostCommitEventBus<CharacterChangedEvent> _characterEvents;
	private readonly PostCommitEventBus<ItemTraitChangedEvent> _traitEvents;

	public AggregateMutationService(
		DomainRepositories repositories,
		CompiledSchema schema,
		PolicyPipeline<CharacterMutationContext> characterPolicy,
		PolicyPipeline<ItemTraitMutationContext> traitPolicy,
		PostCommitEventBus<CharacterChangedEvent>? characterEvents = null,
		PostCommitEventBus<ItemTraitChangedEvent>? traitEvents = null )
	{
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );
		_schema = schema ?? throw new ArgumentNullException( nameof(schema) );
		_characterPolicy = characterPolicy ?? throw new ArgumentNullException( nameof(characterPolicy) );
		_traitPolicy = traitPolicy ?? throw new ArgumentNullException( nameof(traitPolicy) );
		_characterEvents = characterEvents ?? new PostCommitEventBus<CharacterChangedEvent>();
		_traitEvents = traitEvents ?? new PostCommitEventBus<ItemTraitChangedEvent>();
	}

	public async ValueTask<OperationResult<CharacterMutationReceipt>> TouchLastPlayedAsync(
		AccountId actor,
		CharacterId characterId,
		DateTimeOffset timestampUtc,
		CancellationToken cancellationToken = default )
	{
		var prepared = PrepareTouchLastPlayed( actor, characterId, timestampUtc );
		if ( prepared.Failed ) return Failure<CharacterMutationReceipt>( prepared.Error! );

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var staged = StageTouchLastPlayed( unitOfWork, prepared.Value );
		if ( staged.Failed )
		{
			await unitOfWork.DisposeAsync();
			return Failure<CharacterMutationReceipt>( staged.Error! );
		}
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded )
			return PersistenceResultMapping.Failure<CharacterMutationReceipt>( committed.Error! );
		return CompleteTouchLastPlayed( prepared.Value, committed.Value! );
	}

	public OperationResult<PreparedCharacterMutation> PrepareTouchLastPlayed(
		AccountId actor,
		CharacterId characterId,
		DateTimeOffset timestampUtc )
	{
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		if ( document is null )
			return OperationResult<PreparedCharacterMutation>.Failure(
				ErrorCode.NotFound, "Character was not found." );
		var before = document.Value;
		var policy = _characterPolicy.Evaluate( new CharacterMutationContext(
			actor, before, CharacterMutationKind.TouchLastPlayed ) );
		if ( policy.Failed ) return Failure<PreparedCharacterMutation>( policy.Error! );
		if ( timestampUtc.Offset != TimeSpan.Zero || timestampUtc < before.LastPlayedAt )
			return OperationResult<PreparedCharacterMutation>.Failure(
				ErrorCode.InvalidArgument, "Last-played timestamp must be monotonic UTC." );
		return OperationResult<PreparedCharacterMutation>.Success(
			new PreparedCharacterMutation(
				this,
				document,
				before with { LastPlayedAt = timestampUtc },
				CharacterMutationKind.TouchLastPlayed ) );
	}

	public OperationResult StageTouchLastPlayed(
		IUnitOfWork unitOfWork,
		PreparedCharacterMutation prepared )
	{
		ArgumentNullException.ThrowIfNull( unitOfWork );
		ArgumentNullException.ThrowIfNull( prepared );
		if ( !ReferenceEquals( prepared.Owner, this ) )
			return OperationResult.Failure(
				ErrorCode.InvalidArgument, "Prepared character mutation belongs to another service." );
		if ( prepared.Kind != CharacterMutationKind.TouchLastPlayed ||
			!prepared.TryBeginStage() )
			return OperationResult.Failure(
				ErrorCode.Conflict, "Prepared last-played mutation is no longer stageable." );

		var editor = unitOfWork.Edit( _repositories.Characters, prepared.Document );
		if ( editor is null )
		{
			prepared.CancelStage();
			return OperationResult.Failure( ErrorCode.Conflict, "Character changed." );
		}
		editor.Replace( prepared.After );
		unitOfWork.Save( editor );
		return OperationResult.Success();
	}

	public OperationResult<CharacterMutationReceipt> CompleteTouchLastPlayed(
		PreparedCharacterMutation prepared,
		CommitReceipt commit )
	{
		ArgumentNullException.ThrowIfNull( prepared );
		ArgumentNullException.ThrowIfNull( commit );
		if ( !ReferenceEquals( prepared.Owner, this ) )
			return OperationResult<CharacterMutationReceipt>.Failure(
				ErrorCode.InvalidArgument, "Prepared character mutation belongs to another service." );
		if ( !prepared.IsStaged || prepared.IsCompleted )
			return OperationResult<CharacterMutationReceipt>.Failure(
				ErrorCode.Conflict, "Prepared last-played mutation is not awaiting completion." );

		var address = new DocumentAddress(
			DomainCollections.Characters, DomainKeys.Character( prepared.Before.Id ) );
		var expectedRevision = prepared.Document.Revision.Next();
		var committedDocuments = commit.Documents.Where( document =>
			document.Address == address && !document.IsDeleted ).Take( 2 ).ToArray();
		var committedDocument = committedDocuments.Length == 1 ? committedDocuments[0] : null;
		// The provider receipt alone proves the prepared revision committed durably. A live
		// repository re-read here would misreport success as failure whenever a later
		// legitimate commit touched the same character between durability and completion.
		if ( commit.Sequence <= 0 || committedDocument is null ||
			committedDocument.Revision != expectedRevision )
			return OperationResult<CharacterMutationReceipt>.Failure(
				ErrorCode.InvariantViolation,
				"Commit receipt does not contain the prepared character revision." );

		if ( !prepared.TryComplete() )
			return OperationResult<CharacterMutationReceipt>.Failure(
				ErrorCode.Conflict, "Prepared last-played mutation was already completed." );
		_characterEvents.Publish( new CharacterChangedEvent(
			prepared.Before, prepared.After, prepared.Kind, commit.Sequence ) );
		return OperationResult<CharacterMutationReceipt>.Success(
			new CharacterMutationReceipt(
				prepared.Before, prepared.After, prepared.Kind, commit ) );
	}

	public ValueTask<OperationResult<CharacterRecord>> ReplaceSchemaStateAsync(
		AccountId actor,
		CharacterId characterId,
		TypedPayload state,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( state );
		return MutateCharacterAsync(
			actor,
			characterId,
			CharacterMutationKind.ReplaceSchemaState,
			character => ValidatePayload( state ).Succeeded
				? OperationResult<CharacterRecord>.Success( character with { SchemaState = state.DeepCopy() } )
				: OperationResult<CharacterRecord>.Failure( ErrorCode.PersistedTypeInvalid, "Schema state type is not registered or compatible." ),
			cancellationToken );
	}

	public ValueTask<OperationResult<CharacterRecord>> SetBanAsync(
		AccountId actor,
		CharacterId characterId,
		bool banned,
		DateTimeOffset? expiresAtUtc,
		CancellationToken cancellationToken = default ) =>
		MutateCharacterAsync(
			actor,
			characterId,
			CharacterMutationKind.SetBan,
			character => expiresAtUtc is not null && expiresAtUtc.Value.Offset != TimeSpan.Zero
				? OperationResult<CharacterRecord>.Failure( ErrorCode.InvalidArgument, "Ban expiry must be UTC." )
				: OperationResult<CharacterRecord>.Success( character with
				{
					IsBanned = banned,
					BanExpiresAt = banned ? expiresAtUtc : null
				} ),
			cancellationToken );

	public ValueTask<OperationResult<CharacterRecord>> CreditAsync(
		AccountId actor,
		CharacterId characterId,
		long amount,
		CancellationToken cancellationToken = default ) =>
		MutateCharacterAsync(
			actor, characterId, CharacterMutationKind.Credit,
			character => CurrencyService.Credit( character, amount ), cancellationToken );

	public ValueTask<OperationResult<CharacterRecord>> DebitAsync(
		AccountId actor,
		CharacterId characterId,
		long amount,
		CancellationToken cancellationToken = default ) =>
		MutateCharacterAsync(
			actor, characterId, CharacterMutationKind.Debit,
			character => CurrencyService.Debit( character, amount ), cancellationToken );

	public async ValueTask<OperationResult<ItemRecord>> ReplaceTraitAsync(
		AccountId actor,
		CharacterId actorCharacter,
		ItemId itemId,
		string traitId,
		TypedPayload payload,
		CancellationToken cancellationToken = default )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( traitId );
		ArgumentNullException.ThrowIfNull( payload );
		var payloadValidation = ValidatePayload( payload );
		if ( payloadValidation.Failed )
			return OperationResult<ItemRecord>.Failure( payloadValidation.Error!.Code, payloadValidation.Error.Message );

		var document = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( document is null ) return OperationResult<ItemRecord>.Failure( ErrorCode.NotFound, "Item was not found." );
		var before = document.Value;
		var policy = _traitPolicy.Evaluate( new ItemTraitMutationContext( actor, actorCharacter, before, traitId ) );
		if ( policy.Failed ) return OperationResult<ItemRecord>.Failure( policy.Error!.Code, policy.Error.Message );

		var traits = new Dictionary<string, TypedPayload>( before.Traits, StringComparer.Ordinal )
		{
			[traitId] = payload.DeepCopy()
		};
		var after = before with { Traits = traits };
		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.Items, document );
		if ( editor is null )
		{
			await unitOfWork.DisposeAsync();
			return OperationResult<ItemRecord>.Failure( ErrorCode.Conflict, "Item changed." );
		}
		editor.Replace( after );
		unitOfWork.Save( editor );
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded ) return PersistenceResultMapping.Failure<ItemRecord>( committed.Error! );
		_traitEvents.Publish( new ItemTraitChangedEvent( before, after, traitId, committed.Value!.Sequence ) );
		return OperationResult<ItemRecord>.Success( after );
	}

	private async ValueTask<OperationResult<CharacterRecord>> MutateCharacterAsync(
		AccountId actor,
		CharacterId characterId,
		CharacterMutationKind kind,
		Func<CharacterRecord, OperationResult<CharacterRecord>> mutation,
		CancellationToken cancellationToken )
	{
		var committed = await MutateCharacterCommittedAsync(
			actor, characterId, kind, mutation, cancellationToken );
		return committed.Succeeded
			? OperationResult<CharacterRecord>.Success( committed.Value.After )
			: OperationResult<CharacterRecord>.Failure(
				committed.Error!.Code, committed.Error.Message, committed.Error.Details );
	}

	private async ValueTask<OperationResult<CharacterMutationReceipt>> MutateCharacterCommittedAsync(
		AccountId actor,
		CharacterId characterId,
		CharacterMutationKind kind,
		Func<CharacterRecord, OperationResult<CharacterRecord>> mutation,
		CancellationToken cancellationToken )
	{
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		if ( document is null ) return OperationResult<CharacterMutationReceipt>.Failure( ErrorCode.NotFound, "Character was not found." );
		var before = document.Value;
		var policy = _characterPolicy.Evaluate( new CharacterMutationContext( actor, before, kind ) );
		if ( policy.Failed ) return OperationResult<CharacterMutationReceipt>.Failure( policy.Error!.Code, policy.Error.Message );
		var mutated = mutation( before );
		if ( mutated.Failed ) return OperationResult<CharacterMutationReceipt>.Failure(
			mutated.Error!.Code, mutated.Error.Message, mutated.Error.Details );

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.Characters, document );
		if ( editor is null )
		{
			await unitOfWork.DisposeAsync();
			return OperationResult<CharacterMutationReceipt>.Failure( ErrorCode.Conflict, "Character changed." );
		}
		editor.Replace( mutated.Value );
		unitOfWork.Save( editor );
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded ) return PersistenceResultMapping.Failure<CharacterMutationReceipt>( committed.Error! );
		_characterEvents.Publish( new CharacterChangedEvent( before, mutated.Value, kind, committed.Value!.Sequence ) );
		return OperationResult<CharacterMutationReceipt>.Success(
			new CharacterMutationReceipt( before, mutated.Value, kind, committed.Value ) );
	}

	private OperationResult ValidatePayload( TypedPayload payload )
	{
		if ( !_schema.PersistedTypes.TryGet( payload.TypeId.Value, out var registration ) )
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Persisted type is not registered." );
		if ( payload.TypeVersion <= 0 || payload.TypeVersion > registration!.Version )
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Persisted type version is incompatible." );
		return OperationResult.Success();
	}

	private static OperationResult<T> Failure<T>( OperationError error ) =>
		OperationResult<T>.Failure( error.Code, error.Message, error.Details );
}
