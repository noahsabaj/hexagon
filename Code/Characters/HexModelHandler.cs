namespace Hexagon.Characters;

/// <summary>
/// Listens for character load events and applies character model/speeds to the player.
/// Added to the Hexagon Services GameObject during initialization.
///
/// Potential optimization: This could be converted to a per-player satellite component
/// placed on each player's GameObject, using PostToGameObject() to only receive events
/// for its own player. However, the current scene-wide listener approach works correctly
/// and the overhead is negligible since only one instance processes the event for the
/// correct player via the player parameter. The conversion would also require changing
/// the IHexCharacterEvent broadcast mechanism in CharacterManager, which affects all
/// other listeners (HexUIManager, RecognitionManager, etc.).
/// </summary>
public sealed class HexModelHandler : Component, IHexCharacterEvent
{
	void IHexCharacterEvent.OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Build player body if not yet built
		if ( player.GetComponent<PlayerController>() == null )
		{
			var prefab = player.Scene.GetAll<HexagonConfigComponent>().FirstOrDefault()?.PlayerPrefab;
			HexPlayerSetup.BuildPlayerBody( player, prefab );
		}

		HexPlayerSetup.ApplyCharacterToPlayer( player, character );
	}
}
