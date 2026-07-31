#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Hexagon.V2.Kernel.Definitions;

namespace Hexagon.V2.Kernel.Configuration;

public interface IConfigCodec<T>
{
	OperationResult<string> Encode(T value);
	OperationResult<T> Decode(string encoded);
}

public interface IConfigDefinition : IDefinition
{
	Type ValueType { get; }
	object? UntypedDefaultValue { get; }
	OperationResult ValidateDefault();
	OperationResult<string> EncodeObject(object? value);
	OperationResult<object?> DecodeObject(string encoded);
}

/// <summary>
/// A typed configuration contract. Values are never persisted as object-shaped JSON.
/// </summary>
public sealed class ConfigDefinition<T> : IConfigDefinition
{
	private readonly Func<T, OperationResult>? _validator;

	public ConfigDefinition(string id, T defaultValue, IConfigCodec<T> codec,
		Func<T, OperationResult>? validator = null)
	{
		Id = id;
		DefaultValue = defaultValue;
		Codec = codec ?? throw new ArgumentNullException(nameof(codec));
		_validator = validator;
	}

	public string Id { get; }
	public T DefaultValue { get; }
	public IConfigCodec<T> Codec { get; }
	public Type ValueType => typeof(T);
	public object? UntypedDefaultValue => DefaultValue;

	public OperationResult Validate(T value)
	{
		if (_validator is null)
			return OperationResult.Success();

		try
		{
			return _validator(value);
		}
		catch (Exception exception)
		{
			return OperationResult.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Validation for config '{Id}' threw {exception.GetType().Name}.");
		}
	}

	public OperationResult ValidateDefault()
	{
		var validation = Validate(DefaultValue);
		if (validation.Failed)
			return validation;

		var encoded = SafeEncode(DefaultValue);
		if (encoded.Failed)
			return OperationResult.Failure(encoded.Error!.Code, encoded.Error.Message, encoded.Error.Details);

		var decoded = Decode(encoded.Value);
		return decoded.Succeeded
			? OperationResult.Success()
			: OperationResult.Failure(decoded.Error!.Code, decoded.Error.Message, decoded.Error.Details);
	}

	public OperationResult<string> Encode(T value)
	{
		var validation = Validate(value);
		return validation.Succeeded
			? SafeEncode(value)
			: OperationResult<string>.Failure(validation.Error!.Code, validation.Error.Message,
				validation.Error.Details);
	}

	public OperationResult<T> Decode(string encoded)
	{
		var decoded = SafeDecode(encoded);
		if (decoded.Failed)
			return decoded;

		var validation = Validate(decoded.Value);
		return validation.Succeeded
			? decoded
			: OperationResult<T>.Failure(validation.Error!.Code, validation.Error.Message,
				validation.Error.Details);
	}

	public OperationResult<string> EncodeObject(object? value)
	{
		if (value is T typed)
			return Encode(typed);

		if (value is null && default(T) is null)
			return Encode((T)value!);

		if (value is not T)
		{
			return OperationResult<string>.Failure(
				ErrorCode.ConfigurationTypeMismatch,
				$"Config '{Id}' requires {typeof(T).FullName}, not {value?.GetType().FullName ?? "null"}.");
		}

		throw new InvalidOperationException("Unreachable configuration type branch.");
	}

	public OperationResult<object?> DecodeObject(string encoded)
	{
		var result = Decode(encoded);
		return result.Succeeded
			? OperationResult<object?>.Success(result.Value)
			: OperationResult<object?>.Failure(result.Error!.Code, result.Error.Message, result.Error.Details);
	}

	private OperationResult<string> SafeEncode(T value)
	{
		try
		{
			return Codec.Encode(value);
		}
		catch (Exception exception)
		{
			return OperationResult<string>.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Codec for config '{Id}' failed to encode: {exception.GetType().Name}.");
		}
	}

