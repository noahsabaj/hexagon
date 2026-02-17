namespace Hexagon.Vendors;

/// <summary>
/// Vendor buy/sell permission, transaction, and interaction events.
/// </summary>
public interface IHexVendorEvent : ISceneEvent<IHexVendorEvent>
{
	bool CanBuyItem( HexPlayerComponent player, VendorComponent vendor, VendorItem item ) => true;
	bool CanSellItem( HexPlayerComponent player, VendorComponent vendor, VendorItem item, ItemInstance instance ) => true;
	void OnItemBought( HexPlayerComponent player, VendorComponent vendor, VendorItem item, ItemInstance instance ) { }
	void OnItemSold( HexPlayerComponent player, VendorComponent vendor, VendorItem item ) { }
	void OnVendorOpened( HexPlayerComponent player, VendorComponent vendor ) { }
}
