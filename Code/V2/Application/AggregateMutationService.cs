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

	public ValueTask<OperationResult<CharacterRecord>> TouchLastPlayedAsync(
		AccountId actor,
		CharacterId characterId,
		DateTimeOffset timestampUtc,
		CancellationToken cancellationToken = default ) =>
		MutateCharacterAsync(
			actor,
			characterId,
			CharacterMutationKind.TouchLastPlayed,
			character => timestampUtc.Offset != TimeSpan.Zero || timestampUtc < character.LastPlayedAt
				? OperationResult<CharacterRecord>.Failure( ErrorCode.InvalidArgument, "Last-played timestamp must be monotonic UTC." )
				: OperationResult<CharacterRecord>.Success( character with { LastPlayedAt = timestampUtc } ),
			cancellationToken );

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
		var document = _repositories.Characters.Find( DomainKeys.Character( characterId ) );
		if ( document is null ) return OperationResult<CharacterRecord>.Failure( ErrorCode.NotFound, "Character was not found." );
		var before = document.Value;
		var policy = _characterPolicy.Evaluate( new CharacterMutationContext( actor, before, kind ) );
		if ( policy.Failed ) return OperationResult<CharacterRecord>.Failure( policy.Error!.Code, policy.Error.Message );
		var mutated = mutation( before );
		if ( mutated.Failed ) return mutated;

		var unitOfWork = _repositories.Provider.BeginUnitOfWork();
		var editor = unitOfWork.Edit( _repositories.Characters, document );
		if ( editor is null )
		{
			await unitOfWork.DisposeAsync();
			return OperationResult<CharacterRecord>.Failure( ErrorCode.Conflict, "Character changed." );
		}
		editor.Replace( mutated.Value );
		unitOfWork.Save( editor );
		var committed = await unitOfWork.CommitAsync( cancellationToken );
		await unitOfWork.DisposeAsync();
		if ( !committed.Succeeded ) return PersistenceResultMapping.Failure<CharacterRecord>( committed.Error! );
		_characterEvents.Publish( new CharacterChangedEvent( before, mutated.Value, kind, committed.Value!.Sequence ) );
		return mutated;
	}

	private OperationResult ValidatePayload( TypedPayload payload )
	{
		if ( !_schema.PersistedTypes.TryGet( payload.TypeId.Value, out var registration ) )
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Persisted type is not registered." );
		if ( payload.TypeVersion <= 0 || payload.TypeVersion > registration!.Version )
			return OperationResult.Failure( ErrorCode.PersistedTypeInvalid, "Persisted type version is incompatible." );
		return OperationResult.Success();
	}
}
