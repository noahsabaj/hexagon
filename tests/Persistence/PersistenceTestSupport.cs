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
		int checkpointEveryCommits = 0,
		bool quarantineCorruptStore = false ) => new(
		storage,
		new FileSystemPersistenceOptions( "test-schema" )
		{
			CheckpointEveryCommits = checkpointEveryCommits,
			RetainedCheckpointGenerations = 2,
			QuarantineCorruptStore = quarantineCorruptStore
		},
		CreateRegistry() );
}

internal sealed class FaultInjectingStorage : IPersistenceStorage
{
	private readonly InMemoryPersistenceStorage _inner = new();
	private TaskCompletionSource? _immutableStarted;
	private TaskCompletionSource? _immutableRelease;

	public Func<string, bool>? FailNextImmutableWrite { get; set; }
	public Func<string, bool>? FailNextRead { get; set; }
	public Func<string, bool>? FailNextDelete { get; set; }
	public bool FailNextLeaseDispose { get; set; }

	public async ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(
		string path,
		CancellationToken cancellationToken = default )
	{
		var lease = await _inner.AcquireExclusiveLeaseAsync( path, cancellationToken );
		return new FaultInjectingLease( this, lease );
	}

	public Task BlockNextImmutableWrite()
	{
		_immutableStarted = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		_immutableRelease = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
		return _immutableStarted.Task;
	}

	public void ReleaseBlockedImmutableWrite() =>
		(_immutableRelease ?? throw new InvalidOperationException( "No immutable write is blocked." )).TrySetResult();

	public async Task CorruptByteFromEndAsync( string path, int offsetFromEnd )
	{
		var content = await ReadAsync( path ) ?? throw new InvalidOperationException( $"'{path}' does not exist." );
		var bytes = content.ToArray();
		if ( offsetFromEnd <= 0 || offsetFromEnd > bytes.Length )
		{
			throw new ArgumentOutOfRangeException( nameof(offsetFromEnd) );
		}

		bytes[^offsetFromEnd] ^= 0x5A;
		await OverwriteAsync( path, bytes );
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
		await OverwriteAsync( path, bytes );
	}

	public async Task OverwriteAsync( string path, ReadOnlyMemory<byte> content )
	{
		await _inner.DeleteAsync( path );
		if ( !await _inner.TryWriteImmutableAsync( path, content ) )
			throw new InvalidOperationException( $"Could not overwrite test path '{path}'." );
	}

	public async Task SeedAsync( string path, ReadOnlyMemory<byte> content )
	{
		if ( !await _inner.TryWriteImmutableAsync( path, content ) )
			throw new InvalidOperationException( $"Could not seed test path '{path}'." );
	}

	public ValueTask<bool> ExistsAsync( string path, CancellationToken cancellationToken = default ) =>
		_inner.ExistsAsync( path, cancellationToken );

	public ValueTask<ReadOnlyMemory<byte>?> ReadAsync( string path, CancellationToken cancellationToken = default )
	{
		if ( FailNextRead?.Invoke( path ) == true )
		{
			FailNextRead = null;
			throw new InvalidOperationException( "Injected read failure." );
		}
		return _inner.ReadAsync( path, cancellationToken );
	}

	public ValueTask<IReadOnlyList<string>> ListAsync( string prefix, CancellationToken cancellationToken = default ) =>
		_inner.ListAsync( prefix, cancellationToken );

	public async ValueTask<bool> TryWriteImmutableAsync(
		string path,
		ReadOnlyMemory<byte> content,
		CancellationToken cancellationToken = default )
	{
		if ( FailNextImmutableWrite?.Invoke( path ) == true )
		{
			FailNextImmutableWrite = null;
			throw new InvalidOperationException( "Injected immutable-write failure." );
		}
		if ( _immutableStarted is not null && _immutableRelease is not null )
		{
			var started = _immutableStarted;
			var release = _immutableRelease;
			_immutableStarted = null;
			started.TrySetResult();
			await release.Task.WaitAsync( cancellationToken );
			if ( ReferenceEquals( _immutableRelease, release ) ) _immutableRelease = null;
		}

		return await _inner.TryWriteImmutableAsync( path, content, cancellationToken );
	}

	public ValueTask DeleteAsync( string path, CancellationToken cancellationToken = default )
	{
		if ( FailNextDelete?.Invoke( path ) == true )
		{
			FailNextDelete = null;
			throw new InvalidOperationException( "Injected delete failure." );
		}
		return _inner.DeleteAsync( path, cancellationToken );
	}

	private bool ConsumeLeaseDisposeFailure()
	{
		if ( !FailNextLeaseDispose ) return false;
		FailNextLeaseDispose = false;
		return true;
	}

	private sealed class FaultInjectingLease : IPersistenceLease
	{
		private readonly FaultInjectingStorage _owner;
		private readonly IPersistenceLease _inner;

		public FaultInjectingLease( FaultInjectingStorage owner, IPersistenceLease inner )
		{
			_owner = owner;
			_inner = inner;
		}

		public string Path => _inner.Path;
		public bool IsReleased => _inner.IsReleased;

		public async ValueTask DisposeAsync()
		{
			if ( _owner.ConsumeLeaseDisposeFailure() )
				throw new InvalidOperationException( "Injected lease-disposal failure." );
			await _inner.DisposeAsync();
		}
	}
}
