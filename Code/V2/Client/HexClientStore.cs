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
	private readonly Dictionary<Guid, long> _chatMessageRevisions = new();

	public event Action<ClientStoreChange>? Changed;

	public long Version { get; private set; }
	public ClientLifecycleState Lifecycle => !_connected
		? ClientLifecycleState.Disconnected
		: PublicPlayer?.CharacterId is not null && PrivatePlayer is not null
			? ClientLifecycleState.CharacterActive
			: ClientLifecycleState.Connected;

	public ClientStateSnapshot? State => _state;
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

	public void BeginSession()
	{
		ClearAllState();
		_connected = true;
		Publish(ClientStoreChangeKind.SessionStarted);
	}

	/// <summary>
	/// Applies a complete host-authored state publication. A different connection
	/// lifetime or a non-increasing revision is rejected without changing any view.
	/// </summary>
	public bool ApplyState( ClientStateSnapshot snapshot )
	{
		ArgumentNullException.ThrowIfNull( snapshot );
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
		_connected = true;
		Publish( ClientStoreChangeKind.StateApplied );
		return true;
	}

	public void ReplaceCharacterList(CharacterListSnapshot snapshot)
	{
		CharacterList = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
		_connected = true;
		Publish(ClientStoreChangeKind.CharacterListReplaced);
	}

	public void ReplaceChat(ChatSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var stateEpoch = _state?.Epoch;
		if (stateEpoch is null ||
			snapshot.Epoch.Connection != stateEpoch.Value.Connection ||
			snapshot.Epoch.Character != stateEpoch.Value.Character)
			return;
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
			if (current is not null && revision == current.Revision && retained.SequenceEqual(current.Messages)) return;
			Chat = new ChatSnapshot(snapshot.Epoch, revision, retained);
		}

		_connected = true;
		Publish(ClientStoreChangeKind.ChatReplaced);
	}

	public void ClearSession()
	{
		ClearAllState();
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

	private void Publish(ClientStoreChangeKind kind)
	{
		Version = checked(Version + 1);
		Changed?.Invoke(new ClientStoreChange(kind, Version));
	}
}
