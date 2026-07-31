#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

/// <summary>
/// Binds a chat delivery to one authenticated connection and active-character
/// lifetime so delayed private/channel messages cannot cross a realm boundary.
/// </summary>
public readonly record struct ChatDeliveryEpoch
{
	public ChatDeliveryEpoch(ConnectionEpoch connection, long character)
	{
		if (connection.Value == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(connection));
		if (character < 0) throw new ArgumentOutOfRangeException(nameof(character));
		Connection = connection;
		Character = character;
	}

	public ConnectionEpoch Connection { get; }
	public long Character { get; }
}

public sealed record ChatMessageSnapshot(
	Guid MessageId,
	string ChannelId,
	CharacterId? AuthorCharacterId,
	string AuthorName,
	string Text,
	DateTimeOffset SentAtUtc
);

public sealed record ChatSnapshot
{
	public ChatSnapshot(ChatDeliveryEpoch epoch, long revision, IEnumerable<ChatMessageSnapshot> messages)
	{
		if (epoch.Connection.Value == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(epoch));
		if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
		var copied = (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();
		if (copied.Select(message => message.MessageId).Distinct().Count() != copied.Length)
			throw new ArgumentException("Chat message IDs must be unique within a delivery.", nameof(messages));
		Epoch = epoch;
		Revision = revision;
		Messages = Array.AsReadOnly(copied);
	}

	public ChatDeliveryEpoch Epoch { get; }
	public long Revision { get; }
	public IReadOnlyList<ChatMessageSnapshot> Messages { get; }
}

public sealed record ActionProgressSnapshot
{
	public ActionProgressSnapshot(
		Guid instanceId,
		ActionId actionId,
		string label,
		DateTimeOffset startedAtUtc,
		TimeSpan duration,
		bool canCancel)
	{
		if (instanceId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(instanceId));
		if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
		InstanceId = instanceId;
		ActionId = actionId;
		Label = label ?? string.Empty;
		StartedAtUtc = startedAtUtc;
		Duration = duration;
		CanCancel = canCancel;
	}

	public Guid InstanceId { get; }
	public ActionId ActionId { get; }
	public string Label { get; }
	public DateTimeOffset StartedAtUtc { get; }
	public TimeSpan Duration { get; }
	public bool CanCancel { get; }
}
