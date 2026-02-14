namespace Hexagon.Characters;

/// <summary>
/// Listens for character load events and applies character model/speeds to the player.
/// Added to the HexagonFramework GameObject during initialization.
/// </summary>
public sealed class HexModelHandler : Component, ICharacterLoadedListener
{
	/// <summary>
	/// When a character loads, build the player body (if needed) and apply model/speeds.
	/// </summary>
	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Build player body if not yet built
		if ( player.GetComponent<PlayerController>() == null )
		{
			var prefab = player.Scene.GetAll<HexGameManager>().FirstOrDefault()?.PlayerPrefab;
			HexPlayerSetup.BuildPlayerBody( player, prefab );
		}

		HexPlayerSetup.ApplyCharacterToPlayer( player, character );
	}
}
