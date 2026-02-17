namespace Hexagon.Characters;

/// <summary>
/// Player connection lifecycle events.
/// </summary>
public interface IHexPlayerEvent : ISceneEvent<IHexPlayerEvent>
{
	void OnPlayerConnected( HexPlayerComponent player, Connection connection ) { }
	void OnPlayerDisconnected( HexPlayerComponent player, Connection connection ) { }
	Vector3 GetSpawnPosition( Connection connection, Vector3 currentPosition ) => currentPosition;
}
