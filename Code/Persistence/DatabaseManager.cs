using System.Threading;
using System.Threading.Tasks;

namespace Hexagon.Persistence;

/// <summary>
/// JSON-based persistence layer using FileSystem.Data.
/// Documents are organized into collections (directories) and identified by string keys.
/// All data is cached in memory for fast reads, with writes going to disk.
///
/// Converted from a static class to a GameObjectSystem so the in-memory cache is
/// scoped to the scene and cleared on scene reload. All public methods remain static
/// via facades — callers need no changes.
///
/// Structure on disk: hexagon/{collection}/{key}.json
/// </summary>
public sealed class DatabaseManager : GameObjectSystem<DatabaseManager>
{
	private static DatabaseManager _instance;

	private readonly Dictionary<string, Dictionary<string, string>> _cache = new();
	private readonly Dictionary<string, Dictionary<string, ICollectionIndex>> _indexes = new();
	private const string BasePath = "hexagon";

	// Background queue for disk IO
	private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _writeQueue = new();
	private readonly CancellationTokenSource _cts = new();
	private Task _workerTask;

	public DatabaseManager( Scene scene ) : base( scene )
	{
		_instance = this;
		FileSystem.Data.CreateDirectory( BasePath );

		// Start background writer
		_workerTask = GameTask.RunInThreadAsync( ProcessWriteQueue );

		Log.Info( "Hexagon: Database initialized." );
	}

	private async Task ProcessWriteQueue()
	{
		var token = _cts.Token;
		while ( !token.IsCancellationRequested )
		{
			if ( _writeQueue.TryDequeue( out var action ) )
			{
				try
				{
					action();
				}
				catch ( Exception e )
				{
					Log.Error( $"Hexagon Database IO error: {e}" );
				}
			}
			else
			{
				// Yield softly when queue is empty
				await Task.Delay( 10, token );
			}
		}
	}

	public override void Dispose()
	{
		// Cancel the background loop and theoretically flush remaining writes
		_cts.Cancel();
		
		// Flush remaining queue synchronously on shutdown
		while ( _writeQueue.TryDequeue( out var action ) )
		{
			try { action(); } catch { }
		}

		_cts.Dispose();

		_cache.Clear();
		_indexes.Clear();

		if ( _instance == this )
			_instance = null;

		base.Dispose();
	}

	private static DatabaseManager Instance => _instance;

	// --- Explicit initialization (triggers GameObjectSystem creation) ---

	internal static void Initialize()
	{
		// Accessing Instance here is enough — the constructor does the real work.
		if ( Instance == null )
		{
			Log.Warning( "Hexagon: DatabaseManager.Initialize() called but instance not yet created." );
		}
	}

	internal static void Shutdown()
	{
		// Cache is cleared in Dispose(). This facade exists for call-site compatibility.
		Instance?._cache.Clear();
	}

	// --- Public Static Facade (unchanged signatures) ---

	/// <summary>
	/// Save a document to a collection. Serializes to JSON and writes to disk.
	/// </summary>
	public static void Save<T>( string collection, string key, T document )
	{
		if ( Instance == null ) return;

		Instance.EnsureCollection( collection );

		var json = Json.Serialize( document );

		if ( !Instance._cache.ContainsKey( collection ) )
			Instance._cache[collection] = new Dictionary<string, string>();

		Instance._cache[collection][key] = json;

		var path = GetPath( collection, key );
		Instance._writeQueue.Enqueue( () => 
		{
			FileSystem.Data.WriteAllText( path, json );
		} );

		if ( Instance._indexes.TryGetValue( collection, out var byField ) )
			foreach ( var idx in byField.Values )
				if ( idx is CollectionIndex<T> typed )
					typed.OnSave( key, document );
	}

	/// <summary>
	/// Load a document from a collection. Checks cache first, then disk.
	/// Returns default(T) if not found.
	/// </summary>
	public static T Load<T>( string collection, string key )
	{
		if ( Instance == null ) return default;

		// Check cache
		if ( Instance._cache.TryGetValue( collection, out var col ) && col.TryGetValue( key, out var cachedJson ) )
		{
			return Json.Deserialize<T>( cachedJson );
		}

		// Load from disk
		var path = GetPath( collection, key );
		if ( !FileSystem.Data.FileExists( path ) )
			return default;

		var json = FileSystem.Data.ReadAllText( path );

		if ( string.IsNullOrEmpty( json ) )
			return default;

		// Cache it
		if ( !Instance._cache.ContainsKey( collection ) )
			Instance._cache[collection] = new Dictionary<string, string>();

		Instance._cache[collection][key] = json;

		return Json.Deserialize<T>( json );
	}

