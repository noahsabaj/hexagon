namespace Hexagon.Doors;

/// <summary>
/// Manages door registration and lookup.
/// DoorComponents register/unregister themselves on enable/disable.
/// Persistence is handled directly by each DoorComponent via DatabaseManager.
/// </summary>
public sealed class DoorManager : GameObjectSystem<DoorManager>
{
	private static DoorManager _instance;
	private static DoorManager Instance => _instance;

	private readonly Dictionary<string, DoorComponent> _doors = new();

	public DoorManager( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	public override void Dispose()
	{
		if ( _instance == this ) _instance = null;
		base.Dispose();
	}

	/// <summary>
	/// Register a door component. Called by DoorComponent.OnEnabled.
	/// </summary>
	internal static void Register( DoorComponent door )
	{
		if ( string.IsNullOrEmpty( door.DoorId ) ) return;
		if ( Instance == null ) return;
		Instance._doors[door.DoorId] = door;
	}

	/// <summary>
	/// Unregister a door component. Called by DoorComponent.OnDisabled.
	/// </summary>
	internal static void Unregister( DoorComponent door )
	{
		if ( string.IsNullOrEmpty( door.DoorId ) ) return;
		Instance?._doors.Remove( door.DoorId );
	}

	/// <summary>
	/// Get a door component by its ID.
	/// </summary>
	public static DoorComponent GetDoor( string doorId )
	{
		return Instance?._doors.GetValueOrDefault( doorId );
	}

	/// <summary>
	/// Get all registered doors.
	/// </summary>
	public static IReadOnlyDictionary<string, DoorComponent> GetAllDoors() => Instance?._doors;
}
