namespace Hexagon.Attributes;

/// <summary>
/// Character attribute value change events.
/// </summary>
public interface IHexAttributeEvent : ISceneEvent<IHexAttributeEvent>
{
	void OnAttributeChanged( HexCharacter character, string attributeId, float oldValue, float newValue ) { }
}
