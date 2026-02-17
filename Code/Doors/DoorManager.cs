namespace Hexagon.Doors;

/// <summary>
/// Manages door registration and lookup.
/// DoorComponents register/unregister themselves on enable/disable.
/// Persistence is handled directly by each DoorComponent via DatabaseManager.
/// </summary>
public static class DoorManager
{
	private static readonly Dictionary<string, DoorComponent> _doors = new();

	/// <summary>
	/// Register a door component. Called by DoorComponent.OnEnabled.
	/// </summary>
	internal static void Register( DoorComponent door )
	{
		if ( string.IsNullOrEmpty( door.DoorId ) ) return;
		_doors[door.DoorId] = door;
	}

	/// <summary>
	/// Unregister a door component. Called by DoorComponent.OnDisabled.
	/// </summary>
	internal static void Unregister( DoorComponent door )
	{
		if ( string.IsNullOrEmpty( door.DoorId ) ) return;
		_doors.Remove( door.DoorId );
	}

	/// <summary>
	/// Get a door component by its ID.
	/// </summary>
	public static DoorComponent GetDoor( string doorId )
	{
		return _doors.GetValueOrDefault( doorId );
	}

	/// <summary>
	/// Get all registered doors.
	/// </summary>
	public static IReadOnlyDictionary<string, DoorComponent> GetAllDoors() => _doors;
}
