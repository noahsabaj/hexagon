#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;
using Hexagon.V2.Persistence;

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

public sealed class InventoryAccessProof : ICommitPrecondition
{
	private readonly InventoryAccessService _owner;

	internal InventoryAccessProof(
		InventoryAccessService owner,
		ConnectionId connectionId,
		CharacterId characterId,
		InventoryId inventoryId,
		InventoryCapability required,
		Guid connectionEpoch,
		long revision )
	{
		_owner = owner;
		ConnectionId = connectionId;
		CharacterId = characterId;
		InventoryId = inventoryId;
		Required = required;
		ConnectionEpoch = connectionEpoch;
		CapabilityRevision = revision;
	}

	public ConnectionId ConnectionId { get; }
	public CharacterId CharacterId { get; }
	public InventoryId InventoryId { get; }
	public InventoryCapability Required { get; }
	public Guid ConnectionEpoch { get; }
	public long CapabilityRevision { get; }

	public PersistenceInvariantIssue? Validate( CommitPreconditionContext context ) =>
		_owner.ValidateProof( this );
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
	private readonly Dictionary<(ConnectionId Connection, CharacterId Character, InventoryId Inventory), long> _revisions = new();
	private readonly Dictionary<ConnectionId, Guid> _connectionEpochs = new();
	private long _lastCapabilityRevision;

	internal int RevisionEntryCount
	{
		get { lock ( _sync ) return _revisions.Count; }
	}

	internal int ConnectionEpochCount
	{
		get { lock ( _sync ) return _connectionEpochs.Count; }
	}

	/// <summary>
	/// Opens a fresh authorization epoch for one live connection. Reopening the
	/// same engine ID first removes every prior grant and tuple revision so stale
	/// work cannot survive a reconnect ABA.
	/// </summary>
	public void OpenConnection( ConnectionId connectionId )
	{
		lock ( _sync )
		{
			_ = RemoveGrantsUnderLock( grant => grant.ConnectionId == connectionId );
			foreach ( var key in _revisions.Keys.Where( key => key.Connection == connectionId ).ToArray() )
				_revisions.Remove( key );
			_connectionEpochs[connectionId] = Guid.NewGuid();
		}
	}

	public void Grant( InventoryGrant grant )
	{
		if ( grant.Capabilities == InventoryCapability.None )
			throw new ArgumentException( "An inventory grant must provide at least one capability.", nameof(grant) );

		if ( (grant.Kind is InventoryGrantKind.InteractionSession or InventoryGrantKind.NestedBag) && grant.SessionId is null )
			throw new ArgumentException( "Interaction grants require a session ID.", nameof(grant) );
		if ( (grant.Kind is InventoryGrantKind.Character or InventoryGrantKind.Administrative) && grant.SessionId is not null )
			throw new ArgumentException( "Non-session grants cannot carry a session ID.", nameof(grant) );

		lock ( _sync )
		{
			if ( !_connectionEpochs.ContainsKey( grant.ConnectionId ) )
				throw new InvalidOperationException(
					"Inventory capabilities can only be granted to an open connection epoch." );
			var grantKey = (grant.ConnectionId, grant.InventoryId, grant.Kind, grant.SessionId);
			_grants.TryGetValue( grantKey, out var previous );
			_grants[grantKey] = grant;
			if ( previous is not null && previous.CharacterId != grant.CharacterId )
				PruneOrAdvance( previous.ConnectionId, previous.CharacterId, previous.InventoryId );
			Advance( grant.ConnectionId, grant.CharacterId, grant.InventoryId );
		}
	}

	public InventoryAccessProof? Prove(
		ConnectionId connectionId,
		CharacterId characterId,
		InventoryId inventoryId,
		InventoryCapability required )
	{
		lock ( _sync )
		{
			if ( !HasUnderLock( connectionId, characterId, inventoryId, required ) ) return null;
			if ( !_connectionEpochs.TryGetValue( connectionId, out var connectionEpoch ) ) return null;
			if ( !_revisions.TryGetValue( (connectionId, characterId, inventoryId), out var revision ) ) return null;
			return new InventoryAccessProof(
				this, connectionId, characterId, inventoryId, required,
				connectionEpoch, revision );
		}
	}

