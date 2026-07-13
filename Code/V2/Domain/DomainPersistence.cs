#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Domain;

public static class DomainCollections
{
	public const string Characters = "characters";
	public const string CharacterSlots = "character-slots";
	public const string CharacterLifecycleGuards = "character-lifecycle-guards";
	public const string Inventories = "inventories";
	public const string OwnerInventories = "owner-inventories";
	public const string Items = "items";
	public const string WorldItems = "world-items";
	public const string UniqueReservations = "unique-reservations";
	public const string CharacterReferences = "character-references";
	public const string SceneEntities = "scene-entities";
	public const string Configuration = "configuration";
}

/// <summary>
/// Registers every concrete framework aggregate. Schemas register their own
/// concrete state and trait types separately.
/// </summary>
public static class DomainPersistence
{
	public static PersistedTypeRegistry RegisterHexagonDomainTypes( this PersistedTypeRegistry registry )
	{
		ArgumentNullException.ThrowIfNull( registry );

		return registry
			.Register<PersistedConfigRecord>( new PersistedTypeKey( "hexagon.config" ), 1,
				static value => value.DeepCopy() )
			.Register<CharacterRecord>( new PersistedTypeKey( "hexagon.character" ), 1,
				static value => value with { SchemaState = value.SchemaState.DeepCopy() } )
			.Register<CharacterSlotRecord>( new PersistedTypeKey( "hexagon.character-slot" ), 1,
				PersistedValuePublication.Immutable )
			.Register<CharacterLifecycleGuardRecord>( new PersistedTypeKey( "hexagon.character-lifecycle-guard" ), 1,
				PersistedValuePublication.Immutable )
			.Register<InventoryRecord>( new PersistedTypeKey( "hexagon.inventory" ), 1,
				static value => value with
				{
					Placements = PersistedValuePublication.ReadOnlyList( value.Placements )
				} )
			.Register<OwnerInventoryRecord>( new PersistedTypeKey( "hexagon.owner-inventory" ), 1,
				PersistedValuePublication.Immutable )
			.Register<ItemRecord>( new PersistedTypeKey( "hexagon.item" ), 1,
				static value => value with
				{
					Traits = PersistedValuePublication.ReadOnlyDictionary(
						value.Traits.Select( pair => new KeyValuePair<string, TypedPayload>(
							pair.Key, pair.Value.DeepCopy() ) ),
						StringComparer.Ordinal )
				} )
			.Register<WorldItemRecord>( new PersistedTypeKey( "hexagon.world-item" ), 1,
				PersistedValuePublication.Immutable )
			.Register<UniqueReservationRecord>( new PersistedTypeKey( "hexagon.unique-reservation" ), 1,
				PersistedValuePublication.Immutable )
			.Register<CharacterReferenceRecord>( new PersistedTypeKey( "hexagon.character-reference" ), 1,
				static value => value with { State = value.State.DeepCopy() } )
			.Register<PersistentSceneEntityRecord>( new PersistedTypeKey( "hexagon.scene-entity" ), 1,
				static value => value with { State = value.State.DeepCopy() } );
	}
}
