#nullable enable

using System;

namespace Hexagon.V2.Persistence;

/// <summary>
/// Documents the stable persistence identity of a concrete payload type.
/// This marker never triggers discovery: schemas must still register a matching
/// typed codec and immutable-publication function explicitly.
/// </summary>
[AttributeUsage( AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false )]
public sealed class PersistedTypeAttribute : Attribute
{
	public PersistedTypeAttribute( string id, int version )
	{
		Id = new PersistedTypeKey( id ).Value;
		if ( version <= 0 )
			throw new ArgumentOutOfRangeException( nameof(version), "Persisted type versions start at one." );
		Version = version;
	}

	public string Id { get; }
	public int Version { get; }
}
