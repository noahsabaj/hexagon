#nullable enable

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Hexagon.V2.Persistence;

public interface IPersistedTypeCodec
{
	PersistedTypeKey Key { get; }
	Type ClrType { get; }
	int CurrentVersion { get; }
	JsonElement Serialize( object value );
	object Deserialize( JsonElement payload, int storedVersion );
	object PrepareForPublication( object value );
}

public interface IPersistedTypeCodec<T> : IPersistedTypeCodec where T : class
{
	JsonElement Serialize( T value );
	new T Deserialize( JsonElement payload, int storedVersion );
	T PrepareForPublication( T value );
}

/// <summary>
/// s&amp;box-safe helpers for explicit immutable publication. Every persisted codec
/// supplies a typed publication function; aggregate authors use these helpers to
/// copy collection contracts into wrappers that cannot be mutated by a downcast.
/// </summary>
public static class PersistedValuePublication
{
	public static T Immutable<T>( T value ) where T : class =>
		value ?? throw new ArgumentNullException( nameof(value) );

	public static IReadOnlyList<T> ReadOnlyList<T>( IEnumerable<T> values )
	{
		ArgumentNullException.ThrowIfNull( values );
		return new ReadOnlyCollection<T>( new List<T>( values ) );
	}

	public static IReadOnlyDictionary<TKey, TValue> ReadOnlyDictionary<TKey, TValue>(
		IEnumerable<KeyValuePair<TKey, TValue>> values,
		IEqualityComparer<TKey>? comparer = null ) where TKey : notnull
	{
		ArgumentNullException.ThrowIfNull( values );
		return new ReadOnlyDictionary<TKey, TValue>(
			new Dictionary<TKey, TValue>( values, comparer ?? EqualityComparer<TKey>.Default ) );
	}
}

/// <summary>
/// Explicit JSON codec for a concrete persistence type. Older payloads must have a complete,
/// contiguous migration chain to the current version.
/// </summary>
public sealed class JsonPersistedTypeCodec<T> : IPersistedTypeCodec<T> where T : class
{
	private readonly JsonSerializerOptions _options;
	private readonly IReadOnlyDictionary<int, Func<JsonElement, JsonElement>> _upgrades;
	private readonly Func<T, T> _prepareForPublication;

	public JsonPersistedTypeCodec(
		PersistedTypeKey key,
		int currentVersion,
		Func<T, T> prepareForPublication,
		JsonSerializerOptions? options = null,
		IReadOnlyDictionary<int, Func<JsonElement, JsonElement>>? upgrades = null )
	{
		if ( typeof(T).IsAbstract )
		{
			throw new ArgumentException( $"Persisted type '{typeof(T).FullName}' must be concrete." );
		}

		if ( currentVersion <= 0 )
		{
			throw new ArgumentOutOfRangeException( nameof(currentVersion), "Persisted type versions start at one." );
		}

		Key = key;
		CurrentVersion = currentVersion;
		_prepareForPublication = prepareForPublication
			?? throw new ArgumentNullException( nameof(prepareForPublication),
				"Persisted codecs require an explicit typed immutable-publication function." );
		_options = options is null ? CreateDefaultOptions() : new JsonSerializerOptions( options );
		_upgrades = upgrades is null
			? new Dictionary<int, Func<JsonElement, JsonElement>>()
			: new Dictionary<int, Func<JsonElement, JsonElement>>( upgrades );

		foreach ( var version in _upgrades.Keys )
		{
			if ( version <= 0 || version >= currentVersion )
			{
				throw new ArgumentOutOfRangeException(
					nameof(upgrades),
					$"Migration source version {version} must be between one and {currentVersion - 1}." );
			}
		}
	}

	public PersistedTypeKey Key { get; }
	public Type ClrType => typeof(T);
	public int CurrentVersion { get; }

	public JsonElement Serialize( T value )
	{
		ArgumentNullException.ThrowIfNull( value );
		return JsonSerializer.SerializeToElement( value, _options );
	}

	public T Deserialize( JsonElement payload, int storedVersion )
	{
		if ( storedVersion <= 0 || storedVersion > CurrentVersion )
		{
			throw new InvalidOperationException(
				$"Cannot read '{Key}' version {storedVersion}; registered version is {CurrentVersion}." );
		}

		var migratedPayload = payload.Clone();
		for ( var version = storedVersion; version < CurrentVersion; version++ )
		{
			if ( !_upgrades.TryGetValue( version, out var upgrade ) )
			{
				throw new InvalidOperationException(
					$"Persisted type '{Key}' has no migration from version {version} to {version + 1}." );
			}

			migratedPayload = upgrade( migratedPayload ).Clone();
		}

		return JsonSerializer.Deserialize<T>( migratedPayload, _options )
			?? throw new JsonException( $"Codec '{Key}' deserialized a null {typeof(T).FullName}." );
	}

	public T PrepareForPublication( T value )
	{
		ArgumentNullException.ThrowIfNull( value );
		return _prepareForPublication( value )
			?? throw new InvalidOperationException( $"Codec '{Key}' produced a null publication value." );
	}

