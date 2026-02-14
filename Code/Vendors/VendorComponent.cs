namespace Hexagon.Vendors;

/// <summary>
/// A world-placed vendor NPC that players can interact with to buy and sell items.
/// Interacted with via the USE key (IPressable).
///
/// Place on any GameObject in the scene. Set VendorId in the editor or let it auto-generate.
/// Configure the catalog via admin commands or the public API.
/// </summary>
public sealed class VendorComponent : Component, Component.IPressable
{
	/// <summary>
	/// Unique identifier for this vendor. Auto-generated if empty on enable.
	/// </summary>
	[Property] public string VendorId { get; set; } = "";

	/// <summary>
	/// Display name shown in the tooltip and UI.
	/// </summary>
	[Property] public string VendorName { get; set; } = "Vendor";

	private VendorData _data;

	/// <summary>
	/// The underlying vendor data loaded from the database.
	/// </summary>
	public VendorData Data => _data;

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		if ( string.IsNullOrEmpty( VendorId ) )
		{
			VendorId = Persistence.DatabaseManager.NewId();
		}

		_data = VendorManager.LoadVendor( VendorId );

		if ( _data == null )
		{
			_data = new VendorData
			{
				VendorId = VendorId,
				VendorName = VendorName
			};
		}

		VendorManager.Register( this );
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;

		SaveData();
		VendorManager.Unregister( this );
	}

	// --- IPressable ---

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = Core.PressableHelper.GetPlayer( e );
		return player?.Character != null;
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = Core.PressableHelper.GetPlayer( e );
		if ( player?.Character == null ) return false;

		HexEvents.Fire<IVendorOpenedListener>(
			x => x.OnVendorOpened( player, this ) );

		// Send vendor catalog to the interacting player
		if ( Inventory.HexInventoryComponent.Instance != null && player.Connection != null )
		{
			var catalogEntries = GetItems().Select( i =>
			{
				var def = Items.ItemManager.GetDefinition( i.DefinitionId );
				return new Inventory.VendorCatalogEntry
				{
					DefinitionId = i.DefinitionId,
					DisplayName = def?.DisplayName ?? i.DefinitionId,
					Description = def?.Description ?? "",
					Category = def?.Category ?? "Misc",
					BuyPrice = i.BuyPrice,
					SellPrice = i.SellPrice
				};
			} ).ToList();

			var catalogJson = Json.Serialize( catalogEntries );

			using ( Rpc.FilterInclude( player.Connection ) )
			{
				Inventory.HexInventoryComponent.Instance.ReceiveVendorCatalog(
					VendorId, VendorName, catalogJson );
			}
		}

		HexLog.Add( LogType.Vendor, player, $"Opened vendor \"{VendorName}\" ({VendorId})" );
		return true;
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		return new Component.IPressable.Tooltip( VendorName, "storefront", "Buy & Sell" );
	}

	// --- Catalog API ---

	/// <summary>
	/// Add or update an item in the vendor's catalog.
	/// </summary>
	public void AddItem( string definitionId, int buyPrice, int sellPrice )
	{
		var maxItems = Config.HexConfig.Get<int>( "vendor.maxItems", 50 );

		var existing = _data.Items.FirstOrDefault( i => i.DefinitionId == definitionId );
		if ( existing != null )
		{
			existing.BuyPrice = buyPrice;
			existing.SellPrice = sellPrice;
		}
		else
		{
			if ( _data.Items.Count >= maxItems ) return;

			_data.Items.Add( new VendorItem
			{
				DefinitionId = definitionId,
				BuyPrice = buyPrice,
				SellPrice = sellPrice
			} );
		}

		SaveData();
	}

	/// <summary>
	/// Remove an item from the vendor's catalog.
	/// </summary>
	public bool RemoveItem( string definitionId )
	{
		var removed = _data.Items.RemoveAll( i => i.DefinitionId == definitionId ) > 0;
		if ( removed ) SaveData();
		return removed;
	}

	/// <summary>
	/// Get all items in the vendor's catalog.
	/// </summary>
	public List<VendorItem> GetItems()
	{
		return _data?.Items ?? new();
	}

	/// <summary>
	/// Get a specific vendor item by definition ID.
	/// </summary>
	public VendorItem GetVendorItem( string definitionId )
	{
		return _data?.Items.FirstOrDefault( i => i.DefinitionId == definitionId );
	}

	private void SaveData()
	{
		if ( _data == null ) return;
		_data.VendorName = VendorName;
		VendorManager.SaveVendor( _data );
	}
}
