using System.Text.Json;

namespace Hexagon.Core;

/// <summary>
/// Utility for typed access to Dictionary&lt;string, object&gt; stores
/// with safe type conversion and fallback defaults.
/// Handles JsonElement values that arise from JSON round-trips (deserializing
/// Dictionary&lt;string, object&gt; produces JsonElement for each value).
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

			// After JSON round-trip, object values are JsonElement — deserialize them properly.
			if ( value is JsonElement elem )
				return elem.Deserialize<T>() ?? defaultValue;

			return (T)Convert.ChangeType( value, typeof( T ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Hexagon: DataHelper.GetValue failed to convert '{key}' to {typeof( T ).Name}: {ex.Message}" );
			return defaultValue;
		}
	}
}
