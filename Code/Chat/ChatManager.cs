namespace Hexagon.Chat;

/// <summary>
/// Central chat routing system. Registers chat classes, parses prefixes,
/// routes messages to the correct chat class, and delegates to CommandManager for non-chat / prefixed input.
/// </summary>
public static class ChatManager
{
	private static readonly Dictionary<string, IChatClass> _chatClasses = new();
	private static readonly Dictionary<string, string> _aliases = new();
	private static IChatClass _defaultChat;

	internal static void Initialize()
	{
		_chatClasses.Clear();
		_aliases.Clear();
		_defaultChat = null;

		BuiltInChats.Register();

		Log.Info( $"Hexagon: ChatManager initialized with {_chatClasses.Count + (_defaultChat != null ? 1 : 0)} chat classes." );
	}

	/// <summary>
	/// Register a chat class. If prefix is empty, it becomes the default IC chat.
	/// </summary>
	public static void Register( IChatClass chatClass )
	{
		if ( string.IsNullOrEmpty( chatClass.Prefix ) )
		{
			_defaultChat = chatClass;
		}
		else
		{
			var key = chatClass.Prefix.ToLower().TrimStart( '/' );
			_chatClasses[key] = chatClass;
		}
	}

	/// <summary>
	/// Register an alias for a chat class prefix (e.g. "//" → "ooc").
	/// </summary>
	public static void RegisterAlias( string alias, string targetPrefix )
	{
		_aliases[alias.ToLower()] = targetPrefix.ToLower().TrimStart( '/' );
	}

	/// <summary>
	/// Get a registered chat class by prefix.
	/// </summary>
	public static IChatClass GetChatClass( string prefix )
	{
		var key = prefix.ToLower().TrimStart( '/' );

		if ( _chatClasses.TryGetValue( key, out var chat ) )
			return chat;

		// Check aliases
		if ( _aliases.TryGetValue( key, out var target ) && _chatClasses.TryGetValue( target, out var aliasedChat ) )
			return aliasedChat;

		return null;
	}

	/// <summary>
	/// Get the default (no-prefix) chat class.
	/// </summary>
	public static IChatClass GetDefaultChat() => _defaultChat;

	/// <summary>
	/// Get all registered chat classes.
	/// </summary>
	public static IReadOnlyDictionary<string, IChatClass> GetAllChatClasses() => _chatClasses;

