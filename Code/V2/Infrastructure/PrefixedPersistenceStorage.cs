#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Infrastructure;

/// <summary>
/// Maps a logical provider root beneath an isolated data-filesystem prefix.
/// Verification runs can therefore use disposable roots without changing the
/// persistence format or weakening the production provider.
/// </summary>
public sealed class PrefixedPersistenceStorage : IPersistenceStorage
{
	private readonly IPersistenceStorage _inner;

	public PrefixedPersistenceStorage( IPersistenceStorage inner, string prefix )
	{
		_inner = inner ?? throw new ArgumentNullException( nameof(inner) );
		Prefix = Normalize( prefix );
	}

	public string Prefix { get; }

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.ExistsAsync( Map( path ), cancellationToken );

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.ReadAsync( Map( path ), cancellationToken );

	public async ValueTask<IReadOnlyList<string>> ListAsync(
		string prefix,
		CancellationToken cancellationToken = default )
	{
		var physical = await _inner.ListAsync( Map( prefix ), cancellationToken );
		var marker = Prefix + "/";
		return physical
			.Where( path => path.StartsWith( marker, StringComparison.Ordinal ) )
			.Select( path => path[marker.Length..] )
			.ToArray();
	}

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default ) =>
		_inner.TryWriteImmutableAsync( Map( path ), content, cancellationToken );

	public ValueTask AppendAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default ) =>
		_inner.AppendAsync( Map( path ), content, cancellationToken );

	public ValueTask TruncateAsync(
		string path,
		long length,
		CancellationToken cancellationToken = default ) =>
		_inner.TruncateAsync( Map( path ), length, cancellationToken );

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.DeleteAsync( Map( path ), cancellationToken );

	private string Map( string path ) => $"{Prefix}/{Normalize( path )}";

	public static string Normalize( string path )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( path );
		var normalized = path.Replace( '\\', '/' ).Trim( '/' );
		if ( normalized.Length == 0 || normalized.Split( '/' ).Any( segment => segment is "" or "." or ".." ) )
			throw new ArgumentException( "Persistence prefix must be normalized and relative.", nameof(path) );
		return normalized;
	}
}
