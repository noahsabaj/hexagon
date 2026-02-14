namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for consumable items (food, drinks, medical supplies, etc.).
/// When used, runs a timed action (if UseTime > 0) then calls OnConsume for schema-defined effects.
/// Return true from OnConsume to destroy the item, false to keep it.
/// </summary>
[AssetType( Name = "Consumable Item", Extension = "consumable" )]
public class ConsumableItemDef : ItemDefinition
{
	/// <summary>
	/// Time in seconds to consume the item. 0 = instant consumption.
	/// If > 0, uses ActionBarManager.DoStaredAction with the ConsumeVerb text.
	/// </summary>
	[Property] public float UseTime { get; set; } = 0f;

	/// <summary>
	/// Optional sound to play when consumption starts.
	/// </summary>
	[Property] public string UseSound { get; set; }

	/// <summary>
	/// Action bar text shown during timed consumption (e.g. "Eating...", "Drinking...").
	/// </summary>
	[Property] public string ConsumeVerb { get; set; } = "Using";

	public override Dictionary<string, ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions["use"] = new ItemAction
		{
			Name = "Use",
			Icon = "restaurant",
			OnRun = ( player, item ) => OnUse( player, item ),
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		};

		return actions;
	}

	public override bool OnUse( HexPlayerComponent player, ItemInstance item )
	{
		if ( !OnCanUse( player, item ) )
			return false;

		if ( !string.IsNullOrEmpty( UseSound ) )
			Sound.Play( UseSound, player.WorldPosition );

		if ( UseTime > 0f )
		{
			Interaction.ActionBarManager.DoStaredAction( player, player.GameObject, ConsumeVerb, UseTime,
				( p ) =>
				{
					if ( p?.Character?.Data == null ) return;

					if ( OnConsume( p, item ) )
					{
						HexEvents.Fire<IItemConsumedListener>( x => x.OnItemConsumed( p, item ) );
						// Remove the item from inventory
						var inventories = Inventory.InventoryManager.LoadForCharacter( p.Character?.Data?.Id );
						foreach ( var inv in inventories )
						{
							if ( inv.Remove( item.Id ) )
								break;
						}
						ItemManager.DestroyInstance( item.Id );
					}
				}
			);

			// Return false — the timed callback handles removal
			return false;
		}

		// Instant consumption
		if ( OnConsume( player, item ) )
		{
			HexEvents.Fire<IItemConsumedListener>( x => x.OnItemConsumed( player, item ) );
			return true; // Consume the item
		}

		return false;
	}

	/// <summary>
	/// Called when consumption completes. Override in schema subclasses for custom effects
	/// (healing, buffs, etc.). Return true to destroy the item, false to keep it.
	/// </summary>
	public virtual bool OnConsume( HexPlayerComponent player, ItemInstance item )
	{
		return true;
	}
}

/// <summary>
/// Fired after a consumable item is successfully consumed.
/// </summary>
public interface IItemConsumedListener
{
	/// <summary>
	/// Called after a consumable item has been used and consumed.
	/// </summary>
	void OnItemConsumed( HexPlayerComponent player, ItemInstance item );
}