	/// <summary>
	/// Process a raw message from a player. Handles prefix parsing, chat class routing,
	/// and falls through to CommandManager for unrecognized / prefixed input.
	/// </summary>
	public static void ProcessMessage( HexPlayerComponent sender, string rawMessage )
	{
		if ( sender == null ) return;

		var message = rawMessage?.Trim();
		if ( string.IsNullOrEmpty( message ) ) return;

		// Check for special alias prefixes first (e.g. "//", ".//")
		IChatClass chatClass = null;
		string chatMessage = null;

		if ( TryMatchAlias( message, out var matchedChat, out var remainder ) )
		{
			chatClass = matchedChat;
			chatMessage = remainder;
		}
		else if ( message.StartsWith( "/" ) )
		{
			// Extract the command/prefix word
			var spaceIndex = message.IndexOf( ' ', 1 );
			var prefix = spaceIndex > 0 ? message[1..spaceIndex] : message[1..];
			var rest = spaceIndex > 0 ? message[(spaceIndex + 1)..].Trim() : "";

			chatClass = GetChatClass( prefix );

			if ( chatClass != null )
			{
				chatMessage = rest;
			}
			else
			{
				// Not a chat class — route to CommandManager
				var result = Commands.CommandManager.Execute( sender, prefix, rest );
				if ( !string.IsNullOrEmpty( result ) )
				{
					SendSystemMessage( sender, result );
				}
				return;
			}
		}
		else
		{
			// No prefix — default IC chat
			chatClass = _defaultChat;
			chatMessage = message;
		}

		if ( chatClass == null )
		{
			SendSystemMessage( sender, "No default chat class registered." );
			return;
		}

		// CanSay check on the chat class
		if ( !chatClass.CanSay( sender, chatMessage ) )
			return;

		// Hook: ICanSendChatMessage
		if ( !HexEvents.CanAll<ICanSendChatMessage>(
			x => x.CanSendChatMessage( sender, chatClass, chatMessage ) ) )
			return;

		// Special handling for PM — first word is target player name
		if ( chatClass is PMChat )
		{
			HandlePM( sender, chatClass, chatMessage );
			return;
		}

		// Format the message
		var formatted = chatClass.Format( sender, chatMessage );

		// Determine recipients
		var recipients = GetRecipients( sender, chatClass );

		// Send via HexChatComponent
		if ( HexChatComponent.Instance != null && recipients.Count > 0 )
		{
			var color = chatClass.Color;

			// Recognition-aware per-recipient formatting for IC-type chats
			var recognitionEnabled = Config.HexConfig.Get<bool>( "recognition.enabled", true );
			var isRecognitionChat = chatClass.Range > 0
				&& chatClass is not OOCChat
				&& chatClass is not LOOCChat;

			if ( recognitionEnabled && isRecognitionChat )
			{
				foreach ( var conn in recipients )
				{
					var listener = HexGameManager.GetPlayer( conn );
					if ( listener == null ) continue;

					var personalName = Characters.RecognitionManager.GetDisplayNameForChat( listener, sender );
					var personalFormatted = Characters.RecognitionManager.FormatForListener( listener, sender, formatted );

					using ( Rpc.FilterInclude( conn ) )
					{
						HexChatComponent.Instance.ReceiveMessage(
							personalName,
							chatClass.Name,
							personalFormatted,
							color.r, color.g, color.b
						);
					}
				}
			}
			else
			{
				using ( Rpc.FilterInclude( recipients ) )
				{
					HexChatComponent.Instance.ReceiveMessage(
						sender.CharacterName,
						chatClass.Name,
						formatted,
						color.r, color.g, color.b
					);
				}
			}
		}

		// Fire server-side event
		HexEvents.Fire<IChatMessageListener>(
			x => x.OnChatMessage( sender, chatClass, chatMessage, formatted ) );
	}

	/// <summary>
	/// Send a system message to a specific player only.
	/// </summary>
	public static void SendSystemMessage( HexPlayerComponent target, string message )
	{
		if ( target?.Connection == null ) return;
		if ( HexChatComponent.Instance == null ) return;

		using ( Rpc.FilterInclude( target.Connection ) )
		{
			HexChatComponent.Instance.ReceiveMessage(
				"", "System", message, 1f, 1f, 0.4f
			);
		}
	}

	/// <summary>
	/// Send a formatted message to specific recipients with a given chat class appearance.
	/// Useful for PM and other targeted chat.
	/// </summary>
	public static void SendDirectMessage( HexPlayerComponent sender, Connection target,
		IChatClass chatClass, string formatted )
	{
		if ( HexChatComponent.Instance == null ) return;

		var color = chatClass.Color;
		using ( Rpc.FilterInclude( target ) )
		{
			HexChatComponent.Instance.ReceiveMessage(
				sender.CharacterName,
				chatClass.Name,
				formatted,
				color.r, color.g, color.b
			);
		}
	}

