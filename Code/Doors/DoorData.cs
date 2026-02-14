namespace Hexagon.Doors;

/// <summary>
/// Serializable door state persisted to the database.
/// A door can be owned by a character OR a faction, but not both.
/// </summary>
public class DoorData
{
	/// <summary>
	/// Unique identifier for this door. Matches the DoorComponent's DoorId property.
	/// </summary>
	public string DoorId { get; set; }

	/// <summary>
	/// Character ID of the owner. Mutually exclusive with OwnerFactionId.
	/// </summary>
	public string OwnerCharacterId { get; set; }

	/// <summary>
	/// Faction ID of the owner. Mutually exclusive with OwnerCharacterId.
	/// </summary>
	public string OwnerFactionId { get; set; }

	/// <summary>
	/// Whether the door is currently locked.
	/// </summary>
	public bool IsLocked { get; set; }

	/// <summary>
	/// Additional character IDs that have access to this door.
	/// </summary>
	public List<string> AccessList { get; set; } = new();

	/// <summary>
	/// Current lock health. -1 = use config default; 0 = broken.
	/// </summary>
	public int LockHealth { get; set; } = -1;

	/// <summary>
	/// Maximum lock health. -1 = use config default.
	/// </summary>
	public int MaxLockHealth { get; set; } = -1;

	/// <summary>
	/// Whether this door has any owner.
	/// </summary>
	public bool HasOwner => !string.IsNullOrEmpty( OwnerCharacterId ) || !string.IsNullOrEmpty( OwnerFactionId );
}
