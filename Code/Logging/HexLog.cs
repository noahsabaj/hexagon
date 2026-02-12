namespace Hexagon.Logging;

/// <summary>
/// Types of log entries for categorization and filtering.
/// </summary>
public enum LogType
{
	Chat,
	Command,
	Item,
	Character,
	Door,
	Vendor,
	Money,
	Admin,
	System
}

/// <summary>
/// A single log entry recording a player action or system event.
/// </summary>
public class LogEntry
{
	public DateTime Timestamp { get; set; }
	public LogType Type { get; set; }
	public ulong SteamId { get; set; }
	public string PlayerName { get; set; }
	public string Message { get; set; }
}

/// <summary>
/// Server-side logging system for tracking player actions and system events.
/// Logs are date-partitioned into separate database collections for efficient querying.
///
/// Storage: hexagon/logs_YYYY-MM-DD/{guid}.json
/// </summary>
public static class HexLog
{
	internal static void Initialize()
	{
		Log.Info( "Hexagon: HexLog initialized." );
	}

	/// <summary>
	/// Record a log entry for a player action.
	/// </summary>
	public static void Add( LogType type, HexPlayerComponent player, string message )
	{
		var entry = new LogEntry
		{
			Timestamp = DateTime.UtcNow,
			Type = type,
			SteamId = player?.SteamId ?? 0,
			PlayerName = player?.DisplayName ?? "System",
			Message = message
		};

		Save( entry );
		NotifyListeners( entry );

		if ( Config.HexConfig.Get<bool>( "framework.debug" ) )
		{
			Log.Info( $"[HexLog] [{type}] {entry.PlayerName}: {message}" );
		}
	}

	/// <summary>
	/// Record a system log entry with no associated player.
	/// </summary>
	public static void Add( LogType type, string message )
	{
		Add( type, null, message );
	}

	/// <summary>
	/// Get all log entries for a specific date.
	/// </summary>
	public static List<LogEntry> GetLogs( DateTime date )
	{
		var collection = GetCollectionName( date );
		return Persistence.DatabaseManager.LoadAll<LogEntry>( collection );
	}

	/// <summary>
	/// Get all log entries for a specific date and type.
	/// </summary>
	public static List<LogEntry> GetLogs( DateTime date, LogType type )
	{
		var collection = GetCollectionName( date );
		return Persistence.DatabaseManager.Select<LogEntry>( collection, e => e.Type == type );
	}

	/// <summary>
	/// Get all log entries for a specific date and player.
	/// </summary>
	public static List<LogEntry> GetLogsForPlayer( DateTime date, ulong steamId )
	{
		var collection = GetCollectionName( date );
		return Persistence.DatabaseManager.Select<LogEntry>( collection, e => e.SteamId == steamId );
	}

	private static void Save( LogEntry entry )
	{
		var collection = GetCollectionName( entry.Timestamp );
		var key = Persistence.DatabaseManager.NewId();
		Persistence.DatabaseManager.Save( collection, key, entry );
	}

	private static string GetCollectionName( DateTime date )
	{
		return $"logs_{date:yyyy-MM-dd}";
	}

	private static void NotifyListeners( LogEntry entry )
	{
		HexEvents.Fire<ILogListener>( x => x.OnLog( entry ) );
	}
}

/// <summary>
/// Listener interface for receiving log entries in real-time.
/// </summary>
public interface ILogListener
{
	void OnLog( LogEntry entry );
}
