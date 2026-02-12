namespace Hexagon.Factions;

/// <summary>
/// Defines a class (sub-role) within a faction. Create .class files in your
/// schema's Assets folder via the s&box editor.
///
/// Example: "Civil Protection Officer", "Medic", "Engineer", etc.
/// </summary>
[AssetType( Name = "Class", Extension = "class" )]
public class ClassDefinition : GameResource
{
	/// <summary>
	/// Unique identifier for this class.
	/// </summary>
	[Property] public string UniqueId { get; set; }

	/// <summary>
	/// Display name of the class.
	/// </summary>
	[Property] public string Name { get; set; }

	/// <summary>
	/// Description of this class.
	/// </summary>
	[Property, TextArea] public string Description { get; set; }

	/// <summary>
	/// The faction this class belongs to (reference the FactionDefinition UniqueId).
	/// </summary>
	[Property] public string FactionId { get; set; }

	/// <summary>
	/// Override model when this class is active. Empty = use faction default.
	/// </summary>
	[Property] public Model ClassModel { get; set; }

	/// <summary>
	/// Maximum active players in this class (0 = unlimited).
	/// </summary>
	[Property] public int MaxPlayers { get; set; } = 0;

	/// <summary>
	/// Sort order within the faction. Lower = appears first.
	/// </summary>
	[Property] public int Order { get; set; } = 100;

	/// <summary>
	/// Called when the asset is loaded. Registers with FactionManager.
	/// </summary>
	protected override void PostLoad()
	{
		base.PostLoad();

		if ( !string.IsNullOrEmpty( UniqueId ) )
			FactionManager.RegisterClass( this );
	}

	protected override void PostReload()
	{
		base.PostReload();

		if ( !string.IsNullOrEmpty( UniqueId ) )
			FactionManager.RegisterClass( this );
	}
}
