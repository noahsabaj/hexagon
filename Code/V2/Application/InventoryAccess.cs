#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

[Flags]
public enum InventoryCapability
{
	None = 0,
	View = 1 << 0,
	Move = 1 << 1,
	TransferIn = 1 << 2,
	TransferOut = 1 << 3,
	Use = 1 << 4,
	Drop = 1 << 5,
	Sell = 1 << 6
}

public enum InventoryGrantKind
{
	Character,
	InteractionSession,
	NestedBag,
	Administrative
}

public sealed record InventoryGrant
{
	public required ConnectionId ConnectionId { get; init; }
	public required CharacterId CharacterId { get; init; }
	public required InventoryId InventoryId { get; init; }
	public required InventoryCapability Capabilities { get; init; }
	public required InventoryGrantKind Kind { get; init; }
	public InteractionSessionId? SessionId { get; init; }
}

/// <summary>
/// Transient, connection-scoped inventory authorization. Grants are intentionally
/// absent from persisted inventory records.
/// </summary>
public sealed class InventoryAccessService
{
	private readonly object _sync = new();
	private readonly Dictionary<
		(ConnectionId Connection, InventoryId Inventory, InventoryGrantKind Kind, InteractionSessionId? Session),
		InventoryGrant> _grants = new();

	public void Grant( InventoryGrant grant )
	{
		if ( grant.Capabilities == InventoryCapability.None )
			throw new ArgumentException( "An inventory grant must provide at least one capability.", nameof(grant) );

		if ( (grant.Kind is InventoryGrantKind.InteractionSession or InventoryGrantKind.NestedBag) && grant.SessionId is null )
			throw new ArgumentException( "Interaction grants require a session ID.", nameof(grant) );
		if ( (grant.Kind is InventoryGrantKind.Character or InventoryGrantKind.Administrative) && grant.SessionId is not null )
			throw new ArgumentException( "Non-session grants cannot carry a session ID.", nameof(grant) );

		lock ( _sync )
			_grants[(grant.ConnectionId, grant.InventoryId, grant.Kind, grant.SessionId)] = grant;
	}

	public bool Has( ConnectionId connectionId, CharacterId characterId, InventoryId inventoryId, InventoryCapability required )
	{
		lock ( _sync )
		{
			var capabilities = InventoryCapability.None;
			foreach ( var grant in _grants.Values.Where( value =>
				value.ConnectionId == connectionId &&
				value.InventoryId == inventoryId &&
				value.CharacterId == characterId ) )
				capabilities |= grant.Capabilities;
			return (capabilities & required) == required;
		}
	}

	public IReadOnlyList<ConnectionId> GetViewers( InventoryId inventoryId )
	{
		lock ( _sync )
		{
			return _grants.Values
				.Where( grant => grant.InventoryId == inventoryId && (grant.Capabilities & InventoryCapability.View) != 0 )
				.Select( grant => grant.ConnectionId )
				.Distinct()
				.ToArray();
		}
	}

	public IReadOnlyList<InventoryGrant> RevokeSession( InteractionSessionId sessionId ) =>
		RevokeWhere( grant => grant.SessionId == sessionId );

	public IReadOnlyList<InventoryGrant> RevokeCharacter( ConnectionId connectionId, CharacterId characterId ) =>
		RevokeWhere( grant => grant.ConnectionId == connectionId && grant.CharacterId == characterId );

	public IReadOnlyList<InventoryGrant> RevokeConnection( ConnectionId connectionId ) =>
		RevokeWhere( grant => grant.ConnectionId == connectionId );

	private IReadOnlyList<InventoryGrant> RevokeWhere( Func<InventoryGrant, bool> predicate )
	{
		lock ( _sync )
		{
			var removed = _grants.Values.Where( predicate ).ToArray();
			foreach ( var grant in removed )
				_grants.Remove( (grant.ConnectionId, grant.InventoryId, grant.Kind, grant.SessionId) );
			return removed;
		}
	}
}
