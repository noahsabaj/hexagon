namespace Hexagon.Logging;

/// <summary>
/// Real-time log entry events.
/// </summary>
public interface IHexLogEvent : ISceneEvent<IHexLogEvent>
{
	void OnLog( LogEntry entry ) { }
}
