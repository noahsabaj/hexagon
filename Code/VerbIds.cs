#nullable enable

namespace Hexagon;

/// <summary>
/// Every verb a target or an item offers, by id. A client names one in a request, the host checks it
/// against what the target lists, and the journal records it as <c>verb.&lt;id&gt;</c>.
/// </summary>
public static class VerbIds
{
	public const string PersonIntroduce = "person.introduce";
	public const string PersonOffer = "person.offer";
	public const string PersonRestrain = "person.restrain";
	public const string PersonRelease = "person.release";
	public const string PersonSearch = "person.search";
	public const string PersonRevive = "person.revive";
	public const string PersonFinish = "person.finish";

	public const string DoorUse = "door.use";
	public const string DoorLock = "door.lock";

	public const string ContainerOpen = "container.open";
	public const string CorpseSearch = "corpse.search";

	/// <summary>Picking up an item lying in the world.</summary>
	public const string ItemTake = "item.take";
	// What a character does with something it carries.
	public const string ItemDrop = "item.drop";
	public const string ItemUse = "item.use";
	public const string ItemEquip = "item.equip";
	public const string ItemTune = "item.tune";
}
