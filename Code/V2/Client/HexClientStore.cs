#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Networking;
using ClientInventorySnapshot = Hexagon.V2.Networking.InventorySnapshot;

namespace Hexagon.V2.Client;

public enum ClientLifecycleState
{
	Disconnected = 0,
	Connected = 1,
	CharacterActive = 2
}

public enum ClientStoreChangeKind
{
	SessionStarted = 0,
	StateApplied = 1,
	CharacterListReplaced = 2,
	ChatReplaced = 4,
	SessionCleared = 5,
	LateStateRejected = 6
}

public sealed record ClientStoreChange(ClientStoreChangeKind Kind, long Version);

/// <summary>
/// Realm-agnostic immutable-view store. It contains client snapshots only and
/// performs atomic replacement so Razor panels never observe half-updated state.
/// </summary>
public sealed class HexClientStore
{
	public static int MaximumRetainedChatMessages => 200;

	private ClientStateSnapshot? _state;
	private bool _connected;
	private ClientSessionNonce? _pendingNonce;
	private ClientSessionScope? _scope;
	private readonly Dictionary<Guid, long> _chatMessageRevisions = new();

	public event Action<ClientStoreChange>? Changed;

	public long Version { get; private set; }
	public ClientLifecycleState Lifecycle => !_connected
		? ClientLifecycleState.Disconnected
		: PublicPlayer?.CharacterId is not null && PrivatePlayer is not null
			? ClientLifecycleState.CharacterActive
			: ClientLifecycleState.Connected;

	public ClientStateSnapshot? State => _state;
	public ClientSessionScope? Scope => _scope;
	public ClientStateEpoch? StateEpoch => _state?.Epoch;
	public PlayerPublicSnapshot? PublicPlayer => _state?.Player;
	public PlayerPrivateSnapshot? PrivatePlayer => _state?.PrivatePlayer;
	public PlayerRosterSnapshot? Roster => _state?.Roster;
	public IReadOnlyDictionary<string, SchemaViewSnapshot>? SchemaViews => _state?.SchemaViews;
	public CharacterListSnapshot? CharacterList { get; private set; }
	public IReadOnlyList<ClientInventorySnapshot> Inventories =>
		_state?.Inventories ?? Array.Empty<ClientInventorySnapshot>();
	public ChatSnapshot? Chat { get; private set; }
	public ActionProgressSnapshot? ActiveAction => _state?.ActiveAction;

	public void PrepareSession( ClientSessionNonce nonce )
	{
		ClearAllState();
		_pendingNonce = nonce;
		_scope = null;
		_connected = false;
		Publish(ClientStoreChangeKind.SessionStarted);
	}

	public bool AcceptHello( ClientSessionHello hello )
	{
		if ( _scope is not null )
		{
			if ( _scope.Value == hello.Scope ) return true;
			Publish( ClientStoreChangeKind.LateStateRejected );
			return false;
		}
		if ( _pendingNonce is null || hello.Scope.Nonce != _pendingNonce.Value )
		{
			Publish( ClientStoreChangeKind.LateStateRejected );
			return false;
		}
		_scope = hello.Scope;
		_connected = true;
		Publish( ClientStoreChangeKind.SessionStarted );
		return true;
	}

	/// <summary>
	/// Terminates only the exact authenticated session named by the host. Delayed
	/// revocations from an earlier connection lifetime cannot clear a newer one.
	/// </summary>
	public bool RevokeSession( ClientSessionScope scope )
	{
		if ( _scope is null || _scope.Value != scope ) return false;
		ClearAllState();
		_pendingNonce = null;
		_scope = null;
		_connected = false;
		Publish( ClientStoreChangeKind.SessionCleared );
		return true;
	}

