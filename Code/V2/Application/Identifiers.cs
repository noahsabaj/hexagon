#nullable enable

using System;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

public interface IAggregateIdGenerator
{
	CharacterId NewCharacterId();
	InventoryId NewInventoryId();
	ItemId NewItemId();
	InteractionSessionId NewInteractionSessionId();
}

public sealed class RandomAggregateIdGenerator : IAggregateIdGenerator
{
	public CharacterId NewCharacterId() => new( Guid.NewGuid() );
	public InventoryId NewInventoryId() => new( Guid.NewGuid() );
	public ItemId NewItemId() => new( Guid.NewGuid() );
	public InteractionSessionId NewInteractionSessionId() => new( Guid.NewGuid() );
}
