namespace Hexagon.Commands;

/// <summary>
/// Defines a chat command. Register with CommandManager.Register().
/// </summary>
public class HexCommand
{
	/// <summary>
	/// Primary command name (without prefix). E.g. "givemoney".
	/// </summary>
	public string Name { get; set; }

	/// <summary>
	/// Description shown in help text.
	/// </summary>
	public string Description { get; set; }

	/// <summary>
	/// Alternative names for this command.
	/// </summary>
	public string[] Aliases { get; set; } = Array.Empty<string>();

	/// <summary>
	/// Required permission flag string (e.g. "a" for admin, "s" for super admin).
	/// Empty = no permission required.
	/// </summary>
	public string Permission { get; set; } = "";

	/// <summary>
	/// Custom permission check function. If set, called in addition to flag check.
	/// </summary>
	public Func<HexPlayerComponent, bool> PermissionFunc { get; set; }

	/// <summary>
	/// Argument definitions for this command.
	/// </summary>
	public CommandArg[] Arguments { get; set; } = Array.Empty<CommandArg>();

	/// <summary>
	/// Handler called when the command is executed.
	/// Parameters: (caller, context). Return a string message to send back to the caller.
	/// </summary>
	public Func<HexPlayerComponent, CommandContext, string> OnRun { get; set; }
}
