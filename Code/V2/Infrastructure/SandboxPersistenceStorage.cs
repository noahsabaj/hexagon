#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;
using Sandbox;

namespace Hexagon.V2.Infrastructure;

/// <summary>
/// s&amp;box data-filesystem adapter. Every mutation completes and flushes before
/// its ValueTask returns, which makes it a valid WAL durability boundary.
/// </summary>
public sealed class SandboxPersistenceStorage : IPersistenceStorage
{
	private readonly BaseFileSystem _fileSystem;
	private readonly object _sync = new();

	public SandboxPersistenceStorage() : this( FileSystem.Data ) { }

	internal SandboxPersistenceStorage( BaseFileSystem fileSystem ) =>
		_fileSystem = fileSystem ?? throw new ArgumentNullException( nameof(fileSystem) );

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync ) return ValueTask.FromResult( _fileSystem.FileExists( Normalize( path ) ) );
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			if ( !_fileSystem.FileExists( normalized ) )
				return ValueTask.FromResult<ReadOnlyMemory<byte>?>( null );
			using var stream = _fileSystem.OpenRead( normalized );
			if ( stream.Length > int.MaxValue )
				throw new IOException( $"Persistence file '{normalized}' exceeds the supported in-memory read size." );
			var bytes = new byte[(int)stream.Length];
			var offset = 0;
			while ( offset < bytes.Length )
			{
				var read = stream.Read( bytes, offset, bytes.Length - offset );
				if ( read <= 0 )
					throw new EndOfStreamException(
						$"Persistence file '{normalized}' ended at {offset} of {bytes.Length} bytes." );
				offset += read;
			}
			return ValueTask.FromResult<ReadOnlyMemory<byte>?>( new ReadOnlyMemory<byte>( bytes ) );
		}
	}

	public ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( prefix ).TrimEnd( '/' );
			IReadOnlyList<string> paths = _fileSystem.FindFile( normalized, "*", true )
				.Select( relative => $"{normalized}/{relative.Replace( '\\', '/' )}" )
				.OrderBy( path => path, StringComparer.Ordinal )
				.ToArray();
			return ValueTask.FromResult( paths );
		}
	}

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			if ( _fileSystem.FileExists( normalized ) ) return ValueTask.FromResult( false );
			EnsureParentDirectory( normalized );
			using var stream = _fileSystem.OpenWrite( normalized, FileMode.CreateNew );
			stream.Write( content.Span );
			stream.Flush();
			if ( stream.Length != content.Length )
				throw new IOException(
					$"Immutable persistence write for '{normalized}' produced {stream.Length} of {content.Length} bytes." );
			return ValueTask.FromResult( true );
		}
	}

	public ValueTask AppendAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			EnsureParentDirectory( normalized );
			using var stream = _fileSystem.OpenWrite( normalized, FileMode.Append );
			stream.Write( content.Span );
			stream.Flush();
		}
		return ValueTask.CompletedTask;
	}

	public ValueTask TruncateAsync(
		string path,
		long length,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		if ( length < 0 ) throw new ArgumentOutOfRangeException( nameof(length) );
		lock ( _sync )
		{
			var normalized = Normalize( path );
			EnsureParentDirectory( normalized );
			using var stream = _fileSystem.OpenWrite( normalized, FileMode.OpenOrCreate );
			if ( length > stream.Length ) throw new InvalidOperationException( "Truncate cannot extend a persistence file." );
			stream.SetLength( length );
			stream.Flush();
		}
		return ValueTask.CompletedTask;
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = Normalize( path );
			if ( _fileSystem.FileExists( normalized ) ) _fileSystem.DeleteFile( normalized );
		}
		return ValueTask.CompletedTask;
	}

	private void EnsureParentDirectory( string path )
	{
		var separator = path.LastIndexOf( '/' );
		if ( separator <= 0 ) return;
		var directory = path[..separator];
		if ( !_fileSystem.DirectoryExists( directory ) ) _fileSystem.CreateDirectory( directory );
	}

	private static string Normalize( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
			throw new ArgumentException( "Persistence path must be normalized and relative.", nameof(path) );
		return normalized;
	}
}
