#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Domain;

public sealed record CharacterCreationRequest
{
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required DefinitionId Model { get; init; }
	public required FactionId Faction { get; init; }
	public ClassId? Class { get; init; }
	public IReadOnlyDictionary<string, CreationValue> Fields { get; init; } = new Dictionary<string, CreationValue>();
}

/// <summary>
/// Framework-owned character state. Schema-specific state is composed through
/// <see cref="SchemaState"/> and an explicitly registered persisted codec.
/// </summary>
public sealed record CharacterRecord
{
	public required CharacterId Id { get; init; }
	public required AccountId AccountId { get; init; }
	public required int Slot { get; init; }
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required DefinitionId Model { get; init; }
	public required FactionId Faction { get; init; }
	public ClassId? Class { get; init; }
	public required long Balance { get; init; }
	public required DateTimeOffset CreatedAt { get; init; }
	public required DateTimeOffset LastPlayedAt { get; init; }
	public bool IsBanned { get; init; }
	public DateTimeOffset? BanExpiresAt { get; init; }
	public required TypedPayload SchemaState { get; init; }
	public long Revision { get; init; }

	public CharacterRecord DeepCopy() => this with { SchemaState = SchemaState.DeepCopy() };

	public CharacterRecord WithBalance( long balance )
	{
		if ( balance < 0 ) throw new ArgumentOutOfRangeException( nameof(balance), "Balance cannot be negative." );
		return this with { Balance = balance };
	}

	public bool IsBanActive( DateTimeOffset now ) =>
		IsBanned && (BanExpiresAt is null || BanExpiresAt > now);
}
