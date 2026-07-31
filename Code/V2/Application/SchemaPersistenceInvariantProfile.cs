#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

/// <summary>
/// Exact nested-payload contract for one item definition. Trait names are part of
/// durable schema state, so undeclared, missing, or differently typed traits fail
/// startup instead of being interpreted by whichever feature happens to read them.
/// </summary>
public sealed record ItemPersistenceContract
{
	public ItemPersistenceContract(
		DefinitionId definition,
		IEnumerable<KeyValuePair<string, PersistedTypeId>> traits )
	{
		Definition = definition;
		ArgumentNullException.ThrowIfNull( traits );
		var copy = new Dictionary<string, PersistedTypeId>( StringComparer.Ordinal );
		foreach ( var trait in traits )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( trait.Key );
			if ( !copy.TryAdd( trait.Key, trait.Value ) )
				throw new ArgumentException( $"Item persistence trait '{trait.Key}' is duplicated.", nameof(traits) );
		}
		Traits = new ReadOnlyDictionary<string, PersistedTypeId>( copy );
	}

	public DefinitionId Definition { get; }
	public IReadOnlyDictionary<string, PersistedTypeId> Traits { get; }

	public static ItemPersistenceContract WithoutTraits( string definitionId ) =>
		new( new DefinitionId( definitionId ), Array.Empty<KeyValuePair<string, PersistedTypeId>>() );

	public static ItemPersistenceContract WithTrait(
		string definitionId,
		string trait,
		string persistedTypeId ) =>
		new(
			new DefinitionId( definitionId ),
			new[]
			{
				new KeyValuePair<string, PersistedTypeId>( trait, new PersistedTypeId( persistedTypeId ) )
			} );
}

public sealed record CharacterReferencePersistenceContract
{
	public CharacterReferencePersistenceContract(
		string category,
		PersistedTypeId stateType,
		bool allowMissingCharacter = false,
		bool allowMissingRelatedCharacter = false,
		bool allowMissingSceneEntity = false )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( category );
		Category = category;
		StateType = stateType;
		AllowMissingCharacter = allowMissingCharacter;
		AllowMissingRelatedCharacter = allowMissingRelatedCharacter;
		AllowMissingSceneEntity = allowMissingSceneEntity;
	}

	public string Category { get; }
	public PersistedTypeId StateType { get; }
	public bool AllowMissingCharacter { get; }
	public bool AllowMissingRelatedCharacter { get; }
	public bool AllowMissingSceneEntity { get; }
}

/// <summary>
/// Schema-owned declaration of every concrete type allowed inside framework outer
/// envelopes. The profile is copied on construction and is safe to share between
/// host scopes.
/// </summary>
public sealed class SchemaPersistenceInvariantProfile
{
	public SchemaPersistenceInvariantProfile(
		PersistedTypeId characterState,
		IEnumerable<ItemPersistenceContract> items,
		IEnumerable<KeyValuePair<string, PersistedTypeId>> characterReferences,
		IEnumerable<KeyValuePair<string, PersistedTypeId>> sceneEntities )
		: this(
			characterState,
			items,
			characterReferences.Select( pair => new CharacterReferencePersistenceContract( pair.Key, pair.Value ) ),
			sceneEntities )
	{
	}

	public SchemaPersistenceInvariantProfile(
		PersistedTypeId characterState,
		IEnumerable<ItemPersistenceContract> items,
		IEnumerable<CharacterReferencePersistenceContract> characterReferences,
		IEnumerable<KeyValuePair<string, PersistedTypeId>> sceneEntities )
	{
		CharacterState = characterState;
		Items = CopyItems( items );
		CharacterReferences = CopyReferenceContracts( characterReferences );
		SceneEntities = CopyContracts( sceneEntities, "scene-entity kind" );
	}

	public PersistedTypeId CharacterState { get; }
	public IReadOnlyDictionary<DefinitionId, ItemPersistenceContract> Items { get; }
	public IReadOnlyDictionary<string, CharacterReferencePersistenceContract> CharacterReferences { get; }
	public IReadOnlyDictionary<string, PersistedTypeId> SceneEntities { get; }

	private static IReadOnlyDictionary<DefinitionId, ItemPersistenceContract> CopyItems(
		IEnumerable<ItemPersistenceContract> items )
	{
		ArgumentNullException.ThrowIfNull( items );
		var copy = new Dictionary<DefinitionId, ItemPersistenceContract>();
		foreach ( var contract in items )
		{
			ArgumentNullException.ThrowIfNull( contract );
			if ( !copy.TryAdd( contract.Definition, contract ) )
				throw new ArgumentException(
					$"Item persistence contract '{contract.Definition}' is duplicated.", nameof(items) );
		}
		return new ReadOnlyDictionary<DefinitionId, ItemPersistenceContract>( copy );
	}

	private static IReadOnlyDictionary<string, PersistedTypeId> CopyContracts(
		IEnumerable<KeyValuePair<string, PersistedTypeId>> contracts,
		string subject )
	{
		ArgumentNullException.ThrowIfNull( contracts );
		var copy = new Dictionary<string, PersistedTypeId>( StringComparer.Ordinal );
		foreach ( var contract in contracts )
		{
			ArgumentException.ThrowIfNullOrWhiteSpace( contract.Key );
			if ( !copy.TryAdd( contract.Key, contract.Value ) )
				throw new ArgumentException( $"Persistence contract for {subject} '{contract.Key}' is duplicated.", nameof(contracts) );
		}
		return new ReadOnlyDictionary<string, PersistedTypeId>( copy );
	}

	private static IReadOnlyDictionary<string, CharacterReferencePersistenceContract> CopyReferenceContracts(
		IEnumerable<CharacterReferencePersistenceContract> contracts )
	{
		ArgumentNullException.ThrowIfNull( contracts );
		var copy = new Dictionary<string, CharacterReferencePersistenceContract>( StringComparer.Ordinal );
		foreach ( var contract in contracts )
		{
			ArgumentNullException.ThrowIfNull( contract );
			if ( !copy.TryAdd( contract.Category, contract ) )
				throw new ArgumentException(
					$"Persistence contract for character-reference category '{contract.Category}' is duplicated.",
					nameof(contracts) );
		}
		return new ReadOnlyDictionary<string, CharacterReferencePersistenceContract>( copy );
	}
}
