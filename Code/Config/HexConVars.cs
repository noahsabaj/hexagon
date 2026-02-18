namespace Hexagon.Config;

/// <summary>
/// ConVar declarations for Hexagon framework configuration values.
///
/// Each ConVar setter calls HexConfig.Set() so console changes (e.g. "hex_walk_speed 300")
/// immediately update the framework configuration. HexConfig.OnChanged then pushes the value
/// back to the ConVar property via ConVarBridge — a re-entry guard prevents loops.
///
/// The primary API for reading config remains HexConfig.Get() — ConVars are a bridge,
/// not a replacement. Plugin configs (hl2rp.*) still use HexConfig.Add() and .Get().
/// </summary>
public static class HexConVars
{
	// --- Gameplay: Movement ---

	[ConVar( "hex_walk_speed" )]
	public static float WalkSpeed
	{
		get => HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
		set => HexConfig.Set( "gameplay.walkSpeed", value );
	}

	[ConVar( "hex_run_speed" )]
	public static float RunSpeed
	{
		get => HexConfig.Get<float>( "gameplay.runSpeed", 300f );
		set => HexConfig.Set( "gameplay.runSpeed", value );
	}

	// --- Gameplay: Characters ---

	[ConVar( "hex_char_max_per_player" )]
	public static int CharMaxPerPlayer
	{
		get => HexConfig.Get<int>( "character.maxPerPlayer", 5 );
		set => HexConfig.Set( "character.maxPerPlayer", value );
	}

	[ConVar( "hex_char_auto_load" )]
	public static bool CharAutoLoad
	{
		get => HexConfig.Get<bool>( "character.autoLoad", false );
		set => HexConfig.Set( "character.autoLoad", value );
	}

	// --- Chat ---

	[ConVar( "hex_chat_ic_range" )]
	public static float ChatIcRange
	{
		get => HexConfig.Get<float>( "chat.icRange", 300f );
		set => HexConfig.Set( "chat.icRange", value );
	}

	[ConVar( "hex_chat_whisper_range" )]
	public static float ChatWhisperRange
	{
		get => HexConfig.Get<float>( "chat.whisperRange", 75f );
		set => HexConfig.Set( "chat.whisperRange", value );
	}

	[ConVar( "hex_chat_yell_range" )]
	public static float ChatYellRange
	{
		get => HexConfig.Get<float>( "chat.yellRange", 600f );
		set => HexConfig.Set( "chat.yellRange", value );
	}

	// --- Doors ---

	[ConVar( "hex_door_kick_time" )]
	public static float DoorKickTime
	{
		get => HexConfig.Get<float>( "door.kickTime", 5f );
		set => HexConfig.Set( "door.kickTime", value );
	}

	[ConVar( "hex_door_kick_damage" )]
	public static float DoorKickDamage
	{
		get => HexConfig.Get<float>( "door.kickDamage", 10f );
		set => HexConfig.Set( "door.kickDamage", value );
	}

	// --- Framework ---

	[ConVar( "hex_save_interval" )]
	public static float SaveInterval
	{
		get => HexConfig.Get<float>( "framework.saveInterval", 300f );
		set => HexConfig.Set( "framework.saveInterval", value );
	}

	[ConVar( "hex_recognition_enabled" )]
	public static bool RecognitionEnabled
	{
		get => HexConfig.Get<bool>( "recognition.enabled", true );
		set => HexConfig.Set( "recognition.enabled", value );
	}

	// --- Inventory ---

	[ConVar( "hex_inv_default_width" )]
	public static int InvDefaultWidth
	{
		get => HexConfig.Get<int>( "inventory.defaultWidth", 4 );
		set => HexConfig.Set( "inventory.defaultWidth", value );
	}

	[ConVar( "hex_inv_default_height" )]
	public static int InvDefaultHeight
	{
		get => HexConfig.Get<int>( "inventory.defaultHeight", 4 );
		set => HexConfig.Set( "inventory.defaultHeight", value );
	}

	// --- Attributes ---

	[ConVar( "hex_attr_boost_max" )]
	public static int AttrBoostMax
	{
		get => HexConfig.Get<int>( "attributes.boostMax", 50 );
		set => HexConfig.Set( "attributes.boostMax", value );
	}
}
