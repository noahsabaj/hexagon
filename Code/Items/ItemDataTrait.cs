namespace Hexagon.Items;

/// <summary>
/// A strongly-typed data component that can be attached to an ItemInstance.
/// Schema developers should subclass this to define stateful data for their items.
/// 
/// Example:
/// public class AmmoTrait : ItemDataTrait
/// {
///     public int Clip { get; set; }
/// }
/// </summary>
public abstract class ItemDataTrait
{
	// Note: the System.Text.Json polymophic serialization utilizes S&box's 
	// TypeLibrary attributes to properly figure out subclass types natively.
}
