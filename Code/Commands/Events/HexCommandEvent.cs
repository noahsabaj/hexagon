namespace Hexagon.Commands;

/// <summary>
/// Command execution permission events.
/// </summary>
public interface IHexCommandEvent : ISceneEvent<IHexCommandEvent>
{
	bool CanRunCommand( HexPlayerComponent player, HexCommand command ) => true;
}
