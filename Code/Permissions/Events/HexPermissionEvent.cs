namespace Hexagon.Permissions;

/// <summary>
/// Schema-defined permission check events beyond simple flags.
/// </summary>
public interface IHexPermissionEvent : ISceneEvent<IHexPermissionEvent>
{
	bool OnPermissionCheck( HexPlayerComponent player, string permission ) => true;
}
