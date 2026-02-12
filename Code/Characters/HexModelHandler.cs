namespace Hexagon.Characters;

/// <summary>
/// Listens for character load events and applies character model/speeds to the player.
/// Added to the HexagonFramework GameObject during initialization.
/// </summary>
public sealed class HexModelHandler : Component, ICharacterLoadedListener
{
	/// <summary>
	/// When a character loads, apply their model and speed settings.
	/// </summary>
	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		HexPlayerSetup.ApplyCharacterToPlayer( player, character );
	}
}