	private static void HandlePM( HexPlayerComponent sender, IChatClass chatClass, string chatMessage )
	{
		if ( string.IsNullOrWhiteSpace( chatMessage ) )
		{
			SendSystemMessage( sender, "Usage: /pm <player> <message>" );
			return;
		}

		// First word = target name, rest = message
		var spaceIdx = chatMessage.IndexOf( ' ' );
		if ( spaceIdx <= 0 )
		{
			SendSystemMessage( sender, "Usage: /pm <player> <message>" );
			return;
		}

		var targetName = chatMessage[..spaceIdx];
		var pmMessage = chatMessage[(spaceIdx + 1)..].Trim();

		if ( string.IsNullOrEmpty( pmMessage ) )
		{
			SendSystemMessage( sender, "Usage: /pm <player> <message>" );
			return;
		}

		// Find target player by partial name match
		HexPlayerComponent target = null;
		var lowerTarget = targetName.ToLower();

		foreach ( var kvp in HexGameManager.Players )
		{
			if ( kvp.Value.DisplayName.ToLower().Contains( lowerTarget ) )
			{
				target = kvp.Value;
				break;
			}
		}

		if ( target == null || target.Connection == null )
		{
			SendSystemMessage( sender, $"Player '{targetName}' not found." );
			return;
		}

		var formatted = chatClass.Format( sender, pmMessage );

		// Send to target
		SendDirectMessage( sender, target.Connection, chatClass, formatted );

		// Also send confirmation to sender
		if ( sender.Connection != null && sender.Connection != target.Connection )
		{
			var confirmMsg = $"[PM to {target.DisplayName}]: {pmMessage}";
			using ( Rpc.FilterInclude( sender.Connection ) )
			{
				HexChatComponent.Instance.ReceiveMessage(
					sender.DisplayName, chatClass.Name, confirmMsg,
					chatClass.Color.r, chatClass.Color.g, chatClass.Color.b
				);
			}
		}

		HexEvents.Fire<IChatMessageListener>(
			x => x.OnChatMessage( sender, chatClass, pmMessage, formatted ) );
	}

	private static List<Connection> GetRecipients( HexPlayerComponent sender, IChatClass chatClass )
	{
		var recipients = new List<Connection>();
		var range = chatClass.Range;

		foreach ( var kvp in HexGameManager.Players )
		{
			var listener = kvp.Value;
			if ( listener?.Connection == null ) continue;

			// Range check
			if ( range > 0 )
			{
				var dist = Vector3.DistanceBetween(
					sender.WorldPosition, listener.WorldPosition );

				if ( dist > range )
					continue;
			}

			// Chat class CanHear check
			if ( !chatClass.CanHear( sender, listener ) )
				continue;

			recipients.Add( listener.Connection );
		}

		return recipients;
	}

	private static bool TryMatchAlias( string message, out IChatClass chat, out string remainder )
	{
		chat = null;
		remainder = null;

		// Check ".// " prefix for LOOC
		if ( message.StartsWith( ".//" ) )
		{
			if ( _aliases.TryGetValue( ".//", out var loocTarget ) &&
				_chatClasses.TryGetValue( loocTarget, out var loocChat ) )
			{
				chat = loocChat;
				remainder = message.Length > 3 ? message[3..].Trim() : "";
				return true;
			}
		}

		// Check "//" prefix for OOC
		if ( message.StartsWith( "//" ) )
		{
			if ( _aliases.TryGetValue( "//", out var oocTarget ) &&
				_chatClasses.TryGetValue( oocTarget, out var oocChat ) )
			{
				chat = oocChat;
				remainder = message.Length > 2 ? message[2..].Trim() : "";
				return true;
			}
		}

		return false;
	}
}

/// <summary>
/// Permission hook: can a player send a chat message? Return false to block.
/// </summary>
public interface ICanSendChatMessage
{
	bool CanSendChatMessage( HexPlayerComponent sender, IChatClass chatClass, string message );
}

/// <summary>
/// Server-side: fired after a chat message is sent.
/// </summary>
public interface IChatMessageListener
{
	void OnChatMessage( HexPlayerComponent sender, IChatClass chatClass, string rawMessage, string formattedMessage );
}

/// <summary>
/// Client-side: fired when a chat message is received.
/// </summary>
public interface IChatMessageReceivedListener
{
	void OnChatMessageReceived( string senderName, string chatClassName, string formattedMessage, Color color );
}
