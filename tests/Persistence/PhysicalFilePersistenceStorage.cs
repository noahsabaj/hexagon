#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Persistence;

/// <summary>
/// Test-only physical adapter used to verify cross-process lease and crash-recovery behavior.
/// Production physical storage belongs to the standalone game assembly, not the reusable
/// whitelist-constrained Hexagon library.
/// </summary>
internal sealed class PhysicalFilePersistenceStorage : IPersistenceStorage
{
	private readonly string _root;
	private readonly string _rootPrefix;
	private readonly object _sync = new();

	public PhysicalFilePersistenceStorage( string rootPath )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( rootPath );
		_root = Path.GetFullPath( rootPath );
		_rootPrefix = _root.TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) +
			Path.DirectorySeparatorChar;
		Directory.CreateDirectory( _root );
	}

	public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalized = NormalizePath( path );
		var physical = Resolve( normalized );
		lock ( _sync )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( physical )! );
			return ValueTask.FromResult<IPersistenceLease>(
				PhysicalPersistenceLease.Acquire( normalized, physical ) );
		}
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync ) return ValueTask.FromResult( File.Exists( Resolve( NormalizePath( path ) ) ) );
	}

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = NormalizePath( path );
			var physical = Resolve( normalized );
			if ( !File.Exists( physical ) ) return ValueTask.FromResult<ReadOnlyMemory<byte>?>( null );
			var info = new FileInfo( physical );
			if ( info.Length > int.MaxValue )
				throw new IOException( $"Persistence file '{normalized}' exceeds the supported in-memory read size." );
			ReadOnlyMemory<byte>? result = File.ReadAllBytes( physical );
			return ValueTask.FromResult( result );
		}
	}

	public ValueTask<IReadOnlyList<string>> ListAsync(
		string prefix,
		CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var normalized = NormalizePath( prefix );
			var directory = Resolve( normalized );
			if ( !Directory.Exists( directory ) )
				return ValueTask.FromResult<IReadOnlyList<string>>( Array.Empty<string>() );
			IReadOnlyList<string> paths = Directory.EnumerateFiles(
					directory, "*", SearchOption.AllDirectories )
				.Select( physical => Path.GetRelativePath( _root, physical ).Replace( '\\', '/' ) )
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
			var normalized = NormalizePath( path );
			var destination = Resolve( normalized );
			if ( File.Exists( destination ) ) return ValueTask.FromResult( false );
			var directory = Path.GetDirectoryName( destination )!;
			Directory.CreateDirectory( directory );
			var staging = Path.Combine(
				directory,
				$".{Path.GetFileName( destination )}.{Guid.NewGuid():N}.staging" );
			try
			{
				using ( var stream = new FileStream(
					staging,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					bufferSize: 4096,
					FileOptions.WriteThrough ) )
				{
					stream.Write( content.Span );
					stream.Flush( flushToDisk: true );
					if ( stream.Length != content.Length )
						throw new IOException(
							$"Immutable persistence staging write for '{normalized}' produced " +
							$"{stream.Length} of {content.Length} bytes." );
				}
				try
				{
					File.Move( staging, destination, overwrite: false );
					return ValueTask.FromResult( true );
				}
				catch ( IOException ) when ( File.Exists( destination ) )
				{
					return ValueTask.FromResult( false );
				}
			}
			finally
			{
				if ( File.Exists( staging ) ) File.Delete( staging );
			}
		}
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock ( _sync )
		{
			var physical = Resolve( NormalizePath( path ) );
			if ( File.Exists( physical ) ) File.Delete( physical );
		}
		return ValueTask.CompletedTask;
	}

	private string Resolve( string normalized )
	{
		var physical = Path.GetFullPath( Path.Combine(
			_root,
			normalized.Replace( '/', Path.DirectorySeparatorChar ) ) );
		if ( !physical.StartsWith( _rootPrefix, StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"Persistence path '{normalized}' escapes the configured root." );
		return physical;
	}

	private static string NormalizePath( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
			throw new ArgumentException( $"Persistence path '{path}' is not a normalized relative path.", nameof(path) );
		return normalized;
	}

	private sealed class PhysicalPersistenceLease : IPersistenceLease
	{
		private FileStream? _stream;

		private PhysicalPersistenceLease( string path, FileStream stream )
		{
			Path = path;
			_stream = stream;
		}

		public string Path { get; }
		public bool IsReleased => _stream is null;

		public static PhysicalPersistenceLease Acquire( string logicalPath, string physicalPath )
		{
			FileStream? stream = null;
			try
			{
				stream = new FileStream(
					physicalPath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.None,
					bufferSize: 1,
					FileOptions.WriteThrough );
				stream.SetLength( 0 );
				var marker = Encoding.UTF8.GetBytes(
					$"pid={Environment.ProcessId};acquired={DateTimeOffset.UtcNow:O}" );
				stream.Write( marker );
				stream.Flush( flushToDisk: true );
				return new PhysicalPersistenceLease( logicalPath, stream );
			}
			catch ( IOException exception )
			{
				stream?.Dispose();
				throw new PersistenceLeaseUnavailableException(
					$"Persistence lease '{logicalPath}' is already held or unavailable.", exception );
			}
		}

		public async ValueTask DisposeAsync()
		{
			var stream = Interlocked.Exchange( ref _stream, null );
			if ( stream is not null ) await stream.DisposeAsync();
		}
	}
}
