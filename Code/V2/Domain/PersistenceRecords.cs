#nullable enable

namespace Hexagon.V2.Domain;

/// <summary>
/// Durable uniqueness guard for the owner/slot pair. Its document key is
/// <c>{account-id}:{slot}</c>, so concurrent creation attempts conflict at commit.
/// </summary>
public sealed record CharacterSlotRecord
{
	public required AccountId AccountId { get; init; }
	public required int Slot { get; init; }
	public required CharacterId CharacterId { get; init; }
}

/// <summary>
/// Mutation fence for every reference that names a character. The revision is advanced in the
/// same transaction as reference create/update/delete so character deletion cannot race a stale scan.
/// </summary>
public sealed record CharacterLifecycleGuardRecord
{
	public required CharacterId CharacterId { get; init; }
	public long ReferenceRevision { get; init; }
}

/// <summary>
/// Durable uniqueness guard for one framework-owned inventory role, such as a
/// character's main inventory.
/// </summary>
public sealed record OwnerInventoryRecord
{
	public required string Role { get; init; }
	public required InventoryOwner Owner { get; init; }
	public required InventoryId InventoryId { get; init; }
}

/// <summary>
/// Schema initializers reserve unique values here without receiving persistence
/// access. The framework writes every reservation in the character transaction.
/// </summary>
public sealed record UniqueReservationRecord
{
	public required string Namespace { get; init; }
	public required string Value { get; init; }
	public required CharacterId CharacterId { get; init; }
}

/// <summary>
/// Searchable cross-aggregate reference used by doors, recognition and schema
/// modules. Character deletion can therefore remove references without knowing
/// schema implementation types.
/// </summary>
public sealed record CharacterReferenceRecord
{
	public required string Category { get; init; }
	public required CharacterId CharacterId { get; init; }
	public CharacterId? RelatedCharacterId { get; init; }
	public SceneEntityId? SceneEntityId { get; init; }
	public required TypedPayload State { get; init; }
}

public sealed record PersistentSceneEntityRecord
{
	public required SceneEntityId Id { get; init; }
	public required string Kind { get; init; }
	public required TypedPayload State { get; init; }
	public long Revision { get; init; }
}
