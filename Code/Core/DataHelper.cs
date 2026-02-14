namespace Hexagon.Core;

/// <summary>
/// Utility for typed access to Dictionary&lt;string, object&gt; stores
/// with safe type conversion and fallback defaults.
/// </summary>
public static class DataHelper
{
	/// <summary>
	/// Get a typed value from a dictionary with safe type conversion.
	/// Returns defaultValue if the key is missing, the dict is null, or conversion fails.
	/// </summary>
	public static T GetValue<T>( Dictionary<string, object> dict, string key, T defaultValue = default )
	{
		if ( dict == null || !dict.TryGetValue( key, out var value ) )
			return defaultValue;

		try
		{
			if ( value is T typed )
				return typed;

			return (T)Convert.ChangeType( value, typeof( T ) );
		}
		catch
		{
			return defaultValue;
		}
	}
}
