namespace Hexagon.Doors;

/// <summary>
/// Manages door registration, persistence, and lookup.
/// DoorComponents register/unregister themselves on enable/disable.
/// </summary>
public static class DoorManager
{
	private static readonly Dictionary<string, DoorComponent> _doors = new();

	internal static void Initialize()
	{
		Log.Info( "Hexagon: DoorManager initialized." );
	}

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

	/// <summary>
	/// Save door data to the database.
	/// </summary>
	public static void SaveDoor( DoorData data )
	{
		Persistence.DatabaseManager.Save( "doors", data.DoorId, data );
	}

	/// <summary>
	/// Load door data from the database.
	/// </summary>
	public static DoorData LoadDoor( string doorId )
	{
		return Persistence.DatabaseManager.Load<DoorData>( "doors", doorId );
	}

	/// <summary>
	/// Delete door data from the database.
	/// </summary>
	public static void DeleteDoor( string doorId )
	{
		Persistence.DatabaseManager.Delete( "doors", doorId );
	}

	/// <summary>
	/// Save all registered doors to the database.
	/// </summary>
	public static void SaveAll()
	{
		var saved = 0;

		foreach ( var door in _doors.Values )
		{
			door.SaveData();
			saved++;
		}

		if ( saved > 0 )
			Log.Info( $"Hexagon: Saved {saved} door(s)." );
	}
}
