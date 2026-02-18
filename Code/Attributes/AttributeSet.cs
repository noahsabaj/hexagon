namespace Hexagon.Attributes;

/// <summary>
/// Typed accessor for a character's attributes. Provides indexer syntax as a
/// convenience wrapper over AttributeManager, with no behavior change.
///
/// Usage:
///   character.Attributes["hunger"]          // get
///   character.Attributes["hunger"] = 50f    // set (fires OnAttributeChanged)
///   character.Attributes.Get("hunger")      // explicit get
///   character.Attributes.Set("hunger", 50f) // explicit set
/// </summary>
public sealed class AttributeSet
{
	private readonly Characters.HexCharacter _character;

	public AttributeSet( Characters.HexCharacter character )
	{
		_character = character;
	}

	/// <summary>
	/// Get the effective attribute value (base + boosts, clamped to definition range).
	/// </summary>
	public float Get( string id ) => AttributeManager.GetAttribute( _character, id );

	/// <summary>
	/// Set the base attribute value. Fires IHexAttributeEvent.OnAttributeChanged.
	/// </summary>
	public void Set( string id, float value ) => AttributeManager.SetAttribute( _character, id, value );

	/// <summary>
	/// Indexer: character.Attributes["hunger"] = 50f
	/// </summary>
	public float this[string id]
	{
		get => Get( id );
		set => Set( id, value );
	}
}
