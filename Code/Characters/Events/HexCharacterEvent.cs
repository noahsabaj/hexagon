namespace Hexagon.Characters;

/// <summary>
/// Character lifecycle events — creation, loading, unloading, recognition.
/// </summary>
public interface IHexCharacterEvent : ISceneEvent<IHexCharacterEvent>
{
	void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character ) { }
	void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character ) { }
	void OnCharacterCreated( HexPlayerComponent player, HexCharacter character ) { }
	bool CanCharacterCreate( HexPlayerComponent player, HexCharacterData data ) => true;
	bool CanRecognize( HexCharacter observer, HexCharacter target ) => true;
	void OnCharacterRecognized( HexPlayerComponent introducer, HexPlayerComponent recognizer ) { }
}
