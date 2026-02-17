namespace Hexagon.Factions;

/// <summary>
/// Class loadout application permission and notification events.
/// </summary>
public interface IHexLoadoutEvent : ISceneEvent<IHexLoadoutEvent>
{
	bool CanApplyLoadout( HexPlayerComponent player, Characters.HexCharacter character, ClassDefinition classDef ) => true;
	void OnLoadoutApplied( HexPlayerComponent player, Characters.HexCharacter character, List<Items.ItemInstance> items ) { }
}
