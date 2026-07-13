#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Hexagon.V2.Networking;

/// <summary>
/// Complete character-scoped view for one client. Player, inventory, and action
/// values cross the network and enter the client store as one atomic publication.
/// </summary>
public sealed record ClientStateSnapshot
{
	public ClientStateSnapshot(
		ClientStateEpoch epoch,
		PlayerPublicSnapshot player,
		PlayerPrivateSnapshot? privatePlayer,
		PlayerRosterSnapshot roster,
		IEnumerable<SchemaViewSnapshot>? schemaViews,
		IEnumerable<InventorySnapshot>? inventories,
		ActionProgressSnapshot? activeAction )
	{
		var inventoryInput = (inventories ?? Array.Empty<InventorySnapshot>()).ToArray();
		var viewInput = (schemaViews ?? Array.Empty<SchemaViewSnapshot>()).ToArray();
		ValidatePayload( player, privatePlayer, roster, viewInput, inventoryInput, activeAction );
		Player = player;
		Roster = roster;

		var inventoryCopy = inventoryInput
			.OrderBy( snapshot => snapshot.InventoryId.Value )
			.ToArray();
		var viewCopy = viewInput
			.OrderBy( view => view.PanelId, StringComparer.Ordinal )
			.ToArray();

		Epoch = epoch;
		PrivatePlayer = privatePlayer;
		SchemaViews = new ReadOnlyDictionary<string, SchemaViewSnapshot>(
			viewCopy.ToDictionary( view => view.PanelId, StringComparer.Ordinal ) );
		Inventories = Array.AsReadOnly( inventoryCopy );
		ActiveAction = activeAction;
	}

	public ClientStateEpoch Epoch { get; }
	public PlayerPublicSnapshot Player { get; }
	public PlayerPrivateSnapshot? PrivatePlayer { get; }
	public PlayerRosterSnapshot Roster { get; }
	public IReadOnlyDictionary<string, SchemaViewSnapshot> SchemaViews { get; }
	public IReadOnlyList<InventorySnapshot> Inventories { get; }
	public ActionProgressSnapshot? ActiveAction { get; }

	public static void ValidatePayload(
		PlayerPublicSnapshot player,
		PlayerPrivateSnapshot? privatePlayer,
		PlayerRosterSnapshot roster,
		IEnumerable<SchemaViewSnapshot>? schemaViews,
		IEnumerable<InventorySnapshot>? inventories,
		ActionProgressSnapshot? activeAction )
	{
		ArgumentNullException.ThrowIfNull( player );
		ArgumentNullException.ThrowIfNull( roster );
		if ( privatePlayer is not null && player.CharacterId != privatePlayer.CharacterId )
			throw new ArgumentException( "Public and private snapshots must identify the same character.", nameof(privatePlayer) );
		if ( !player.HasCharacter && (privatePlayer is not null || activeAction is not null) )
			throw new ArgumentException( "An unloaded player cannot have private or action state.", nameof(privatePlayer) );

		var inventoryCopy = (inventories ?? Array.Empty<InventorySnapshot>()).ToArray();
		if ( inventoryCopy.Any( snapshot => snapshot is null ) )
			throw new ArgumentException( "Inventory snapshots cannot contain null values.", nameof(inventories) );
		if ( inventoryCopy.Select( snapshot => snapshot.InventoryId ).Distinct().Count() != inventoryCopy.Length )
			throw new ArgumentException( "Inventory snapshots must have unique IDs.", nameof(inventories) );
		if ( !player.HasCharacter && inventoryCopy.Length != 0 )
			throw new ArgumentException( "An unloaded player cannot retain inventory views.", nameof(inventories) );

		var viewCopy = (schemaViews ?? Array.Empty<SchemaViewSnapshot>()).ToArray();
		if ( viewCopy.Any( view => view is null ) )
			throw new ArgumentException( "Schema view snapshots cannot contain null values.", nameof(schemaViews) );
		if ( viewCopy.Select( view => view.PanelId ).Distinct( StringComparer.Ordinal ).Count() != viewCopy.Length )
			throw new ArgumentException( "Schema view snapshots must have unique panel IDs.", nameof(schemaViews) );
	}
}
