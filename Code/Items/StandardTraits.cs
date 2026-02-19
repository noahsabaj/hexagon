namespace Hexagon.Items;

public class WeaponTrait : ItemDataTrait
{
	public int ClipAmmo { get; set; }
}

public class OutfitTrait : ItemDataTrait
{
	public bool Equipped { get; set; }
	public string EquippedSlot { get; set; }
	public string PreviousModel { get; set; }
}

public class CurrencyTrait : ItemDataTrait
{
	public int Amount { get; set; }
}

public class BagTrait : ItemDataTrait
{
	public string BagInventoryId { get; set; }
}
