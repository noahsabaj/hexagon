#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

/// <summary>
/// Public presentation state only. PlatformAccountId is display metadata and
/// must never be used by a server authorization path.
/// </summary>
public sealed record PlayerPublicSnapshot
{
	public PlayerPublicSnapshot(
		ConnectionId connectionId,
		ulong platformAccountId,
		string platformDisplayName,
		CharacterId? characterId,
		string characterName,
		string description,
		DefinitionId? model,
		FactionId? faction,
		ClassId? characterClass,
		bool isDead,
		bool isWeaponRaised,
		string? replicatedCharacterName = null)
	{
		ConnectionId = connectionId;
		PlatformAccountId = platformAccountId;
		PlatformDisplayName = platformDisplayName ?? string.Empty;
		CharacterId = characterId;
		CharacterName = characterName ?? string.Empty;
		ReplicatedCharacterName = characterId is null
			? string.Empty
			: string.IsNullOrWhiteSpace(replicatedCharacterName) ? "Unknown citizen" : replicatedCharacterName;
		Description = description ?? string.Empty;
		Model = model;
		Faction = faction;
		Class = characterClass;
		IsDead = isDead;
		IsWeaponRaised = isWeaponRaised;
	}

	public ConnectionId ConnectionId { get; }
	public ulong PlatformAccountId { get; }
	public string PlatformDisplayName { get; }
	public CharacterId? CharacterId { get; }
	public string CharacterName { get; }
	/// <summary>
	/// Non-sensitive label safe for global body replication. The owner-filtered
	/// <see cref="CharacterName"/> may contain recognition-protected identity.
	/// </summary>
	public string ReplicatedCharacterName { get; }
	public string Description { get; }
	public DefinitionId? Model { get; }
	public FactionId? Faction { get; }
	public ClassId? Class { get; }
	public bool IsDead { get; }
	public bool IsWeaponRaised { get; }
	public bool HasCharacter => CharacterId is not null;
}

public sealed record PlayerPrivateSnapshot
{
	public PlayerPrivateSnapshot(
		CharacterId characterId,
		long balance,
		InventoryId? mainInventoryId,
		IReadOnlyDictionary<string, SnapshotValue>? values = null,
		IEnumerable<string>? permissions = null)
	{
		if (balance < 0)
			throw new ArgumentOutOfRangeException(nameof(balance));

		CharacterId = characterId;
		Balance = balance;
		MainInventoryId = mainInventoryId;
		Values = new ReadOnlyDictionary<string, SnapshotValue>(
			new Dictionary<string, SnapshotValue>(
				values ?? new Dictionary<string, SnapshotValue>(),
				StringComparer.Ordinal));
		Permissions = Array.AsReadOnly((permissions ?? Array.Empty<string>())
			.Select(RequirePermission)
			.Distinct(StringComparer.Ordinal)
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray());
	}

	public CharacterId CharacterId { get; }
	public long Balance { get; }
	public InventoryId? MainInventoryId { get; }
	public IReadOnlyDictionary<string, SnapshotValue> Values { get; }
	public IReadOnlyList<string> Permissions { get; }

	private static string RequirePermission(string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		return value;
	}
}
