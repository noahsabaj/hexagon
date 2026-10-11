#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Hexagon.Logic;

/// <summary>The few file operations the store needs. The game backs this with <c>FileSystem.Data</c>.</summary>
public interface IFileStore
{
	bool Exists( string path );
	string Read( string path );
	void Write( string path, string text );
	/// <summary>Adds to the end of a file, creating it if needed.</summary>
	void Append( string path, string text );
	void Delete( string path );
	IEnumerable<string> Find( string folder, string pattern );
}

/// <summary>
/// One JSON file per document. The sandboxed filesystem has no rename, so a write cannot be made
/// atomic; instead every save writes a complete sibling first, then the real file, then removes
/// the sibling. A crash leaves at least one complete copy, and <see cref="Load{T}"/> prefers the
/// real file and falls back to the sibling. A sibling that does not parse is a save that never
/// finished, so the real file is still the last committed state.
/// Documents that must change together, such as both sides of a sale, are saved with
/// <see cref="SaveTogether"/>: all of them are first written into one file, which is the moment
/// the change happens, and that file is replayed if a crash interrupts the rest.
/// </summary>
public sealed class DocumentStore
{
	public const string PendingSuffix = ".pending";
	/// <summary>Where changes to several documents wait while they are written.</summary>
	public const string TogetherFolder = "together";

	private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
	private readonly IFileStore _files;
	private readonly Action<string> _warn;
	private bool _recovered;

	public DocumentStore( IFileStore files, Action<string>? warn = null )
	{
		_files = files ?? throw new ArgumentNullException( nameof(files) );
		_warn = warn ?? (_ => { });
	}

	public void Save<T>( string path, T document ) where T : class
	{
		Recover();
		Commit( path, JsonSerializer.Serialize( document, Options ) );
	}

	/// <summary>
	/// Saves several documents as one change. Every one of them is written into a single file first:
	/// a crash before that file is complete changes nothing, and a crash after it is finished by
	/// <see cref="Recover"/>. So a sale can never leave the goods or the payment in two places.
	/// </summary>
	public void SaveTogether( params (string Path, object Document)[] documents )
	{
		Recover();
		var change = new Together
		{
			Documents = documents.Select( value => new Together.Document { Path = value.Path, Json = JsonSerializer.Serialize( value.Document, value.Document.GetType(), Options ) } ).ToList()
		};
		var path = $"{TogetherFolder}/{Guid.NewGuid():N}.json";
		_files.Write( path, JsonSerializer.Serialize( change, Options ) );
		foreach ( var document in change.Documents ) Commit( document.Path, document.Json );
		try
		{
			_files.Delete( path );
		}
		catch ( Exception ) when ( _files.Exists( path ) )
		{
			// A finished change must never be replayed over later saves. Emptied, it no longer parses.
			_files.Write( path, string.Empty );
			throw;
		}
	}

	/// <summary>
	/// Finishes any change a crash interrupted, and discards any that never completed. Runs once,
	/// before the store first reads or writes anything, so nothing is loaded from a half-made change.
	/// </summary>
	public void Recover()
	{
		if ( _recovered ) return;
		_recovered = true;
		foreach ( var found in _files.Find( TogetherFolder, "*.json" ).ToArray() )
		{
			var path = $"{TogetherFolder}/{found}";
			if ( TryRead<Together>( path ) is { } change )
			{
				foreach ( var document in change.Documents ) Commit( document.Path, document.Json );
				_warn( $"Finished an interrupted save of {change.Documents.Count} documents." );
			}
			else if ( _files.Exists( path ) && _files.Read( path ).Length > 0 )
			{
				_warn( "Discarded a save that a crash interrupted before it changed anything." );
			}
			_files.Delete( path );
		}
	}

	public T? Load<T>( string path ) where T : class
	{
		Recover();
		var committed = TryRead<T>( path );
		var pending = TryRead<T>( path + PendingSuffix );
		if ( committed is null && pending is not null )
		{
			// The crash fell between the two writes: the sibling is the newest complete copy.
			_warn( $"Recovered '{path}' from its pending copy." );
			Save( path, pending );
			return pending;
		}
		if ( _files.Exists( path + PendingSuffix ) ) _files.Delete( path + PendingSuffix );
		if ( committed is null && _files.Exists( path ) ) _warn( $"'{path}' is unreadable and was skipped." );
		return committed;
	}

	public IReadOnlyList<T> LoadAll<T>( string folder ) where T : class
	{
		Recover();
		var paths = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var found in _files.Find( folder, "*.json*" ) )
		{
			var name = found.EndsWith( PendingSuffix, StringComparison.OrdinalIgnoreCase )
				? found[..^PendingSuffix.Length]
				: found;
			if ( name.EndsWith( ".json", StringComparison.OrdinalIgnoreCase ) ) paths.Add( $"{folder}/{name}" );
		}
		var documents = new List<T>();
		foreach ( var path in paths )
			if ( Load<T>( path ) is { } document ) documents.Add( document );
		return documents;
	}

	public void Delete( string path )
	{
		Recover();
		if ( _files.Exists( path + PendingSuffix ) ) _files.Delete( path + PendingSuffix );
		if ( _files.Exists( path ) ) _files.Delete( path );
	}

	/// <summary>The sibling, then the real file, then the sibling is removed.</summary>
	private void Commit( string path, string json )
	{
		_files.Write( path + PendingSuffix, json );
		_files.Write( path, json );
		_files.Delete( path + PendingSuffix );
	}

	private T? TryRead<T>( string path ) where T : class
	{
		if ( !_files.Exists( path ) ) return null;
		try
		{
			return JsonSerializer.Deserialize<T>( _files.Read( path ), Options );
		}
		catch ( JsonException )
		{
			return null;
		}
	}

	/// <summary>Several documents, written as one file before any of them is written alone.</summary>
	private sealed class Together
	{
		public List<Document> Documents { get; set; } = new();

		public sealed class Document
		{
			public string Path { get; set; } = string.Empty;
			public string Json { get; set; } = string.Empty;
		}
	}
}
