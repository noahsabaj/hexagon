namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for outfit/clothing items. When equipped, changes the player's
/// model or bodygroups. Handles equip/unequip with model restoration.
/// </summary>
[AssetType( Name = "Outfit Item", Extension = "outfit" )]
public class OutfitItemDef : ItemDefinition
{
	/// <summary>
	/// The model to apply when this outfit is equipped.
	/// If null, the player keeps their current model.
	/// </summary>
	[Property] public Model OutfitModel { get; set; }

	/// <summary>
	/// Bodygroup overrides to apply when equipped. Key = bodygroup name, Value = choice index.
	/// </summary>
	[Property] public Dictionary<string, int> Bodygroups { get; set; } = new();

	/// <summary>
	/// Equipment slot this outfit occupies (e.g. "head", "torso", "legs", "feet").
	/// </summary>
	[Property] public string Slot { get; set; } = "torso";

	public override Dictionary<string, ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions["wear"] = new ItemAction
		{
			Name = "Wear",
			Icon = "checkroom",
			OnRun = ( player, item ) =>
			{
				OnEquip( player, item );
				return false;
			},
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		};

		actions["takeoff"] = new ItemAction
		{
			Name = "Take Off",
			Icon = "remove_circle_outline",
			OnRun = ( player, item ) =>
			{
				OnUnequip( player, item );
				return false;
			}
		};

		return actions;
	}

	public override void OnEquip( HexPlayerComponent player, ItemInstance item )
	{
		base.OnEquip( player, item );

		// Store the player's current model so we can restore it
		var renderer = player.Components.Get<SkinnedModelRenderer>();
		if ( renderer != null && OutfitModel != null )
		{
			item.SetData( "previousModel", renderer.Model?.ResourcePath ?? "" );
			renderer.Model = OutfitModel;
		}

		// Apply bodygroup overrides
		if ( renderer != null && Bodygroups != null )
		{
			foreach ( var kvp in Bodygroups )
			{
				renderer.SetBodyGroup( kvp.Key, kvp.Value );
			}
		}

		item.SetData( "equipped", true );
		item.SetData( "equippedSlot", Slot );
	}

	public override void OnUnequip( HexPlayerComponent player, ItemInstance item )
	{
		base.OnUnequip( player, item );

		// Restore previous model
		var renderer = player.Components.Get<SkinnedModelRenderer>();
		if ( renderer != null && OutfitModel != null )
		{
			var previousPath = item.GetData<string>( "previousModel", "" );
			if ( !string.IsNullOrEmpty( previousPath ) )
			{
				renderer.Model = Model.Load( previousPath );
			}
		}

		item.RemoveData( "equipped" );
		item.RemoveData( "equippedSlot" );
		item.RemoveData( "previousModel" );
	}
}
