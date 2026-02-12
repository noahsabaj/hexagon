namespace Hexagon.Vendors;

/// <summary>
/// A single item entry in a vendor's catalog with buy/sell prices.
/// </summary>
public class VendorItem
{
	/// <summary>
	/// The ItemDefinition unique ID for this vendor listing.
	/// </summary>
	public string DefinitionId { get; set; }

	/// <summary>
	/// Price a player pays to buy this item. 0 = not for sale.
	/// </summary>
	public int BuyPrice { get; set; }

	/// <summary>
	/// Price a player receives when selling this item. 0 = cannot sell.
	/// </summary>
	public int SellPrice { get; set; }
}

/// <summary>
/// Serializable vendor catalog persisted to the database.
/// </summary>
public class VendorData
{
	/// <summary>
	/// Unique identifier for this vendor. Matches the VendorComponent's VendorId.
	/// </summary>
	public string VendorId { get; set; }

	/// <summary>
	/// Display name of the vendor.
	/// </summary>
	public string VendorName { get; set; }

	/// <summary>
	/// The items this vendor buys and sells.
	/// </summary>
	public List<VendorItem> Items { get; set; } = new();
}