	public bool Has( ConnectionId connectionId, CharacterId characterId, InventoryId inventoryId, InventoryCapability required )
	{
		lock ( _sync )
		{
			return HasUnderLock( connectionId, characterId, inventoryId, required );
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

	public IReadOnlyList<InventoryGrant> RevokeConnection( ConnectionId connectionId )
	{
		lock ( _sync )
		{
			var removed = RemoveGrantsUnderLock( grant => grant.ConnectionId == connectionId );
			foreach ( var key in _revisions.Keys.Where( key => key.Connection == connectionId ).ToArray() )
				_revisions.Remove( key );
			_connectionEpochs.Remove( connectionId );
			return removed;
		}
	}

	private IReadOnlyList<InventoryGrant> RevokeWhere( Func<InventoryGrant, bool> predicate )
	{
		lock ( _sync )
		{
			var removed = RemoveGrantsUnderLock( predicate );
			foreach ( var grant in removed
				.DistinctBy( grant => (grant.ConnectionId, grant.CharacterId, grant.InventoryId) ) )
				PruneOrAdvance( grant.ConnectionId, grant.CharacterId, grant.InventoryId );
			return removed;
		}
	}

	internal PersistenceInvariantIssue? ValidateProof( InventoryAccessProof proof )
	{
		lock ( _sync )
		{
			return _connectionEpochs.TryGetValue( proof.ConnectionId, out var connectionEpoch ) &&
				connectionEpoch == proof.ConnectionEpoch &&
				_revisions.TryGetValue( (proof.ConnectionId, proof.CharacterId, proof.InventoryId), out var revision ) &&
				revision == proof.CapabilityRevision &&
				HasUnderLock( proof.ConnectionId, proof.CharacterId, proof.InventoryId, proof.Required )
				? null
				: new PersistenceInvariantIssue(
					"inventory.access.stale",
					$"inventory/{proof.InventoryId}/access",
					"Inventory capability changed after the action was planned." );
		}
	}

	private bool HasUnderLock(
		ConnectionId connectionId,
		CharacterId characterId,
		InventoryId inventoryId,
		InventoryCapability required )
	{
		if ( !_connectionEpochs.ContainsKey( connectionId ) ) return false;
		var capabilities = InventoryCapability.None;
		foreach ( var grant in _grants.Values.Where( value =>
			value.ConnectionId == connectionId && value.InventoryId == inventoryId &&
			value.CharacterId == characterId ) )
			capabilities |= grant.Capabilities;
		return (capabilities & required) == required;
	}

	private void Advance( ConnectionId connectionId, CharacterId characterId, InventoryId inventoryId )
	{
		if ( !_connectionEpochs.ContainsKey( connectionId ) )
			throw new InvalidOperationException(
				"Inventory capability revision cannot advance for a closed connection." );
		_revisions[(connectionId, characterId, inventoryId)] = checked(++_lastCapabilityRevision);
	}

	private void PruneOrAdvance( ConnectionId connectionId, CharacterId characterId, InventoryId inventoryId )
	{
		var key = (connectionId, characterId, inventoryId);
		if ( _grants.Values.Any( grant =>
			grant.ConnectionId == connectionId && grant.CharacterId == characterId &&
			grant.InventoryId == inventoryId ) )
		{
			Advance( connectionId, characterId, inventoryId );
			return;
		}
		_revisions.Remove( key );
	}

	private InventoryGrant[] RemoveGrantsUnderLock( Func<InventoryGrant, bool> predicate )
	{
		var removed = _grants.Values.Where( predicate ).ToArray();
		foreach ( var grant in removed )
			_grants.Remove( (grant.ConnectionId, grant.InventoryId, grant.Kind, grant.SessionId) );
		return removed;
	}

}
