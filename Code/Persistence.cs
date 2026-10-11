#nullable enable

using System;
using System.Collections.Generic;
using Sandbox;
using Hexagon.Logic;

namespace Hexagon;

/// <summary>Backs the document store with the game's sandboxed data folder.</summary>
public sealed class SandboxFileStore : IFileStore
{
	/// <summary>Folder under the game's data directory. Tests point this elsewhere so they never touch a real city.</summary>
	[ConVar( "hexagon_data_root" )]
	public static string Root { get; set; } = "hexagon";

	/// <summary>
	/// The game's data folder, taken once when the store is made. <c>FileSystem.Data</c> belongs to
	/// whichever context the engine is running in, and the city's closing save can run in the menu's,
	/// which has none: the editor stops play from its main-thread queue, and the menu drains that too.
	/// </summary>
	private readonly BaseFileSystem _files = FileSystem.Data;

	public bool Exists( string path ) => _files.FileExists( Full( path ) );
	public string Read( string path ) => _files.ReadAllText( Full( path ) );
	public void Delete( string path ) => _files.DeleteFile( Full( path ) );

	public void Write( string path, string text )
	{
		var full = Full( path );
		var slash = full.LastIndexOf( '/' );
		if ( slash > 0 ) _files.CreateDirectory( full[..slash] );
		_files.WriteAllText( full, text );
	}

	public void Append( string path, string text )
	{
		var full = Full( path );
		var slash = full.LastIndexOf( '/' );
		if ( slash > 0 ) _files.CreateDirectory( full[..slash] );
		using var stream = _files.OpenWrite( full, System.IO.FileMode.Append );
		var bytes = System.Text.Encoding.UTF8.GetBytes( text );
		stream.Write( bytes, 0, bytes.Length );
	}

	public IEnumerable<string> Find( string folder, string pattern ) =>
		_files.DirectoryExists( Full( folder ) ) ? _files.FindFile( Full( folder ), pattern ) : Array.Empty<string>();

	private static string Full( string path ) => $"{Root}/{path}";
}
