using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Tests.Persistence;

internal sealed record TestDocument( string Name, int Score );

internal sealed record CollectionTestDocument
{
	public string Name { get; init; } = "";
	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

internal sealed class MutablePersistedTestDocument
{
	public string Name { get; set; } = "";
	public List<string> Tags { get; init; } = new();
}

[PersistedType( "test.marked", 1 )]
internal sealed record MarkedButUnregisteredTestDocument( string Name );

internal abstract record AbstractTestDocument( string Name );

internal static class PersistenceTestSupport
{
	public static PersistedTypeRegistry CreateRegistry() => new PersistedTypeRegistry()
		.Register<TestDocument>( new PersistedTypeKey( "test.document" ), 1,
			PersistedValuePublication.Immutable )
		.Register<CollectionTestDocument>( new PersistedTypeKey( "test.collection" ), 1,
			static value => value with
			{
				Tags = PersistedValuePublication.ReadOnlyList( value.Tags )
			} );

	public static FileSystemPersistenceProvider CreateFileProvider(
		IPersistenceStorage storage,
		int checkpointEveryCommits = 0 ) => new(
		storage,
		new FileSystemPersistenceOptions( "test-schema" )
		{
			CheckpointEveryCommits = checkpointEveryCommits,
			RetainedCheckpointGenerations = 2
		},
		CreateRegistry() );
}

internal sealed class FaultInjectingStorage : IPersistenceStorage
{
	private readonly InMemoryPersistenceStorage _inner = new();
	private TaskCompletionSource? _appendStarted;
	private TaskCompletionSource? _appendRelease;

	public bool FailNextAppend { get; set; }
	public Func<string, bool>? FailNextImmutableWrite { get; set; }

	public Task BlockNextAppend()
	{
		_appendStarted = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		_appendRelease = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		return _appendStarted.Task;
	}

	public void ReleaseBlockedAppend() =>
		(_appendRelease ?? throw new InvalidOperationException( "No append is blocked." )).TrySetResult();

	public async Task CorruptByteFromEndAsync( string path, int offsetFromEnd )
	{
		var content = await ReadAsync( path ) ?? throw new InvalidOperationException( $"'{path}' does not exist." );
		var bytes = content.ToArray();
		if ( offsetFromEnd <= 0 || offsetFromEnd > bytes.Length )
		{
			throw new ArgumentOutOfRangeException( nameof(offsetFromEnd) );
		}

		bytes[^offsetFromEnd] ^= 0x5A;
		await TruncateAsync( path, 0 );
		await AppendAsync( path, bytes );
	}

	public async Task CorruptByteAsync( string path, int offset )
	{
		var content = await ReadAsync( path ) ?? throw new InvalidOperationException( $"'{path}' does not exist." );
		var bytes = content.ToArray();
		if ( offset < 0 || offset >= bytes.Length )
		{
			throw new ArgumentOutOfRangeException( nameof(offset) );
		}

		bytes[offset] ^= 0x5A;
		await TruncateAsync( path, 0 );
		await AppendAsync( path, bytes );
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.ExistsAsync( path, cancellationToken );

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.ReadAsync( path, cancellationToken );

	public ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default ) =>
		_inner.ListAsync( prefix, cancellationToken );

	public ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		if ( FailNextImmutableWrite?.Invoke( path ) == true )
		{
			FailNextImmutableWrite = null;
			throw new InvalidOperationException( "Injected immutable-write failure." );
		}

		return _inner.TryWriteImmutableAsync( path, content, cancellationToken );
	}

	public async ValueTask AppendAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		if ( FailNextAppend )
		{
			FailNextAppend = false;
			throw new InvalidOperationException( "Injected append failure." );
		}

		if ( _appendStarted is not null && _appendRelease is not null )
		{
			var started = _appendStarted;
			var release = _appendRelease;
			_appendStarted = null;
			started.TrySetResult();
			await release.Task.WaitAsync( cancellationToken );
			if ( ReferenceEquals( _appendRelease, release ) )
			{
				_appendRelease = null;
			}
		}

		await _inner.AppendAsync( path, content, cancellationToken );
	}

	public ValueTask TruncateAsync( string path, long length, CancellationToken cancellationToken = default ) =>
		_inner.TruncateAsync( path, length, cancellationToken );

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.DeleteAsync( path, cancellationToken );
}
