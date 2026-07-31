#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Networking;

public sealed record CharacterSummarySnapshot
{
	public CharacterSummarySnapshot(
		CharacterId characterId,
		int slot,
		string name,
		string description,
		DefinitionId model,
		FactionId faction,
		ClassId? characterClass,
		DateTimeOffset lastPlayedAt,
		bool isBanned)
	{
		if (slot < 0)
			throw new ArgumentOutOfRangeException(nameof(slot));

		CharacterId = characterId;
		Slot = slot;
		Name = name ?? string.Empty;
		Description = description ?? string.Empty;
		Model = model;
		Faction = faction;
		Class = characterClass;
		LastPlayedAt = lastPlayedAt;
		IsBanned = isBanned;
	}

	public CharacterId CharacterId { get; }
	public int Slot { get; }
	public string Name { get; }
	public string Description { get; }
	public DefinitionId Model { get; }
	public FactionId Faction { get; }
	public ClassId? Class { get; }
	public DateTimeOffset LastPlayedAt { get; }
	public bool IsBanned { get; }
}

public sealed record CharacterListSnapshot
{
	public CharacterListSnapshot(long revision, IEnumerable<CharacterSummarySnapshot> characters)
	{
		if (revision < 0)
			throw new ArgumentOutOfRangeException(nameof(revision));

		Revision = revision;
		Characters = Array.AsReadOnly((characters ?? throw new ArgumentNullException(nameof(characters)))
			.OrderBy(character => character.Slot)
			.ThenBy(character => character.CharacterId.Value)
			.ToArray());
	}

	public long Revision { get; }
	public IReadOnlyList<CharacterSummarySnapshot> Characters { get; }
}
