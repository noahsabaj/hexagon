#nullable enable

using System.Collections.Generic;

namespace Hexagon.Logic;

/// <summary>Something a character can do to a target. <paramref name="Requires"/> names a capability, or is null when anyone may.</summary>
public sealed record Verb( string Id, string Title, string? Requires = null );

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
	/// <summary>Treating someone's wounds with what is in hand.</summary>
	public const string PersonTreat = "person.treat";

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
	/// <summary>Putting on something carried, or taking off something worn.</summary>
	public const string ItemWear = "item.wear";
}

/// <summary>
/// What can be done to a person, by the state anyone can see them in, whether their player is there or
/// left them lying. Someone down can be helped up, searched or finished; someone bound searched,
/// treated or released; anyone else is met, offered something, treated, or bound by whoever has the
/// means. Treating takes something in hand that treats; someone down is helped up first.
/// </summary>
public static class PersonVerbs
{
	/// <summary>The verbs, first the one a careless key would do least harm with. <paramref name="finishRequires"/> is the capability finishing needs, if any.</summary>
	public static IReadOnlyList<Verb> For( bool down, bool restrained, string? finishRequires = null )
	{
		if ( down )
			return new[] { new Verb( VerbIds.PersonRevive, "Help up" ), new Verb( VerbIds.PersonSearch, "Search" ), new Verb( VerbIds.PersonFinish, "Finish", string.IsNullOrEmpty( finishRequires ) ? null : finishRequires ) };
		var treat = new Verb( VerbIds.PersonTreat, "Treat" );
		if ( restrained ) return new[] { new Verb( VerbIds.PersonSearch, "Search" ), treat, new Verb( VerbIds.PersonRelease, "Release" ) };
		return new[] { new Verb( VerbIds.PersonIntroduce, "Introduce yourself" ), new Verb( VerbIds.PersonOffer, "Offer something" ), treat, new Verb( VerbIds.PersonRestrain, "Restrain", Capability.Restrain ) };
	}
}
