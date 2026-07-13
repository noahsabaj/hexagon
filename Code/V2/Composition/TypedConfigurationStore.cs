#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Composition;

public sealed record TypedConfigurationValue<T>(
	string Key,
	DocumentRevision Revision,
	T Value );

public sealed record PersistedConfigurationSnapshot(
	string Key,
	string ValueTypeId,
	DocumentRevision Revision,
	string CanonicalEncodedValue );

public interface ITypedConfigurationStore
{
	OperationResult<TypedConfigurationValue<T>> Get<T>( string key );
	IReadOnlyList<PersistedConfigurationSnapshot> Snapshot();
	ValueTask<OperationResult<TypedConfigurationValue<T>>> SetAsync<T>(
		string key,
		T value,
		DocumentRevision expectedRevision,
		CancellationToken cancellationToken = default );
}

/// <summary>
/// Durable typed schema configuration. Startup validates every existing value,
/// rejects unknown documents, and inserts all missing defaults in one commit.
/// Consumers never receive an object dictionary or bypass the registered codec.
/// </summary>
public sealed class TypedConfigurationStore : ITypedConfigurationStore
{
	private readonly IPersistenceProvider _provider;
	private readonly IReadOnlyDictionary<string, IConfigDefinition> _definitions;
	private readonly IPersistenceRepository<PersistedConfigRecord> _repository;
	private bool _initialized;

	public TypedConfigurationStore(
		IPersistenceProvider provider,
		IReadOnlyDictionary<string, IConfigDefinition> definitions )
	{
		_provider = provider ?? throw new ArgumentNullException( nameof(provider) );
		if ( !provider.IsInitialized )
			throw new InvalidOperationException( "Persistence must be initialized before typed configuration is composed." );
		ArgumentNullException.ThrowIfNull( definitions );
		_definitions = new ReadOnlyDictionary<string, IConfigDefinition>(
			new Dictionary<string, IConfigDefinition>( definitions, StringComparer.Ordinal ) );
		_repository = provider.Repository<PersistedConfigRecord>( DomainCollections.Configuration );
	}

	public bool IsInitialized => _initialized;

	public IReadOnlyList<PersistedConfigurationSnapshot> Snapshot()
	{
		if ( !_initialized ) throw new InvalidOperationException( "Typed configuration is not initialized." );
		return Array.AsReadOnly( _repository.All()
			.OrderBy( document => document.Key, StringComparer.Ordinal )
			.Select( document => new PersistedConfigurationSnapshot(
				document.Key,
				document.Value.ValueTypeId,
				document.Revision,
				CanonicalEncodedValue( document ) ) )
			.ToArray() );
	}

	private string CanonicalEncodedValue( DocumentSnapshot<PersistedConfigRecord> document )
	{
		var definition = _definitions[document.Key];
		var decoded = definition.DeserializeObject( document.Value.EncodedValue );
		return definition.SerializeObject( decoded ).GetRawText();
	}

	public async ValueTask<OperationResult> InitializeAsync(
		CancellationToken cancellationToken = default )
	{
		if ( _initialized )
			return OperationResult.Failure( ErrorCode.Conflict, "Typed configuration is already initialized." );

		var validation = ValidateCommittedDocuments();
		if ( validation.Failed ) return validation;

		var missing = _definitions.Values
			.Where( definition => _repository.Find( definition.Key ) is null )
			.OrderBy( definition => definition.Key, StringComparer.Ordinal )
			.ToArray();
		if ( missing.Length > 0 )
		{
			var defaults = new List<(IConfigDefinition Definition, JsonElement Encoded)>();
			foreach ( var definition in missing )
			{
				var encoded = Encode( definition, definition.DefaultObject );
				if ( encoded.Failed ) return Failure( encoded.Error! );
				defaults.Add( (definition, encoded.Value) );
			}
			var unit = _provider.BeginUnitOfWork();
			foreach ( var entry in defaults )
			{
				unit.Create(
					_repository,
					entry.Definition.Key,
					new PersistedConfigRecord
				{
						Key = entry.Definition.Key,
						ValueTypeId = entry.Definition.ValueTypeId,
						EncodedValue = entry.Encoded
					} );
			}
			var committed = await CommitAndDisposeAsync( unit, cancellationToken );
			if ( !committed.Succeeded ) return PersistenceFailure( committed.Error! );
		}

		validation = ValidateCommittedDocuments();
		if ( validation.Failed ) return validation;
		_initialized = true;
		return OperationResult.Success();
	}

