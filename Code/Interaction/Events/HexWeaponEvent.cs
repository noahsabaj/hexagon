namespace Hexagon.Interaction;

/// <summary>
/// Weapon raise/fire permission and state change events.
/// </summary>
public interface IHexWeaponEvent : ISceneEvent<IHexWeaponEvent>
{
	bool CanRaiseWeapon( HexPlayerComponent player ) => true;
	void OnWeaponRaised( HexPlayerComponent player, bool isRaised ) { }
	bool CanFireWeapon( HexPlayerComponent player ) => true;
}
