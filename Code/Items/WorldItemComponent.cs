namespace Hexagon.Items;

/// <summary>
/// Networked component for a dropped item in the world.
/// Spawned by WorldItemManager.SpawnWorldItem when a player drops an item from their inventory.
///
/// All connected clients can see dropped items because this lives on a networked GameObject.
/// Clients call RequestPickup() [Rpc.Host] to pick up the item; the server validates and transfers.
/// </summary>
public sealed class WorldItemComponent : Component
{
	/// <summary>
	/// The ItemInstance ID this world item represents.
	/// </summary>
	[Sync] public string ItemInstanceId { get; set; }

	/// <summary>
	/// The ItemDefinition unique ID. Used client-side to apply the correct world model.
	/// Fires ApplyModel() whenever the value is received or changes — no per-frame polling needed.
	/// </summary>
	[Sync, Change( nameof( ApplyModel ) )] public string DefinitionId { get; set; }

	/// <summary>
	/// Display name shown in any client-side interaction prompts.
	/// </summary>
	[Sync] public string DisplayName { get; set; }

	protected override void OnStart()
	{
		// Apply model immediately for clients that join after this item already exists.
		if ( IsProxy )
			ApplyModel();
	}

	private void ApplyModel()
	{
		var def = ItemManager.GetDefinition( DefinitionId );
		if ( def?.WorldModel == null ) return;

		var renderer = GetOrAddComponent<ModelRenderer>();
		renderer.Model = def.WorldModel;
	}

	// --- Server RPC: client requests pickup ---

	/// <summary>
	/// Client calls this to request picking up the item.
	/// Server validates ownership, inventory space, and then transfers the item.
	/// </summary>
	[Rpc.Host]
	public void RequestPickup()
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player?.Character == null ) return;

		// Validate item still exists in DB
		var item = ItemManager.GetInstance( ItemInstanceId );
		if ( item == null )
		{
			WorldItemManager.DespawnWorldItem( ItemInstanceId );
			return;
		}

		// Find a player inventory that can fit this item.
		// Active players have inventories already in memory — no DB query needed.
		var inventories = Inventory.InventoryManager.GetForCharacter( player.Character.Id );
		Inventory.HexInventory target = null;

		foreach ( var inv in inventories )
		{
			var def = item.Definition;
			if ( def == null ) continue;

			if ( inv.FindEmptySlot( def.Width, def.Height ) != null )
			{
				target = inv;
				break;
			}
		}

		if ( target == null )
		{
			// No space — let the client know
			player.GetComponent<UI.NotificationPlayerComponent>()?
				.ReceiveNotification( "You have no inventory space for that item.", 3f );
			return;
		}

		// Add item to inventory (finds an empty slot automatically)
		if ( !target.Add( item ) )
		{
			player.GetComponent<UI.NotificationPlayerComponent>()?
				.ReceiveNotification( "Could not pick up item.", 3f );
			return;
		}

		// Notify definition
		item.Definition?.OnPickup( player, item );

		// Remove the world object
		WorldItemManager.DespawnWorldItem( ItemInstanceId );
	}
}
