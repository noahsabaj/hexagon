namespace Hexagon.Chat;

/// <summary>
/// Default in-character chat. No prefix. Range-limited.
/// Format: {Name} says "{message}"
/// </summary>
public class ICChat : IChatClass
{
	public string Name => "IC";
	public string Prefix => "";
	public float Range => Config.HexConfig.Get<float>( "chat.icRange", 300f );
	public Color Color => Color.White;

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"{speaker.CharacterName} says \"{message}\"";
	}
}

/// <summary>
/// Yell chat. Prefix: /y. Extended range.
/// Format: {Name} yells "{message}!"
/// </summary>
public class YellChat : IChatClass
{
	public string Name => "Yell";
	public string Prefix => "/y";
	public float Range => Config.HexConfig.Get<float>( "chat.yellRange", 600f );
	public Color Color => Color.Yellow;

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"{speaker.CharacterName} yells \"{message}!\"";
	}
}

/// <summary>
/// Whisper chat. Prefix: /w. Short range.
/// Format: {Name} whispers "{message}"
/// </summary>
public class WhisperChat : IChatClass
{
	public string Name => "Whisper";
	public string Prefix => "/w";
	public float Range => Config.HexConfig.Get<float>( "chat.whisperRange", 100f );
	public Color Color => new Color( 0.7f, 0.7f, 0.7f );

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"{speaker.CharacterName} whispers \"{message}\"";
	}
}

/// <summary>
/// Action/emote chat. Prefix: /me. IC range.
/// Format: ** {Name} {message} **
/// </summary>
public class MeChat : IChatClass
{
	public string Name => "Me";
	public string Prefix => "/me";
	public float Range => Config.HexConfig.Get<float>( "chat.icRange", 300f );
	public Color Color => new Color( 1f, 0.6f, 0.2f ); // Orange

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"** {speaker.CharacterName} {message} **";
	}
}

/// <summary>
/// Impersonal action chat. Prefix: /it. IC range.
/// Format: ** {message} **
/// </summary>
public class ItChat : IChatClass
{
	public string Name => "It";
	public string Prefix => "/it";
	public float Range => Config.HexConfig.Get<float>( "chat.icRange", 300f );
	public Color Color => new Color( 1f, 0.6f, 0.2f ); // Orange

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"** {message} **";
	}
}

/// <summary>
/// Out-of-character global chat. Prefix: /ooc (alias: //).
/// Format: (OOC) {Name}: {message}
/// Rate-limited by chat.oocDelay config.
/// </summary>
public class OOCChat : IChatClass
{
	public string Name => "OOC";
	public string Prefix => "/ooc";
	public float Range => 0f; // Global
	public Color Color => new Color( 1f, 0.2f, 0.2f ); // Red

	private readonly Dictionary<ulong, RealTimeSince> _lastSent = new();

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		var delay = Config.HexConfig.Get<float>( "chat.oocDelay", 1f );

		if ( _lastSent.TryGetValue( speaker.SteamId, out var since ) && since < delay )
			return false;

		_lastSent[speaker.SteamId] = 0f;
		return true;
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"(OOC) {speaker.DisplayName}: {message}";
	}
}

/// <summary>
/// Local out-of-character chat. Prefix: /looc (alias: .//).
/// Format: (LOOC) {Name}: {message}
/// </summary>
public class LOOCChat : IChatClass
{
	public string Name => "LOOC";
	public string Prefix => "/looc";
	public float Range => Config.HexConfig.Get<float>( "chat.icRange", 300f );
	public Color Color => new Color( 1f, 0.5f, 0.5f ); // Light red

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;

	public bool CanSay( HexPlayerComponent speaker, string message ) => true;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"(LOOC) {speaker.DisplayName}: {message}";
	}
}

/// <summary>
/// Dice roll chat. Prefix: /roll. IC range.
/// Format: ** {Name} rolled {1-100} out of 100 **
/// The message text is ignored; a random roll is generated.
/// </summary>
public class RollChat : IChatClass
{
	public string Name => "Roll";
	public string Prefix => "/roll";
	public float Range => Config.HexConfig.Get<float>( "chat.icRange", 300f );
	public Color Color => new Color( 0.3f, 0.9f, 0.9f ); // Cyan

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => speaker.HasActiveCharacter;

	public string Format( HexPlayerComponent speaker, string message )
	{
		var roll = Game.Random.Next( 1, 101 );
		return $"** {speaker.CharacterName} rolled {roll} out of 100 **";
	}
}

/// <summary>
/// Private message chat. Prefix: /pm. Direct to a single player.
/// First word after prefix is the target player name.
/// Format: [PM from {Name}]: {message}
/// </summary>
public class PMChat : IChatClass
{
	public string Name => "PM";
	public string Prefix => "/pm";
	public float Range => 0f;
	public Color Color => new Color( 0.7f, 0.3f, 1f ); // Purple

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;
	public bool CanSay( HexPlayerComponent speaker, string message ) => true;

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"[PM from {speaker.DisplayName}]: {message}";
	}
}

/// <summary>
/// Event announcer chat. Prefix: /event. Global range. Requires admin flag.
/// Format: ** {message} **
/// </summary>
public class EventChat : IChatClass
{
	public string Name => "Event";
	public string Prefix => "/event";
	public float Range => 0f; // Global
	public Color Color => new Color( 1f, 0.84f, 0f ); // Gold

	public bool CanHear( HexPlayerComponent speaker, HexPlayerComponent listener ) => true;

	public bool CanSay( HexPlayerComponent speaker, string message )
	{
		return Permissions.PermissionManager.HasPermission( speaker, "a" );
	}

	public string Format( HexPlayerComponent speaker, string message )
	{
		return $"** {message} **";
	}
}

/// <summary>
/// Registers all built-in chat classes and aliases.
/// Called during ChatManager initialization.
/// </summary>
internal static class BuiltInChats
{
	internal static void Register()
	{
		ChatManager.Register( new ICChat() );
		ChatManager.Register( new YellChat() );
		ChatManager.Register( new WhisperChat() );
		ChatManager.Register( new MeChat() );
		ChatManager.Register( new ItChat() );
		ChatManager.Register( new OOCChat() );
		ChatManager.Register( new LOOCChat() );
		ChatManager.Register( new RollChat() );
		ChatManager.Register( new PMChat() );
		ChatManager.Register( new EventChat() );

		// Aliases
		ChatManager.RegisterAlias( "//", "ooc" );
		ChatManager.RegisterAlias( ".//", "looc" );
	}
}
