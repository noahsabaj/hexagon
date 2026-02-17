namespace Hexagon.Core;

/// <summary>
/// Scene-placed configuration for the Hexagon framework.
/// Place this on a GameObject in your scene to configure spawn and player settings.
/// Read by <see cref="HexagonSystem"/> during initialization and player connection.
/// </summary>
public sealed class HexagonConfigComponent : Component
{
	/// <summary>
	/// Optional prefab to spawn for each player when their character loads.
	/// If null, a default first-person player is built (PlayerController, citizen model, Dresser).
	/// </summary>
	[Property] public GameObject PlayerPrefab { get; set; }

	/// <summary>
	/// World position to spawn players at. Can be further modified via
	/// <see cref="IHexPlayerEvent.GetSpawnPosition"/>.
	/// </summary>
	[Property] public Vector3 SpawnPosition { get; set; } = new( 0, 0, 100 );
}
