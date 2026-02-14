namespace Hexagon.Core;

/// <summary>
/// Utility for IPressable implementations.
/// </summary>
public static class PressableHelper
{
	/// <summary>
	/// Extract the HexPlayerComponent from a pressable interaction event.
	/// Returns null if the source has no player component.
	/// </summary>
	public static Characters.HexPlayerComponent GetPlayer( Component.IPressable.Event e )
	{
		return e.Source?.GetComponentInParent<Characters.HexPlayerComponent>();
	}
}
