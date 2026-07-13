#nullable enable

using System;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

/// <summary>
/// One typed repository map shared by application services. Repository reads
/// always target the provider's committed logical view.
/// </summary>
public sealed class DomainRepositories
{
	public DomainRepositories( IPersistenceProvider provider )
	{
		Provider = provider ?? throw new ArgumentNullException( nameof(provider) );
		if ( !provider.IsInitialized )
			throw new InvalidOperationException( "Persistence must be initialized before repositories are composed." );

		Characters = provider.Repository<CharacterRecord>( DomainCollections.Characters );
		CharacterSlots = provider.Repository<CharacterSlotRecord>( DomainCollections.CharacterSlots );
		CharacterLifecycleGuards = provider.Repository<CharacterLifecycleGuardRecord>( DomainCollections.CharacterLifecycleGuards );
		Inventories = provider.Repository<InventoryRecord>( DomainCollections.Inventories );
		OwnerInventories = provider.Repository<OwnerInventoryRecord>( DomainCollections.OwnerInventories );
		Items = provider.Repository<ItemRecord>( DomainCollections.Items );
		WorldItems = provider.Repository<WorldItemRecord>( DomainCollections.WorldItems );
		UniqueReservations = provider.Repository<UniqueReservationRecord>( DomainCollections.UniqueReservations );
		CharacterReferences = provider.Repository<CharacterReferenceRecord>( DomainCollections.CharacterReferences );
		SceneEntities = provider.Repository<PersistentSceneEntityRecord>( DomainCollections.SceneEntities );
	}

	public IPersistenceProvider Provider { get; }
	public IPersistenceRepository<CharacterRecord> Characters { get; }
	public IPersistenceRepository<CharacterSlotRecord> CharacterSlots { get; }
	public IPersistenceRepository<CharacterLifecycleGuardRecord> CharacterLifecycleGuards { get; }
	public IPersistenceRepository<InventoryRecord> Inventories { get; }
	public IPersistenceRepository<OwnerInventoryRecord> OwnerInventories { get; }
	public IPersistenceRepository<ItemRecord> Items { get; }
	public IPersistenceRepository<WorldItemRecord> WorldItems { get; }
	public IPersistenceRepository<UniqueReservationRecord> UniqueReservations { get; }
	public IPersistenceRepository<CharacterReferenceRecord> CharacterReferences { get; }
	public IPersistenceRepository<PersistentSceneEntityRecord> SceneEntities { get; }
}

public static class DomainKeys
{
	public static string Character( CharacterId id ) => id.Value.ToString( "N" );
	public static string CharacterLifecycleGuard( CharacterId id ) => Character( id );
	public static string Inventory( InventoryId id ) => id.Value.ToString( "N" );
	public static string Item( ItemId id ) => id.Value.ToString( "N" );
	public static string SceneEntity( SceneEntityId id ) => id.Value.ToString( "N" );
	public static string CharacterSlot( AccountId accountId, int slot ) => $"{accountId.Value}:{slot}";
	public static string OwnerInventory( InventoryOwner owner, string role ) =>
		$"{(int)owner.Kind}:{owner.OwnerId:N}:{NormalizeKeyPart( role )}";
	public static string WorldItem( ItemId id ) => Item( id );
	public static string UniqueReservation( string reservationNamespace, string value ) =>
		$"{NormalizeKeyPart( reservationNamespace )}:{NormalizeKeyPart( value )}";

	private static string NormalizeKeyPart( string value )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( value );
		var normalized = value.Trim().ToLowerInvariant();
		if ( normalized.Any( character => char.IsControl( character ) || character is '/' or '\\' or ':' ) )
			throw new ArgumentException( "Key parts cannot contain path separators, colons or control characters.", nameof(value) );
		return normalized;
	}
}