	private OperationResult<T> SafeDecode(string encoded)
	{
		try
		{
			return Codec.Decode(encoded);
		}
		catch (Exception exception)
		{
			return OperationResult<T>.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Codec for config '{Id}' failed to decode: {exception.GetType().Name}.");
		}
	}
}

public sealed class ConfigRegistry
{
	private readonly IReadOnlyDictionary<string, IConfigDefinition> _definitions;
	private readonly IReadOnlyList<IConfigDefinition> _all;

	internal ConfigRegistry(IDictionary<string, IConfigDefinition> definitions)
	{
		_definitions = new ReadOnlyDictionary<string, IConfigDefinition>(
			new Dictionary<string, IConfigDefinition>(definitions, StringComparer.Ordinal));
		_all = Array.AsReadOnly(_definitions.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray());
	}

	public int Count => _definitions.Count;
	public IEnumerable<IConfigDefinition> All => _all;

	public OperationResult<ConfigDefinition<T>> Require<T>(string id)
	{
		if (id is null || !_definitions.TryGetValue(id, out var definition))
		{
			return OperationResult<ConfigDefinition<T>>.Failure(
				ErrorCode.UnknownDefinition,
				$"Unknown configuration '{id}'.");
		}

		if (definition is not ConfigDefinition<T> typed)
		{
			return OperationResult<ConfigDefinition<T>>.Failure(
				ErrorCode.ConfigurationTypeMismatch,
				$"Configuration '{id}' is {definition.ValueType.FullName}, not {typeof(T).FullName}.");
		}

		return OperationResult<ConfigDefinition<T>>.Success(typed);
	}
}

public static class ConfigCodecs
{
	public static IConfigCodec<string> String { get; } = new DelegateCodec<string>(
		value => OperationResult<string>.Success(value),
		value => OperationResult<string>.Success(value));

	public static IConfigCodec<bool> Boolean { get; } = Parse(
		value => value ? "true" : "false",
		(string value, out bool parsed) => bool.TryParse(value, out parsed));

	public static IConfigCodec<int> Int32 { get; } = Parse(
		value => value.ToString(CultureInfo.InvariantCulture),
		(string value, out int parsed) => int.TryParse(value, NumberStyles.Integer,
			CultureInfo.InvariantCulture, out parsed));

	public static IConfigCodec<long> Int64 { get; } = Parse(
		value => value.ToString(CultureInfo.InvariantCulture),
		(string value, out long parsed) => long.TryParse(value, NumberStyles.Integer,
			CultureInfo.InvariantCulture, out parsed));

	public static IConfigCodec<decimal> Decimal { get; } = Parse(
		value => value.ToString(CultureInfo.InvariantCulture),
		(string value, out decimal parsed) => decimal.TryParse(value, NumberStyles.Number,
			CultureInfo.InvariantCulture, out parsed));

	public static IConfigCodec<Guid> Guid { get; } = Parse(
		value => value.ToString("D", CultureInfo.InvariantCulture),
		(string value, out Guid parsed) => System.Guid.TryParseExact(value, "D", out parsed));

	private delegate bool TryParse<T>(string text, out T value);

	private static IConfigCodec<T> Parse<T>(Func<T, string> format, TryParse<T> parse)
	{
		return new DelegateCodec<T>(
			value => OperationResult<string>.Success(format(value)),
			text => parse(text, out var value)
				? OperationResult<T>.Success(value)
				: OperationResult<T>.Failure(ErrorCode.ConfigurationInvalid,
					$"'{text}' is not a valid {typeof(T).Name}."));
	}

	private sealed class DelegateCodec<T> : IConfigCodec<T>
	{
		private readonly Func<T, OperationResult<string>> _encode;
		private readonly Func<string, OperationResult<T>> _decode;

		public DelegateCodec(Func<T, OperationResult<string>> encode,
			Func<string, OperationResult<T>> decode)
		{
			_encode = encode;
			_decode = decode;
		}

		public OperationResult<string> Encode(T value) => _encode(value);
		public OperationResult<T> Decode(string encoded) => _decode(encoded);
	}
}