	public OperationResult<TypedConfigurationValue<T>> Get<T>( string key )
	{
		if ( !_initialized )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.Conflict, "Typed configuration is not initialized." );
		var definition = RequireDefinition<T>( key );
		if ( definition.Failed ) return OperationResult<TypedConfigurationValue<T>>.Failure(
			definition.Error!.Code, definition.Error.Message );
		var document = _repository.Find( key );
		if ( document is null )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.NotFound, $"Configuration '{key}' has no persisted value." );
		return Decode<T>( definition.Value, document );
	}

	public async ValueTask<OperationResult<TypedConfigurationValue<T>>> SetAsync<T>(
		string key,
		T value,
		DocumentRevision expectedRevision,
		CancellationToken cancellationToken = default )
	{
		if ( !_initialized )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.Conflict, "Typed configuration is not initialized." );
		var definition = RequireDefinition<T>( key );
		if ( definition.Failed ) return OperationResult<TypedConfigurationValue<T>>.Failure(
			definition.Error!.Code, definition.Error.Message );
		var observed = _repository.Find( key );
		if ( observed is null )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.NotFound, $"Configuration '{key}' has no persisted value." );
		if ( observed.Revision != expectedRevision )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.Conflict,
				$"Configuration '{key}' revision changed; expected {expectedRevision}, current {observed.Revision}." );

		var encoded = Encode( definition.Value, value );
		if ( encoded.Failed ) return OperationResult<TypedConfigurationValue<T>>.Failure(
			encoded.Error!.Code, encoded.Error.Message );
		var unit = _provider.BeginUnitOfWork();
		var editor = unit.Edit( _repository, observed );
		if ( editor is null )
		{
			await unit.DisposeAsync();
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.Conflict, $"Configuration '{key}' changed before it could be edited." );
		}
		editor.Replace( observed.Value with
		{
			EncodedValue = encoded.Value
		} );
		unit.Save( editor );
		var committed = await CommitAndDisposeAsync( unit, cancellationToken );
		if ( !committed.Succeeded )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				Map( committed.Error!.Code ), committed.Error.Message );

		var published = _repository.Find( key );
		if ( published is null )
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.InternalError, $"Configuration '{key}' disappeared after commit." );
		return Decode<T>( definition.Value, published );
	}

	private OperationResult ValidateCommittedDocuments()
	{
		foreach ( var document in _repository.All() )
		{
			if ( !_definitions.TryGetValue( document.Key, out var definition ) )
				return OperationResult.Failure(
					ErrorCode.UnknownDefinition,
					$"Persisted configuration '{document.Key}' is not registered by the active schema." );
			if ( !string.Equals( document.Value.Key, document.Key, StringComparison.Ordinal ) )
				return OperationResult.Failure(
					ErrorCode.ConfigurationInvalid,
					$"Configuration document '{document.Key}' contains key '{document.Value.Key}'." );
			if ( !string.Equals( document.Value.ValueTypeId, definition.ValueTypeId, StringComparison.Ordinal ) )
				return OperationResult.Failure(
					ErrorCode.ConfigurationTypeMismatch,
					$"Configuration '{document.Key}' is persisted as '{document.Value.ValueTypeId}', not '{definition.ValueTypeId}'." );
			try
			{
				var decoded = definition.DeserializeObject( document.Value.EncodedValue );
				_ = definition.SerializeObject( decoded );
			}
			catch ( Exception exception ) when (
				exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException )
			{
				return OperationResult.Failure(
					ErrorCode.ConfigurationInvalid,
					$"Configuration '{document.Key}' could not be decoded by its registered codec." );
			}
		}
		return OperationResult.Success();
	}

	private OperationResult<IConfigDefinition> RequireDefinition<T>( string key )
	{
		if ( key is null || !_definitions.TryGetValue( key, out var definition ) )
			return OperationResult<IConfigDefinition>.Failure(
				ErrorCode.UnknownDefinition, $"Unknown configuration '{key ?? "<null>"}'." );
		if ( definition.ValueType != typeof(T) )
			return OperationResult<IConfigDefinition>.Failure(
				ErrorCode.ConfigurationTypeMismatch,
				$"Configuration '{key}' is {definition.ValueType.FullName}, not {typeof(T).FullName}." );
		return OperationResult<IConfigDefinition>.Success( definition );
	}

	private static OperationResult<TypedConfigurationValue<T>> Decode<T>(
		IConfigDefinition definition,
		DocumentSnapshot<PersistedConfigRecord> document )
	{
		try
		{
			var decoded = definition.DeserializeObject( document.Value.EncodedValue );
			if ( decoded is not T typed )
				return OperationResult<TypedConfigurationValue<T>>.Failure(
					ErrorCode.ConfigurationTypeMismatch,
					$"Configuration '{document.Key}' codec returned an incompatible value." );
			return OperationResult<TypedConfigurationValue<T>>.Success(
				new TypedConfigurationValue<T>( document.Key, document.Revision, typed ) );
		}
		catch ( Exception exception ) when (
			exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException )
		{
			return OperationResult<TypedConfigurationValue<T>>.Failure(
				ErrorCode.ConfigurationInvalid,
				$"Configuration '{document.Key}' could not be decoded by its registered codec." );
		}
	}

	private static OperationResult<JsonElement> Encode( IConfigDefinition definition, object? value )
	{
		try
		{
			return OperationResult<JsonElement>.Success( definition.SerializeObject( value ).Clone() );
		}
		catch ( Exception exception ) when (
			exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException )
		{
			return OperationResult<JsonElement>.Failure(
				exception is ArgumentException ? ErrorCode.ConfigurationTypeMismatch : ErrorCode.ConfigurationInvalid,
				$"Configuration '{definition.Key}' could not be encoded by its registered codec." );
		}
	}

	private static OperationResult Failure( OperationError error ) =>
		OperationResult.Failure( error.Code, error.Message, error.Details );

	private static OperationResult PersistenceFailure( PersistenceError error ) =>
		OperationResult.Failure( Map( error.Code ), error.Message );

	private static async ValueTask<PersistenceResult<CommitReceipt>> CommitAndDisposeAsync(
		IUnitOfWork unit,
		CancellationToken cancellationToken )
	{
		var committed = await unit.CommitAsync( cancellationToken );
		await unit.DisposeAsync();
		return committed;
	}

	private static ErrorCode Map( PersistenceErrorCode code ) => code switch
	{
		PersistenceErrorCode.NotFound => ErrorCode.NotFound,
		PersistenceErrorCode.AlreadyExists or PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
		PersistenceErrorCode.TypeNotRegistered or PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
		_ => ErrorCode.InternalError
	};
}
