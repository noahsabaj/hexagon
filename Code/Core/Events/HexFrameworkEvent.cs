namespace Hexagon.Core;

/// <summary>
/// Framework lifecycle events.
/// </summary>
public interface IHexFrameworkEvent : ISceneEvent<IHexFrameworkEvent>
{
	void OnFrameworkInit() { }
	void OnFrameworkShutdown() { }
}