	/// <summary>
	/// Delete a document from a collection.
	/// </summary>
	public static void Delete( string collection, string key )
	{
		if ( Instance?._cache.TryGetValue( collection, out var col ) == true )
			col.Remove( key );

		var path = GetPath( collection, key );
		if ( Instance != null )
		{
			Instance._writeQueue.Enqueue( () => 
			{
				if ( FileSystem.Data.FileExists( path ) )
					FileSystem.Data.DeleteFile( path );
			} );
		}
		else
		{
			if ( FileSystem.Data.FileExists( path ) )
				FileSystem.Data.DeleteFile( path );
		}

		if ( Instance?._indexes.TryGetValue( collection, out var byField ) == true )
			foreach ( var idx in byField.Values )
				idx.OnDelete( key );
	}

	/// <summary>
	/// Check if a document exists in a collection.
	/// </summary>
	public static bool Exists( string collection, string key )
	{
		if ( Instance?._cache.TryGetValue( collection, out var col ) == true && col.ContainsKey( key ) )
			return true;

		return FileSystem.Data.FileExists( GetPath( collection, key ) );
	}

	/// <summary>
	/// Load all documents from a collection. Scans the collection directory for JSON files.
	/// </summary>
	public static List<T> LoadAll<T>( string collection )
	{
		var results = new List<T>();
		var dirPath = $"{BasePath}/{collection}";

		if ( !FileSystem.Data.DirectoryExists( dirPath ) )
			return results;

		foreach ( var file in FileSystem.Data.FindFile( dirPath, "*.json" ) )
		{
			var key = GetKeyFromFilename( file );
			var doc = Load<T>( collection, key );

			if ( doc != null )
				results.Add( doc );
		}

		return results;
	}

	/// <summary>
	/// Load all documents from a collection that match a predicate.
	/// </summary>
	public static List<T> Select<T>( string collection, Func<T, bool> predicate )
	{
		return LoadAll<T>( collection ).Where( predicate ).ToList();
	}

	/// <summary>
	/// Get all document keys in a collection.
	/// </summary>
	public static List<string> GetKeys( string collection )
	{
		var keys = new List<string>();
		var dirPath = $"{BasePath}/{collection}";

		if ( !FileSystem.Data.DirectoryExists( dirPath ) )
			return keys;

		foreach ( var file in FileSystem.Data.FindFile( dirPath, "*.json" ) )
		{
			keys.Add( System.IO.Path.GetFileNameWithoutExtension( file ) );
		}

		return keys;
	}

	/// <summary>
	/// Generate a unique ID for a new document.
	/// </summary>
	public static string NewId()
	{
		return Guid.NewGuid().ToString( "N" );
	}

	/// <summary>
	/// Register an in-memory index for fast field lookups on a collection.
	/// Must be called before any SelectByField() queries on this collection+field.
	/// The index is built lazily on first use and maintained on Save() / Delete().
	/// </summary>
	public static void RegisterIndex<T>( string collection, string fieldName, Func<T, string> keySelector )
	{
		if ( Instance == null ) return;

		var index = new CollectionIndex<T>( collection, fieldName, keySelector );

		if ( !Instance._indexes.TryGetValue( collection, out var byField ) )
		{
			byField = new Dictionary<string, ICollectionIndex>( StringComparer.Ordinal );
			Instance._indexes[collection] = byField;
		}

		byField[fieldName] = index;
	}

	/// <summary>
	/// Select documents whose indexed field equals fieldValue.
	/// Requires a prior RegisterIndex() call for this collection + fieldName.
	/// The index is built lazily on first query and kept current via Save() / Delete().
	/// </summary>
	public static List<T> SelectByField<T>( string collection, string fieldName, string fieldValue )
	{
		if ( Instance == null ) return new();

		if ( Instance._indexes.TryGetValue( collection, out var byField )
			&& byField.TryGetValue( fieldName, out var idxBase )
			&& idxBase is CollectionIndex<T> idx )
		{
			if ( !idx.IsBuilt )
				idx.Build( GetAllWithKeys<T>( collection ) );

			var docKeys = idx.Lookup( fieldValue );
			if ( docKeys == null || docKeys.Count == 0 ) return new();

			var results = new List<T>( docKeys.Count );
			foreach ( var docKey in docKeys )
			{
				var doc = Load<T>( collection, docKey );
				if ( doc != null ) results.Add( doc );
			}
			return results;
		}

		Log.Warning( $"Hexagon: SelectByField called on '{collection}.{fieldName}' with no registered index." );
		return new();
	}

	private static IEnumerable<(string key, T document)> GetAllWithKeys<T>( string collection )
	{
		foreach ( var key in GetKeys( collection ) )
		{
			var doc = Load<T>( collection, key );
			if ( doc != null ) yield return (key, doc);
		}
	}

	private void EnsureCollection( string collection )
	{
		var dirPath = $"{BasePath}/{collection}";
		if ( !FileSystem.Data.DirectoryExists( dirPath ) )
			FileSystem.Data.CreateDirectory( dirPath );
	}

	private static string GetPath( string collection, string key )
	{
		return $"{BasePath}/{collection}/{key}.json";
	}

	private static string GetKeyFromFilename( string filename )
	{
		var dot = filename.LastIndexOf( '.' );
		return dot >= 0 ? filename[..dot] : filename;
	}
}
