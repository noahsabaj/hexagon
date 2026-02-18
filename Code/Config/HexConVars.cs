namespace Hexagon.Config;

/// <summary>
/// ConVar declarations for Hexagon framework configuration values.
///
/// Each ConVar getter reads from HexConfig (which holds the registered default from
/// DefaultConfigs plus any saved overrides). Each setter writes back to HexConfig so
/// console changes (e.g. "hex_walk_speed 300") immediately update the framework config.
///
/// DefaultConfigs.Register() is the single source of truth for default values.
/// No fallback literals are duplicated here — the registered defaults always win.
/// </summary>
public static class HexConVars
{
	// --- Gameplay: Movement ---

	[ConVar( "hex_walk_speed" )]
	public static float WalkSpeed
	{
		get => HexConfig.Get<float>( "gameplay.walkSpeed" );
		set => HexConfig.Set( "gameplay.walkSpeed", value );
	}

	[ConVar( "hex_run_speed" )]
	public static float RunSpeed
	{
		get => HexConfig.Get<float>( "gameplay.runSpeed" );
		set => HexConfig.Set( "gameplay.runSpeed", value );
	}

	// --- Gameplay: Characters ---

	[ConVar( "hex_char_max_per_player" )]
	public static int CharMaxPerPlayer
	{
		get => HexConfig.Get<int>( "character.maxPerPlayer" );
		set => HexConfig.Set( "character.maxPerPlayer", value );
	}

	[ConVar( "hex_char_auto_load" )]
	public static bool CharAutoLoad
	{
		get => HexConfig.Get<bool>( "character.autoLoad" );
		set => HexConfig.Set( "character.autoLoad", value );
	}

	// --- Chat ---

	[ConVar( "hex_chat_ic_range" )]
	public static float ChatIcRange
	{
		get => HexConfig.Get<float>( "chat.icRange" );
		set => HexConfig.Set( "chat.icRange", value );
	}

	[ConVar( "hex_chat_whisper_range" )]
	public static float ChatWhisperRange
	{
		get => HexConfig.Get<float>( "chat.whisperRange" );
		set => HexConfig.Set( "chat.whisperRange", value );
	}

	[ConVar( "hex_chat_yell_range" )]
	public static float ChatYellRange
	{
		get => HexConfig.Get<float>( "chat.yellRange" );
		set => HexConfig.Set( "chat.yellRange", value );
	}

	// --- Doors ---

	[ConVar( "hex_door_kick_time" )]
	public static float DoorKickTime
	{
		get => HexConfig.Get<float>( "door.kickTime" );
		set => HexConfig.Set( "door.kickTime", value );
	}

	[ConVar( "hex_door_kick_damage" )]
	public static int DoorKickDamage
	{
		get => HexConfig.Get<int>( "door.kickDamage" );
		set => HexConfig.Set( "door.kickDamage", value );
	}

	// --- Framework ---

	[ConVar( "hex_save_interval" )]
	public static float SaveInterval
	{
		get => HexConfig.Get<float>( "framework.saveInterval" );
		set => HexConfig.Set( "framework.saveInterval", value );
	}

	[ConVar( "hex_recognition_enabled" )]
	public static bool RecognitionEnabled
	{
		get => HexConfig.Get<bool>( "recognition.enabled" );
		set => HexConfig.Set( "recognition.enabled", value );
	}

	// --- Inventory ---

	[ConVar( "hex_inv_default_width" )]
	public static int InvDefaultWidth
	{
		get => HexConfig.Get<int>( "inventory.defaultWidth" );
		set => HexConfig.Set( "inventory.defaultWidth", value );
	}

	[ConVar( "hex_inv_default_height" )]
	public static int InvDefaultHeight
	{
		get => HexConfig.Get<int>( "inventory.defaultHeight" );
		set => HexConfig.Set( "inventory.defaultHeight", value );
	}

	// --- Attributes ---

	[ConVar( "hex_attr_boost_max" )]
	public static int AttrBoostMax
	{
		get => HexConfig.Get<int>( "attributes.boostMax" );
		set => HexConfig.Set( "attributes.boostMax", value );
	}
}