	/// <summary>
	/// Applies a complete host-authored state publication. A different connection
	/// lifetime or a non-increasing revision is rejected without changing any view.
	/// </summary>
	public bool ApplyState( ClientSessionScope scope, ClientStateSnapshot snapshot )
	{
		ArgumentNullException.ThrowIfNull( snapshot );
		if ( !Accepts( scope ) || snapshot.Epoch.Connection != scope.Connection )
		{
			Publish( ClientStoreChangeKind.LateStateRejected );
			return false;
		}
		var current = _state;
		if ( current is not null )
		{
			var currentEpoch = current.Epoch;
			var nextEpoch = snapshot.Epoch;
			if ( nextEpoch.Connection != currentEpoch.Connection ||
				nextEpoch.Revision <= currentEpoch.Revision ||
				nextEpoch.Character < currentEpoch.Character ||
				(nextEpoch.Character == currentEpoch.Character &&
				 snapshot.Player.CharacterId != current.Player.CharacterId) )
			{
				Publish( ClientStoreChangeKind.LateStateRejected );
				return false;
			}
		}

		if ( current is not null && snapshot.Epoch.Character != current.Epoch.Character )
		{
			Chat = null;
			_chatMessageRevisions.Clear();
		}
		_state = snapshot;
		Publish( ClientStoreChangeKind.StateApplied );
		return true;
	}

	public bool ReplaceCharacterList( ClientSessionScope scope, CharacterListSnapshot snapshot )
	{
		if ( !Accepts( scope ) ) return false;
		CharacterList = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
		Publish(ClientStoreChangeKind.CharacterListReplaced);
		return true;
	}

	public bool ReplaceChat( ClientSessionScope scope, ChatSnapshot snapshot )
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if ( !Accepts( scope ) || snapshot.Epoch.Connection != scope.Connection ) return false;
		var stateEpoch = _state?.Epoch;
		if (stateEpoch is null ||
			snapshot.Epoch.Connection != stateEpoch.Value.Connection ||
			snapshot.Epoch.Character != stateEpoch.Value.Character)
			return false;
		var current = Chat;
		if (current is not null && current.Epoch != snapshot.Epoch)
		{
			current = null;
			_chatMessageRevisions.Clear();
		}

		if (current is null && snapshot.Messages.Count <= MaximumRetainedChatMessages)
		{
			foreach (var message in snapshot.Messages)
				_chatMessageRevisions.TryAdd(message.MessageId, snapshot.Revision);
			Chat = snapshot;
		}
		else
		{
			var messages = new List<ChatMessageSnapshot>();
			var messageIds = new HashSet<Guid>();
			if (current is not null)
			{
				foreach (var message in current.Messages)
				{
					messages.Add(message);
					messageIds.Add(message.MessageId);
				}
			}
			foreach (var message in snapshot.Messages)
			{
				if (!messageIds.Add(message.MessageId)) continue;
				messages.Add(message);
				_chatMessageRevisions.Add(message.MessageId, snapshot.Revision);
			}
			var retained = messages
				.OrderBy(message => _chatMessageRevisions[message.MessageId])
				.ThenBy(message => message.SentAtUtc)
				.TakeLast(MaximumRetainedChatMessages)
				.ToArray();
			var retainedIds = retained.Select(message => message.MessageId).ToHashSet();
			foreach (var evicted in _chatMessageRevisions.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
				_chatMessageRevisions.Remove(evicted);
			var revision = current is null ? snapshot.Revision : Math.Max(current.Revision, snapshot.Revision);
			if (current is not null && revision == current.Revision && retained.SequenceEqual(current.Messages)) return false;
			Chat = new ChatSnapshot(snapshot.Epoch, revision, retained);
		}

		Publish(ClientStoreChangeKind.ChatReplaced);
		return true;
	}

	public void ClearSession()
	{
		ClearAllState();
		_pendingNonce = null;
		_scope = null;
		_connected = false;
		Publish(ClientStoreChangeKind.SessionCleared);
	}

	private void ClearAllState()
	{
		_state = null;
		CharacterList = null;
		Chat = null;
		_chatMessageRevisions.Clear();
	}

	public bool Accepts( ClientSessionScope scope ) =>
		_connected && _scope is not null && _scope.Value == scope;

	private void Publish(ClientStoreChangeKind kind)
	{
		Version = checked(Version + 1);
		Changed?.Invoke(new ClientStoreChange(kind, Version));
	}
}
