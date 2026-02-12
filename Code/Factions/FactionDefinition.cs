namespace Hexagon.Factions;

/// <summary>
/// Defines a faction that characters can belong to. Create .faction files in your
/// schema's Assets folder via the s&box editor.
///
/// Example: Citizens faction, Combine faction, Rebels faction, etc.
/// </summary>
[AssetType( Name = "Faction", Extension = "faction" )]
public class FactionDefinition : GameResource
{
	/// <summary>
	/// Unique identifier for this faction. Used in code and persistence.
	/// </summary>
	[Property] public string UniqueId { get; set; }

	/// <summary>
	/// Display name of the faction.
	/// </summary>
	[Property] public string Name { get; set; }

	/// <summary>
	/// Description shown in character creation.
	/// </summary>
	[Property, TextArea] public string Description { get; set; }

	/// <summary>
	/// Faction color used in chat, scoreboard, etc.
	/// </summary>
	[Property] public Color Color { get; set; } = Color.White;

	/// <summary>
	/// Models available to characters in this faction.
	/// </summary>
	[Property] public List<Model> Models { get; set; } = new();

	/// <summary>
	/// If true, players can create characters in this faction without a whitelist.
	/// </summary>
	[Property] public bool IsDefault { get; set; } = true;

	/// <summary>
	/// Maximum active players in this faction (0 = unlimited).
	/// </summary>
	[Property] public int MaxPlayers { get; set; } = 0;

	/// <summary>
	/// Sort order in character creation UI. Lower = appears first.
	/// </summary>
	[Property] public int Order { get; set; } = 100;

	/// <summary>
	/// Starting money for characters created in this faction.
	/// If -1, uses the global currency.startingAmount config.
	/// </summary>
	[Property] public int StartingMoney { get; set; } = -1;

	/// <summary>
	/// Called when the asset is loaded. Registers with FactionManager.
	/// </summary>
	protected override void PostLoad()
	{
		base.PostLoad();

		if ( !string.IsNullOrEmpty( UniqueId ) )
			FactionManager.Register( this );
	}

	protected override void PostReload()
	{
		base.PostReload();

		if ( !string.IsNullOrEmpty( UniqueId ) )
			FactionManager.Register( this );
	}
}
