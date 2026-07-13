#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Application;

public sealed record CharacterCreationContext(
	AccountId AccountId,
	CharacterCreationRequest Request,
	int Slot,
	DateTimeOffset NowUtc );

public sealed record CharacterStatePlan( TypedPayload State, long StartingBalance );

/// <summary>
/// Computes schema-owned state from authenticated context and registered creation
/// values. The request never contains framework-managed rank, balance or private state.
/// </summary>
public interface ICharacterStateFactory
{
	OperationResult<CharacterStatePlan> Create( CharacterCreationContext context );
}

public sealed record ItemGrantPlan
{
	public required DefinitionId Definition { get; init; }
	public IReadOnlyDictionary<string, TypedPayload> Traits { get; init; } =
		new Dictionary<string, TypedPayload>( StringComparer.Ordinal );
	public BagInventoryPlan? Bag { get; init; }
}

public sealed record BagInventoryPlan
{
	public required int Width { get; init; }
	public required int Height { get; init; }
	public IReadOnlyList<ItemGrantPlan> Items { get; init; } = Array.Empty<ItemGrantPlan>();
}

public sealed record UniqueReservationPlan( string Namespace, string Value );

public sealed record CharacterInitializerContribution
{
	public IReadOnlyList<ItemGrantPlan> Items { get; init; } = Array.Empty<ItemGrantPlan>();
	public IReadOnlyList<UniqueReservationPlan> Reservations { get; init; } =
		Array.Empty<UniqueReservationPlan>();
}

/// <summary>
/// Initializers are pure planners. Persistence, networking and other side effects
/// are deliberately unavailable at this boundary.
/// </summary>
public interface ICharacterInitializer
{
	string Id { get; }
	int Order { get; }
	OperationResult<CharacterInitializerContribution> Build( CharacterCreationContext context, CharacterStatePlan state );
}

public interface ICharacterModelCatalog
{
	bool IsAllowed( DefinitionId model, FactionId faction, ClassId? characterClass );
}

public sealed record CharacterCreatedEvent(
	CharacterRecord Character,
	InventoryRecord MainInventory,
	IReadOnlyList<ItemRecord> Items,
	long CommitSequence );

public sealed record CharacterDeletedEvent( CharacterId CharacterId, AccountId AccountId, long CommitSequence );

public sealed record CharacterCreationReceipt(
	CharacterRecord Character,
	InventoryRecord MainInventory,
	IReadOnlyList<ItemRecord> Items );

public sealed record CharacterDeletionContext( AccountId ActorAccountId, CharacterRecord Character );
