namespace Hexagon.Items;

/// <summary>
/// Item lifecycle events — consumption, etc.
/// </summary>
public interface IHexItemEvent : ISceneEvent<IHexItemEvent>
{
	void OnItemConsumed( HexPlayerComponent player, ItemInstance item ) { }
}
