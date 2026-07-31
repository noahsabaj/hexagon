#nullable enable

namespace Hexagon.V2.Domain;

/// <summary>
/// Canonical owner-inventory role names used in <c>OwnerInventoryRecord</c> keys. Commit-time
/// invariants guarantee exactly one canonical owner record per inventory, keyed by
/// <c>DomainKeys.OwnerInventory(owner, role)</c>, so a keyed Find on (owner, role) answers
/// ownership questions without scanning the inventory store.
/// </summary>
public static class InventoryRoles
{
	public const string Main = "main";
	public const string Bag = "bag";
	public const string Storage = "storage";
}
