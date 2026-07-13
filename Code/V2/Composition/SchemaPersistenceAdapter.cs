#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Configuration;
using Hexagon.V2.Kernel.Persistence;
using Hexagon.V2.Kernel.Schema;
using Hexagon.V2.Persistence;
using KernelConfigDefinition = Hexagon.V2.Kernel.Configuration.IConfigDefinition;
using ProviderConfigDefinition = Hexagon.V2.Persistence.IConfigDefinition;

namespace Hexagon.V2.Composition;

/// <summary>
/// Validated inputs for constructing a persistence provider. Configuration keeps
/// the compiled kernel definitions as its single typed codec contract.
/// </summary>
public sealed record SchemaPersistenceBindings(
	PersistedTypeRegistry Types,
	ConfigRegistry Configs,
	IReadOnlyDictionary<string, ProviderConfigDefinition> PersistenceConfigs
);

/// <summary>
/// Binds schema metadata to concrete infrastructure codecs without reflection.
/// All registrations are checked before the target registry is changed.
/// </summary>
public static class SchemaPersistenceAdapter
{
	public static OperationResult<SchemaPersistenceBindings> Bind(
		CompiledSchema? schema,
		PersistedTypeRegistry? targetRegistry,
		IEnumerable<IPersistedTypeCodec>? schemaCodecs)
	{
		if (schema is null)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.InvalidArgument,
				"A compiled schema is required for persistence composition.");
		}

		if (targetRegistry is null)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.InvalidArgument,
				"A persistence type registry is required for composition.");
		}

		if (schemaCodecs is null)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.InvalidArgument,
				"The schema codec collection cannot be null.");
		}

		if (targetRegistry.IsFrozen)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.Conflict,
				"Persistence registrations are frozen because the provider is already initialized.");
		}

		IPersistedTypeCodec[] codecs;
		try
		{
			codecs = schemaCodecs.ToArray();
		}
		catch (Exception exception)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.PersistedTypeInvalid,
				$"Enumerating schema persistence codecs failed: {exception.GetType().Name}.");
		}

		var metadataById = schema.PersistedTypes.All.ToDictionary(x => x.Id, StringComparer.Ordinal);
		var codecById = new Dictionary<string, IPersistedTypeCodec>(StringComparer.Ordinal);
		var codecTypes = new HashSet<Type>();

		foreach (var codec in codecs)
		{
			if (codec is null)
			{
				return Failure("A schema persistence codec cannot be null.");
			}

			var id = codec.Key.Value;
			if (!codecById.TryAdd(id, codec))
			{
				return Failure($"Schema persistence codec ID '{id}' is duplicated.");
			}

			if (!codecTypes.Add(codec.ClrType))
			{
				return Failure($"Schema persistence CLR type '{codec.ClrType.FullName}' is duplicated.");
			}

			if (!metadataById.ContainsKey(id))
			{
				return OperationResult<SchemaPersistenceBindings>.Failure(
					ErrorCode.UnknownDefinition,
					$"Codec '{id}' has no compiled schema persisted-type registration.");
			}
		}

		foreach (var metadata in metadataById.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
		{
			if (!codecById.TryGetValue(metadata.Id, out var codec))
			{
				return OperationResult<SchemaPersistenceBindings>.Failure(
					ErrorCode.UnknownDefinition,
					$"Persisted type '{metadata.Id}' has no concrete codec.");
			}

			if (codec.ClrType != metadata.ClrType)
			{
				return Failure(
					$"Codec '{metadata.Id}' targets {codec.ClrType.FullName}, but the schema registered {metadata.ClrType.FullName}.");
			}

			if (codec.CurrentVersion != metadata.Version)
			{
				return Failure(
					$"Codec '{metadata.Id}' version {codec.CurrentVersion} does not match schema version {metadata.Version}.");
			}
		}

		var existingCodecs = targetRegistry.Codecs;
		foreach (var codec in codecs)
		{
			if (existingCodecs.Any(x => x.Key == codec.Key))
				return Failure($"Persistence type ID '{codec.Key}' is already registered.");

			if (existingCodecs.Any(x => x.ClrType == codec.ClrType))
				return Failure($"Persistence CLR type '{codec.ClrType.FullName}' is already registered.");
		}

		try
		{
			foreach (var codec in codecs.OrderBy(x => x.Key.Value, StringComparer.Ordinal))
				targetRegistry.Register(codec);
		}
		catch (Exception exception)
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.PersistedTypeInvalid,
				$"Registering schema persistence codecs failed: {exception.GetType().Name}.");
		}

		Dictionary<string, ProviderConfigDefinition> persistenceConfigs;
		try
		{
			persistenceConfigs = schema.Configs.All.ToDictionary(
				x => x.Id,
				x => (ProviderConfigDefinition)new KernelConfigPersistenceDefinition(x),
				StringComparer.Ordinal);
			foreach ( var definition in persistenceConfigs.Values ) _ = definition.ValueTypeId;
		}
		catch ( ArgumentException exception )
		{
			return OperationResult<SchemaPersistenceBindings>.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Schema configuration cannot be persisted: {exception.Message}" );
		}

		return OperationResult<SchemaPersistenceBindings>.Success(
			new SchemaPersistenceBindings(
				targetRegistry,
				schema.Configs,
				new ReadOnlyDictionary<string, ProviderConfigDefinition>(persistenceConfigs)));
	}

	private static OperationResult<SchemaPersistenceBindings> Failure(string message)
		=> OperationResult<SchemaPersistenceBindings>.Failure(ErrorCode.PersistedTypeInvalid, message);

	private sealed class KernelConfigPersistenceDefinition : ProviderConfigDefinition
	{
		private readonly KernelConfigDefinition _definition;

		public KernelConfigPersistenceDefinition(KernelConfigDefinition definition)
		{
			_definition = definition;
		}

		public string Key => _definition.Id;
		public Type ValueType => _definition.ValueType;
		public string ValueTypeId => ConfigValueTypeIdentity.For( _definition.ValueType );
		public object? DefaultObject => _definition.UntypedDefaultValue;

		public JsonElement SerializeObject(object? value)
		{
			var result = _definition.EncodeObject(value);
			if (result.Failed)
				throw new ArgumentException(result.Error!.Message, nameof(value));

			return JsonSerializer.SerializeToElement(result.Value);
		}

		public object? DeserializeObject(JsonElement payload)
		{
			if (payload.ValueKind != JsonValueKind.String)
				throw new JsonException($"Config '{Key}' requires an encoded string payload.");

			var result = _definition.DecodeObject(payload.GetString()!);
			if (result.Failed)
				throw new JsonException(result.Error!.Message);

			return result.Value;
		}
	}
}
