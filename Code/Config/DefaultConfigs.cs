namespace Hexagon.Config;

/// <summary>
/// Registers all built-in framework configuration values.
/// Schema devs and plugins can add their own via HexConfig.Add().
/// </summary>
internal static class DefaultConfigs
{
	internal static void Register()
	{
		// Framework
		HexConfig.Add( "framework.saveInterval", 300f, "Seconds between auto-saves", "Framework" );
		HexConfig.Add( "framework.debug", false, "Enable debug logging", "Framework" );

		// Characters
		HexConfig.Add( "character.autoLoad", false, "Auto-load last character on connect", "Characters" );
		HexConfig.Add( "character.maxPerPlayer", 5, "Maximum characters per player", "Characters" );
		HexConfig.Add( "character.nameMinLength", 3, "Minimum character name length", "Characters" );
		HexConfig.Add( "character.nameMaxLength", 64, "Maximum character name length", "Characters" );
		HexConfig.Add( "character.descMinLength", 16, "Minimum character description length", "Characters" );
		HexConfig.Add( "character.descMaxLength", 512, "Maximum character description length", "Characters" );

		// Chat
		HexConfig.Add( "chat.icRange", 300f, "In-character chat range (units)", "Chat" );
		HexConfig.Add( "chat.whisperRange", 100f, "Whisper chat range (units)", "Chat" );
		HexConfig.Add( "chat.yellRange", 600f, "Yell chat range (units)", "Chat" );
		HexConfig.Add( "chat.oocDelay", 1f, "Seconds between OOC messages", "Chat" );

		// Inventory
		HexConfig.Add( "inventory.defaultWidth", 4, "Default inventory width", "Inventory" );
		HexConfig.Add( "inventory.defaultHeight", 4, "Default inventory height", "Inventory" );

		// Currency
		HexConfig.Add( "currency.symbol", "$", "Currency symbol", "Currency" );
		HexConfig.Add( "currency.singular", "dollar", "Currency name (singular)", "Currency" );
		HexConfig.Add( "currency.plural", "dollars", "Currency name (plural)", "Currency" );
		HexConfig.Add( "currency.startingAmount", 0, "Starting money for new characters", "Currency" );

		// Attributes
		HexConfig.Add( "attributes.boostMax", 50, "Maximum active boosts per character", "Attributes" );

		// Storage
		HexConfig.Add( "storage.defaultWidth", 4, "Default storage container width", "Storage" );
		HexConfig.Add( "storage.defaultHeight", 4, "Default storage container height", "Storage" );

		// Doors
		HexConfig.Add( "door.allowFactionOwnership", true, "Allow factions to own doors", "Doors" );

		// Vendors
		HexConfig.Add( "vendor.maxItems", 50, "Maximum items per vendor catalog", "Vendors" );

		// UI
		HexConfig.Add( "ui.chatMaxHistory", 100, "Maximum chat messages in history", "UI" );
		HexConfig.Add( "ui.deathRespawnTime", 5f, "Seconds before respawn is available", "UI" );
		HexConfig.Add( "ui.inventoryCellSize", 64, "Inventory cell size in pixels", "UI" );

		// Gameplay
		HexConfig.Add( "gameplay.walkSpeed", 200f, "Default walk speed", "Gameplay" );
		HexConfig.Add( "gameplay.runSpeed", 320f, "Default run speed", "Gameplay" );
	}
}
