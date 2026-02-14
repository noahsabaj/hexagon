namespace Hexagon.Core;

/// <summary>
/// Utility for resolving the calling player in RPC handlers.
/// Eliminates repeated caller validation boilerplate.
/// </summary>
public static class RpcHelper
{
	/// <summary>
	/// Resolve the calling player from the current RPC context.
	/// Returns null if the caller is invalid or not a registered player.
	/// </summary>
	public static Characters.HexPlayerComponent GetCallingPlayer()
	{
		var caller = Rpc.Caller;
		if ( caller == null ) return null;

		return Characters.HexGameManager.GetPlayer( caller );
	}
}
