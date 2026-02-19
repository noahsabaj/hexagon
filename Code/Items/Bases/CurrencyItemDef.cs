namespace Hexagon.Items.Bases;

/// <summary>
/// Base definition for physical currency items. When picked up or used,
/// adds to the character's money. Supports split/merge for different denominations.
/// </summary>
[AssetType( Name = "Currency Item", Extension = "currency" )]
public class CurrencyItemDef : ItemDefinition
{
	/// <summary>
	/// Default value of a newly created currency item.
	/// The actual value is stored per-instance in Data["amount"].
	/// </summary>
	[Property] public int DefaultAmount { get; set; } = 100;

	public override List<ItemAction> GetActions()
	{
		var actions = base.GetActions();

		actions.Add( new ItemAction
		{
			Name = "Pick Up",
			Icon = "payments",
			OnRun = ( player, item ) => OnUse( player, item ),
			OnCanRun = ( player, item ) => OnCanUse( player, item )
		} );

		return actions;
	}

	/// <summary>
	/// Get the amount of money this currency item represents.
	/// </summary>
	public int GetAmount( ItemInstance item )
	{
		return item.TryGetTrait<CurrencyTrait>( out var t ) ? t.Amount : DefaultAmount;
	}

	/// <summary>
	/// Set the amount of money this currency item represents.
	/// </summary>
	public void SetAmount( ItemInstance item, int amount )
	{
		item.GetTrait<CurrencyTrait>().Amount = Math.Max( 0, amount );
		item.MarkDirty();
	}

	public override bool OnUse( HexPlayerComponent player, ItemInstance item )
	{
		var character = player.Character;
		if ( character == null ) return false;

		var amount = GetAmount( item );
		if ( amount <= 0 ) return false;

		// Add to character's money via CharVar
		var currentMoney = character.GetVar<int>( "Money" );
		character.SetVar( "Money", currentMoney + amount );

		return true; // Consume the item
	}

	public override void OnInstanced( ItemInstance item )
	{
		base.OnInstanced( item );

		// Initialize with default amount
		if ( !item.TryGetTrait<CurrencyTrait>( out _ ) )
		{
			item.GetTrait<CurrencyTrait>().Amount = DefaultAmount;
			item.MarkDirty();
		}
	}

	/// <summary>
	/// Create a currency item with a specific amount.
	/// </summary>
	public static ItemInstance CreateWithAmount( string definitionId, int amount, string characterId = null )
	{
		var instance = ItemManager.CreateInstance( definitionId, characterId );
		if ( instance == null ) return null;

		instance.GetTrait<CurrencyTrait>().Amount = amount;
		instance.MarkDirty();
		return instance;
	}
}
