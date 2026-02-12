namespace Hexagon.Commands;

/// <summary>
/// Manages command registration, parsing, permission checking, and execution.
/// Commands are invoked when a player types /{name} in chat and it doesn't match a chat class prefix.
/// </summary>
public static class CommandManager
{
	private static readonly Dictionary<string, HexCommand> _commands = new();
	private static readonly Dictionary<string, string> _aliases = new();

	internal static void Initialize()
	{
		_commands.Clear();
		_aliases.Clear();

		BuiltInCommands.Register();

		Log.Info( $"Hexagon: CommandManager initialized with {_commands.Count} commands." );
	}

	/// <summary>
	/// Register a command.
	/// </summary>
	public static void Register( HexCommand command )
	{
		var key = command.Name.ToLower();
		_commands[key] = command;

		foreach ( var alias in command.Aliases )
		{
			_aliases[alias.ToLower()] = key;
		}
	}

	/// <summary>
	/// Get a command by name or alias.
	/// </summary>
	public static HexCommand GetCommand( string name )
	{
		var key = name.ToLower();

		if ( _commands.TryGetValue( key, out var cmd ) )
			return cmd;

		if ( _aliases.TryGetValue( key, out var target ) && _commands.TryGetValue( target, out var aliasedCmd ) )
			return aliasedCmd;

		return null;
	}

	/// <summary>
	/// Get all registered commands.
	/// </summary>
	public static IReadOnlyDictionary<string, HexCommand> GetAllCommands() => _commands;

	/// <summary>
	/// Execute a command by name with raw argument input. Returns a message to send to the caller.
	/// Called by ChatManager when a /prefixed message doesn't match a chat class.
	/// </summary>
	public static string Execute( HexPlayerComponent caller, string name, string rawArgs )
	{
		var command = GetCommand( name );

		if ( command == null )
			return $"Unknown command: /{name}";

		// Permission check — flag-based
		if ( !string.IsNullOrEmpty( command.Permission ) )
		{
			if ( !Permissions.PermissionManager.HasPermission( caller, command.Permission ) )
				return "You do not have permission to use this command.";
		}

		// Permission check — custom function
		if ( command.PermissionFunc != null && !command.PermissionFunc( caller ) )
			return "You do not have permission to use this command.";

		// Hook: ICanRunCommandListener
		if ( !HexEvents.CanAll<ICanRunCommandListener>(
			x => x.CanRunCommand( caller, command ) ) )
			return "You are not allowed to run this command.";

		// Parse arguments
		var context = ParseArguments( command, rawArgs, out var error );
		if ( error != null )
			return error;

		// Execute
		try
		{
			return command.OnRun?.Invoke( caller, context );
		}
		catch ( Exception ex )
		{
			Log.Error( $"Hexagon: Error executing command /{name}: {ex}" );
			return "An error occurred while running that command.";
		}
	}

	private static CommandContext ParseArguments( HexCommand command, string rawArgs, out string error )
	{
		error = null;
		var context = new CommandContext { RawInput = rawArgs };
		var args = command.Arguments;

		if ( args.Length == 0 )
			return context;

		var tokens = TokenizeArgs( rawArgs );
		var tokenIndex = 0;

		for ( var i = 0; i < args.Length; i++ )
		{
			var arg = args[i];

			// Remainder: join all remaining tokens
			if ( arg.IsRemainder )
			{
				if ( tokenIndex < tokens.Count )
				{
					context.Set( arg.Name, string.Join( " ", tokens.Skip( tokenIndex ) ) );
					tokenIndex = tokens.Count;
				}
				else if ( arg.IsOptional )
				{
					context.Set( arg.Name, arg.DefaultValue ?? "" );
				}
				else
				{
					error = GetUsageString( command );
					return null;
				}
				continue;
			}

			// No more tokens
			if ( tokenIndex >= tokens.Count )
			{
				if ( arg.IsOptional )
				{
					context.Set( arg.Name, arg.DefaultValue );
					continue;
				}

				error = GetUsageString( command );
				return null;
			}

			var token = tokens[tokenIndex];
			tokenIndex++;

			// Resolve typed argument
			if ( arg.ArgType == typeof( HexPlayerComponent ) )
			{
				var player = FindPlayer( token );
				if ( player == null )
				{
					error = $"Player '{token}' not found.";
					return null;
				}
				context.Set( arg.Name, player );
			}
			else if ( arg.ArgType == typeof( int ) )
			{
				if ( !int.TryParse( token, out var intVal ) )
				{
					error = $"'{token}' is not a valid number for argument '{arg.Name}'.";
					return null;
				}
				context.Set( arg.Name, intVal );
			}
			else if ( arg.ArgType == typeof( float ) )
			{
				if ( !float.TryParse( token, out var floatVal ) )
				{
					error = $"'{token}' is not a valid number for argument '{arg.Name}'.";
					return null;
				}
				context.Set( arg.Name, floatVal );
			}
			else
			{
				// String
				context.Set( arg.Name, token );
			}
		}

		return context;
	}

	private static List<string> TokenizeArgs( string input )
	{
		var tokens = new List<string>();
		if ( string.IsNullOrWhiteSpace( input ) )
			return tokens;

		var inQuotes = false;
		var current = new System.Text.StringBuilder();

		for ( var i = 0; i < input.Length; i++ )
		{
			var c = input[i];

			if ( c == '"' )
			{
				inQuotes = !inQuotes;
				continue;
			}

			if ( c == ' ' && !inQuotes )
			{
				if ( current.Length > 0 )
				{
					tokens.Add( current.ToString() );
					current.Clear();
				}
				continue;
			}

			current.Append( c );
		}

		if ( current.Length > 0 )
			tokens.Add( current.ToString() );

		return tokens;
	}

	private static HexPlayerComponent FindPlayer( string nameOrPartial )
	{
		var lower = nameOrPartial.ToLower();

		// Exact match first
		foreach ( var kvp in HexGameManager.Players )
		{
			if ( kvp.Value.DisplayName.ToLower() == lower )
				return kvp.Value;
		}

		// Partial match
		foreach ( var kvp in HexGameManager.Players )
		{
			if ( kvp.Value.DisplayName.ToLower().Contains( lower ) )
				return kvp.Value;
		}

		return null;
	}

	private static string GetUsageString( HexCommand command )
	{
		var argList = string.Join( " ", command.Arguments.Select( a =>
			a.IsOptional ? $"[{a.Name}]" : $"<{a.Name}>" ) );
		return $"Usage: /{command.Name} {argList}";
	}
}

/// <summary>
/// Permission hook: can a player run a command? Return false to block.
/// </summary>
public interface ICanRunCommandListener
{
	bool CanRunCommand( HexPlayerComponent player, HexCommand command );
}
