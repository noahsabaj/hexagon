#nullable enable

using System;
using System.Text.Json;

namespace Hexagon.V2.Persistence;

public interface IConfigDefinition
{
	string Key { get; }
	Type ValueType { get; }
	string ValueTypeId { get; }
	object? DefaultObject { get; }
	JsonElement SerializeObject( object? value );
	object? DeserializeObject( JsonElement payload );
}

public interface IConfigValueCodec<T>
{
	JsonElement Serialize( T value );
	T Deserialize( JsonElement payload );
}

public sealed class JsonConfigValueCodec<T> : IConfigValueCodec<T>
{
	private readonly JsonSerializerOptions _options;

	public JsonConfigValueCodec( JsonSerializerOptions? options = null )
	{
		_options = options is null
			? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
			: new JsonSerializerOptions( options );
	}

	public JsonElement Serialize( T value ) => JsonSerializer.SerializeToElement( value, _options );

	public T Deserialize( JsonElement payload ) =>
		JsonSerializer.Deserialize<T>( payload, _options )
		?? throw new JsonException( $"Config codec deserialized a null {typeof(T).FullName}." );
}

/// <summary>
/// Typed configuration contract. Persisted configuration is encoded through this definition;
/// object dictionaries and runtime conversion are intentionally unsupported.
/// </summary>
public sealed class ConfigDefinition<T> : IConfigDefinition
{
	private readonly Func<T, bool> _validator;

	public ConfigDefinition(
		string key,
		T defaultValue,
		IConfigValueCodec<T>? codec = null,
		Func<T, bool>? validator = null )
	{
		Key = PersistenceName.Validate( key, nameof(key) );
		DefaultValue = defaultValue;
		Codec = codec ?? new JsonConfigValueCodec<T>();
		_validator = validator ?? (_ => true);

		if ( !_validator( defaultValue ) )
		{
			throw new ArgumentException( $"Default value for config '{Key}' is invalid.", nameof(defaultValue) );
		}
	}

	public string Key { get; }
	public T DefaultValue { get; }
	public IConfigValueCodec<T> Codec { get; }
	public Type ValueType => typeof(T);
	public string ValueTypeId => ConfigValueTypeIdentity.For( typeof(T) );
	public object? DefaultObject => DefaultValue;

	public JsonElement Serialize( T value )
	{
		if ( !_validator( value ) )
		{
			throw new ArgumentException( $"Value for config '{Key}' is invalid.", nameof(value) );
		}

		return Codec.Serialize( value );
	}

	public T Deserialize( JsonElement payload )
	{
		var value = Codec.Deserialize( payload );
		if ( !_validator( value ) )
		{
			throw new JsonException( $"Persisted value for config '{Key}' is invalid." );
		}

		return value;
	}

	JsonElement IConfigDefinition.SerializeObject( object? value )
	{
		if ( value is not T typed )
		{
			throw new ArgumentException( $"Config '{Key}' requires a value of type {typeof(T).FullName}.", nameof(value) );
		}

		return Serialize( typed );
	}

	object? IConfigDefinition.DeserializeObject( JsonElement payload ) => Deserialize( payload );
}

/// <summary>
/// Immutable framework document for one typed configuration value. The encoded
/// JSON belongs to the registered definition's codec; the stable value-type ID
/// prevents a definition from silently changing CLR types between restarts.
/// </summary>
internal sealed record PersistedConfigRecord
{
	public required string Key { get; init; }
	public required string ValueTypeId { get; init; }
	public required JsonElement EncodedValue { get; init; }

	public PersistedConfigRecord DeepCopy() => this with { EncodedValue = EncodedValue.Clone() };
}

public static class ConfigValueTypeIdentity
{
	public static string For( Type type )
	{
		ArgumentNullException.ThrowIfNull( type );
		if ( type == typeof(string) ) return "string";
		if ( type == typeof(bool) ) return "boolean";
		if ( type == typeof(int) ) return "int32";
		if ( type == typeof(long) ) return "int64";
		if ( type == typeof(decimal) ) return "decimal";
		if ( type == typeof(Guid) ) return "guid";
		throw new ArgumentException(
			$"Configuration value type '{type.FullName}' has no stable persistence identity.",
			nameof(type) );
	}
}
