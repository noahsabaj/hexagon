namespace Hexagon.Commands;

/// <summary>
/// Contains parsed arguments for a command execution.
/// </summary>
public class CommandContext
{
	private readonly Dictionary<string, object> _args = new();

	/// <summary>
	/// The raw input string (everything after the command name).
	/// </summary>
	public string RawInput { get; set; }

	/// <summary>
	/// Get a parsed argument by name.
	/// </summary>
	public T Get<T>( string name, T defaultValue = default )
	{
		if ( !_args.TryGetValue( name, out var value ) )
			return defaultValue;

		if ( value is T typed )
			return typed;

		try
		{
			return (T)Convert.ChangeType( value, typeof( T ) );
		}
		catch
		{
			return defaultValue;
		}
	}

	/// <summary>
	/// Set a parsed argument value.
	/// </summary>
	public void Set( string name, object value )
	{
		_args[name] = value;
	}

	/// <summary>
	/// Check if an argument was provided.
	/// </summary>
	public bool Has( string name )
	{
		return _args.ContainsKey( name );
	}
}
