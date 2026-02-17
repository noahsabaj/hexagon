namespace Hexagon.Doors;

/// <summary>
/// Door interaction, damage, and ownership events.
/// </summary>
public interface IHexDoorEvent : ISceneEvent<IHexDoorEvent>
{
	bool CanUseDoor( HexPlayerComponent player, DoorComponent door ) => true;
	void OnDoorUsed( HexPlayerComponent player, DoorComponent door ) { }
	void OnDoorOwnerChanged( DoorComponent door, string oldOwnerId, string newOwnerId, bool isFaction ) { }
	void OnDoorDamaged( HexPlayerComponent attacker, DoorComponent door, float damage, int remainingHealth ) { }
	void OnDoorBreached( HexPlayerComponent attacker, DoorComponent door ) { }
	bool CanKickDoor( HexPlayerComponent player, DoorComponent door ) => true;
}
