namespace Hexagon.Inventory;

/// <summary>
/// Client-side inventory update/removal and vendor catalog events.
/// </summary>
public interface IHexInventoryEvent : ISceneEvent<IHexInventoryEvent>
{
	void OnInventoryUpdated( string inventoryId ) { }
	void OnInventoryRemoved( string inventoryId ) { }
	void OnVendorCatalogReceived( string vendorId, string vendorName, List<VendorCatalogEntry> items ) { }
	void OnVendorResult( bool success, string message ) { }
}
