namespace Hexagon.Characters;

/// <summary>
/// Character CRUD result events, dispatched on the client after server round-trips.
/// Panels listen for these via ISceneEvent instead of subscribing to C# delegates
/// on CharacterCrudComponent directly.
/// </summary>
public interface IHexCrudEvent : ISceneEvent<IHexCrudEvent>
{
	/// <summary>Called when the server sends a fresh character list.</summary>
	void OnCharacterListReceived() { }

	/// <summary>Called when the server responds to a character creation request.</summary>
	void OnCharacterCreateResult( bool success, string message ) { }
}
