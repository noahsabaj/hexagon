namespace Hexagon.Commands;

/// <summary>
/// Defines a single argument for a command.
/// </summary>
public class CommandArg
{
	/// <summary>
	/// Display name of the argument (e.g. "player", "amount").
	/// </summary>
	public string Name { get; set; }

	/// <summary>
	/// Expected type: typeof(HexPlayerComponent), typeof(string), typeof(int), typeof(float).
	/// </summary>
	public Type ArgType { get; set; }

	/// <summary>
	/// Whether this argument can be omitted.
	/// </summary>
	public bool IsOptional { get; set; }

	/// <summary>
	/// If true, this argument captures all remaining tokens as a single string.
	/// Must be the last argument.
	/// </summary>
	public bool IsRemainder { get; set; }

	/// <summary>
	/// Default value if the argument is optional and not provided.
	/// </summary>
	public object DefaultValue { get; set; }
}

/// <summary>
/// Factory methods for creating command arguments.
/// </summary>
public static class Arg
{
	public static CommandArg Player( string name = "player" )
	{
		return new CommandArg { Name = name, ArgType = typeof( HexPlayerComponent ) };
	}

	public static CommandArg String( string name = "text", bool remainder = false )
	{
		return new CommandArg { Name = name, ArgType = typeof( string ), IsRemainder = remainder };
	}

	public static CommandArg Number( string name = "number" )
	{
		return new CommandArg { Name = name, ArgType = typeof( float ) };
	}

	public static CommandArg Int( string name = "amount" )
	{
		return new CommandArg { Name = name, ArgType = typeof( int ) };
	}

	/// <summary>
	/// Make an existing argument optional with a default value.
	/// </summary>
	public static CommandArg Optional( CommandArg arg, object defaultValue = null )
	{
		arg.IsOptional = true;
		arg.DefaultValue = defaultValue;
		return arg;
	}
}
