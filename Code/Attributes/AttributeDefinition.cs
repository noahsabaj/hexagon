namespace Hexagon.Attributes;

/// <summary>
/// Defines an attribute type (e.g. Hunger, Stamina, Health).
/// Create .attrib asset files in the s&box editor to define attribute types.
///
/// Auto-registers with AttributeManager when the asset loads.
/// </summary>
[AssetType( Name = "Attribute", Extension = "attrib" )]
public class AttributeDefinition : GameResource
{
	/// <summary>
	/// Unique identifier for this attribute (e.g. "hunger", "stamina").
	/// </summary>
	[Property] public string UniqueId { get; set; }

	/// <summary>
	/// Display name shown in UI.
	/// </summary>
	[Property] public string DisplayName { get; set; }

	/// <summary>
	/// Minimum value this attribute can reach.
	/// </summary>
	[Property] public float MinValue { get; set; } = 0f;

	/// <summary>
	/// Maximum value this attribute can reach.
	/// </summary>
	[Property] public float MaxValue { get; set; } = 100f;

	/// <summary>
	/// Starting value for new characters.
	/// </summary>
	[Property] public float StartValue { get; set; } = 0f;

	protected override void PostLoad()
	{
		base.PostLoad();
		if ( !string.IsNullOrEmpty( UniqueId ) )
			AttributeManager.RegisterDefinition( this );
	}

	protected override void PostReload()
	{
		base.PostReload();
		if ( !string.IsNullOrEmpty( UniqueId ) )
			AttributeManager.RegisterDefinition( this );
	}
}