	JsonElement IPersistedTypeCodec.Serialize( object value ) =>
		value is T typed
			? Serialize( typed )
			: throw new ArgumentException( $"Codec '{Key}' cannot serialize {value.GetType().FullName}." );

	object IPersistedTypeCodec.Deserialize( JsonElement payload, int storedVersion ) =>
		Deserialize( payload, storedVersion );

	object IPersistedTypeCodec.PrepareForPublication( object value ) =>
		value is T typed
			? PrepareForPublication( typed )
			: throw new ArgumentException( $"Codec '{Key}' cannot publish {value.GetType().FullName}." );

	private static JsonSerializerOptions CreateDefaultOptions() => new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		WriteIndented = false
	};
}

/// <summary>
/// Explicit registry for every concrete type that may enter persistent state.
/// It is frozen when a provider initializes so recovery cannot depend on runtime registration order.
/// </summary>
public sealed class PersistedTypeRegistry
{
	private readonly Dictionary<PersistedTypeKey, IPersistedTypeCodec> _byKey = new();
	private readonly Dictionary<Type, IPersistedTypeCodec> _byType = new();
	private bool _frozen;

	public bool IsFrozen => _frozen;
	public IReadOnlyCollection<IPersistedTypeCodec> Codecs => _byKey.Values.ToArray();

	public PersistedTypeRegistry Register<T>(
		PersistedTypeKey key,
		int currentVersion,
		Func<T, T> prepareForPublication,
		JsonSerializerOptions? options = null,
		IReadOnlyDictionary<int, Func<JsonElement, JsonElement>>? upgrades = null ) where T : class =>
		Register( new JsonPersistedTypeCodec<T>( key, currentVersion, prepareForPublication, options, upgrades ) );

	/// <summary>
	/// Fails closed for registrations that omit their typed immutable-publication strategy.
	/// This overload remains only to produce a startup diagnostic for stale schema code.
	/// </summary>
	public PersistedTypeRegistry Register<T>(
		PersistedTypeKey key,
		int currentVersion,
		JsonSerializerOptions? options = null,
		IReadOnlyDictionary<int, Func<JsonElement, JsonElement>>? upgrades = null ) where T : class =>
		throw new ArgumentException(
			$"Persisted type '{typeof(T).FullName}' must register an explicit typed immutable-publication function.",
			nameof(T) );

	public PersistedTypeRegistry Register<T>( IPersistedTypeCodec<T> codec ) where T : class
	{
		ArgumentNullException.ThrowIfNull( codec );

		if ( codec.ClrType != typeof(T) )
		{
			throw new ArgumentException(
				$"Codec CLR type '{codec.ClrType.FullName}' does not match registration type '{typeof(T).FullName}'.",
				nameof(codec) );
		}

		return Register( (IPersistedTypeCodec)codec );
	}

	/// <summary>
	/// Registers a codec supplied by schema composition without reflection. Generic repositories still
	/// require the codec to implement the matching <see cref="IPersistedTypeCodec{T}"/> interface.
	/// </summary>
	public PersistedTypeRegistry Register( IPersistedTypeCodec codec )
	{
		ArgumentNullException.ThrowIfNull( codec );
		EnsureMutable();
		_ = new PersistedTypeKey( codec.Key.Value );

		if ( codec.ClrType.IsAbstract )
		{
			throw new ArgumentException( $"Persisted type '{codec.ClrType.FullName}' must be concrete.", nameof(codec) );
		}

		if ( codec.CurrentVersion <= 0 )
		{
			throw new ArgumentException( "Persisted type versions start at one.", nameof(codec) );
		}

		if ( _byKey.ContainsKey( codec.Key ) )
		{
			throw new InvalidOperationException( $"Persisted type key '{codec.Key}' is already registered." );
		}

		if ( _byType.ContainsKey( codec.ClrType ) )
		{
			throw new InvalidOperationException( $"CLR type '{codec.ClrType.FullName}' is already registered." );
		}

		_byKey.Add( codec.Key, codec );
		_byType.Add( codec.ClrType, codec );
		return this;
	}

	public IPersistedTypeCodec Resolve( PersistedTypeKey key )
	{
		_ = new PersistedTypeKey( key.Value );
		return _byKey.TryGetValue( key, out var codec )
			? codec
			: throw new KeyNotFoundException( $"Persisted type key '{key}' is not registered." );
	}

	public IPersistedTypeCodec<T> Resolve<T>() where T : class =>
		_byType.TryGetValue( typeof(T), out var codec )
			? (IPersistedTypeCodec<T>)codec
			: throw new KeyNotFoundException( $"CLR type '{typeof(T).FullName}' is not registered for persistence." );

	internal void Freeze() => _frozen = true;

	private void EnsureMutable()
	{
		if ( _frozen )
		{
			throw new InvalidOperationException( "Persisted type registration is frozen after provider initialization." );
		}
	}
}
