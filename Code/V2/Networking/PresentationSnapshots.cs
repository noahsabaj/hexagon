#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

public sealed record PlayerRosterRowSnapshot
{
	public PlayerRosterRowSnapshot(
		ConnectionId connectionId,
		CharacterId? characterId,
		IReadOnlyDictionary<string, SnapshotValue>? fields = null )
	{
		ConnectionId = connectionId;
		CharacterId = characterId;
		Fields = PresentationSnapshotMap.Copy( fields );
	}

	public ConnectionId ConnectionId { get; }
	public CharacterId? CharacterId { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Fields { get; }
}

/// <summary>
/// Public scoreboard/recognition projection. Rows contain only stable public IDs
/// and the closed SnapshotValue scalar union.
/// </summary>
public sealed record PlayerRosterSnapshot
{
	public PlayerRosterSnapshot( long revision, IEnumerable<PlayerRosterRowSnapshot>? rows = null )
	{
		if ( revision < 0 ) throw new ArgumentOutOfRangeException( nameof(revision) );
		var copy = (rows ?? Array.Empty<PlayerRosterRowSnapshot>())
			.OrderBy( row => row.ConnectionId.Value )
			.ToArray();
		if ( copy.Select( row => row.ConnectionId ).Distinct().Count() != copy.Length )
			throw new ArgumentException( "Player roster rows must have unique connection IDs.", nameof(rows) );
		Revision = revision;
		Rows = Array.AsReadOnly( copy );
	}

	private PlayerRosterSnapshot( long revision, IReadOnlyList<PlayerRosterRowSnapshot> rows )
	{
		if ( revision < 0 ) throw new ArgumentOutOfRangeException( nameof(revision) );
		Revision = revision;
		Rows = rows;
	}

	public long Revision { get; }
	public IReadOnlyList<PlayerRosterRowSnapshot> Rows { get; }
	public static PlayerRosterSnapshot Empty { get; } = new( 0 );

	/// <summary>
	/// Re-stamps an immutable roster without copying or re-sorting its unchanged rows.
	/// </summary>
	public PlayerRosterSnapshot WithRevision( long revision ) =>
		revision == Revision ? this : new PlayerRosterSnapshot( revision, Rows );
}

/// <summary>
/// Generic immutable projection for one schema-registered panel. Panel
/// registration is validated by the host application against CompiledSchema.
/// </summary>
public sealed record SchemaViewSnapshot
{
	public SchemaViewSnapshot(
		string panelId,
		long revision,
		IReadOnlyDictionary<string, SnapshotValue>? fields = null,
		IEnumerable<IReadOnlyDictionary<string, SnapshotValue>>? rows = null )
	{
		if ( revision < 0 ) throw new ArgumentOutOfRangeException( nameof(revision) );
		PanelId = StableIdentifier.Require( panelId, nameof(panelId) );
		Revision = revision;
		Fields = PresentationSnapshotMap.Copy( fields );
		Rows = Array.AsReadOnly( (rows ?? Array.Empty<IReadOnlyDictionary<string, SnapshotValue>>())
			.Select( PresentationSnapshotMap.Copy )
			.ToArray() );
	}

	public string PanelId { get; }
	public long Revision { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Fields { get; }
	public IReadOnlyList<IReadOnlyDictionary<string, SnapshotValue>> Rows { get; }
}

internal static class PresentationSnapshotMap
{
	public static IReadOnlyDictionary<string, SnapshotValue> Copy(
		IReadOnlyDictionary<string, SnapshotValue>? values )
	{
		var copy = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		foreach ( var pair in (values ?? new Dictionary<string, SnapshotValue>())
			.OrderBy( pair => pair.Key, StringComparer.Ordinal ) )
		{
			copy.Add( StableIdentifier.Require( pair.Key, nameof(values) ), pair.Value );
		}
		return new ReadOnlyDictionary<string, SnapshotValue>( copy );
	}
}
