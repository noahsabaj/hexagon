#nullable enable

using System.Collections.Generic;

namespace Hexagon.Logic;

/// <summary>
/// Something a character can do to a target. <paramref name="Requires"/> names a capability, or is null
/// when anyone may. <paramref name="Rank"/> names what a faction's rank must allow, one of
/// <see cref="RankPower"/>, for a verb only a superior offers.
/// </summary>
public sealed record Verb( string Id, string Title, string? Requires = null, string? Rank = null );

/// <summary>What a rank may allow its holder to do to someone else. See <see cref="Ranks"/>.</summary>
public static class RankPower
{
	public const string Enlist = "enlist";
	public const string Promote = "promote";
	public const string Discharge = "discharge";
}

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
	/// <summary>Holding a paper up for someone to read, without letting go of it.</summary>
	public const string PersonShow = "person.show";
	/// <summary>Offering someone a place in your faction, at its lowest rank.</summary>
	public const string PersonEnlist = "person.enlist";
	/// <summary>Giving someone under you another rank, chosen from those yours lets you give.</summary>
	public const string PersonRank = "person.rank";
	/// <summary>Discharging someone under you from your faction.</summary>
	public const string PersonDischarge = "person.discharge";

	public const string DoorUse = "door.use";
	public const string DoorLock = "door.lock";

	public const string ContainerOpen = "container.open";
	/// <summary>Working a machine, such as a press, for as long as its work takes. See <see cref="Making"/>.</summary>
	public const string MachineWork = "machine.work";
	/// <summary>Opening a machine, to load it or take out what it made.</summary>
	public const string MachineOpen = "machine.open";
	/// <summary>Opening a train's wagon while it stands at the station, to unload it or load it.</summary>
	public const string WagonOpen = "wagon.open";
	/// <summary>Working a site, such as a mine or a scrap yard, for what it yields. See <see cref="Sites"/>.</summary>
	public const string SiteWork = "site.work";
	public const string CorpseSearch = "corpse.search";
	/// <summary>Leaning over a map laid out on a table, to mark it up.</summary>
	public const string MapLean = "map.lean";

	/// <summary>Picking up an item lying in the world.</summary>
	public const string ItemTake = "item.take";
	// What a character does with something it carries.
	public const string ItemDrop = "item.drop";
	public const string ItemUse = "item.use";
	public const string ItemEquip = "item.equip";
	public const string ItemTune = "item.tune";
	/// <summary>Putting on something carried, or taking off something worn.</summary>
	public const string ItemWear = "item.wear";
	/// <summary>Laying a map out flat on the table in front of you.</summary>
	public const string ItemLayOut = "item.layout";
	/// <summary>Working with a tool in hand, such as a die, to make something from what is carried.</summary>
	public const string ItemMake = "item.make";
	/// <summary>Telling whether the things in a pile were made alike, with the right tool.</summary>
	public const string ItemCompare = "item.compare";
}

/// <summary>
/// What can be done to a person, by the state anyone can see them in, whether their player is there or
/// left them lying. Someone down can be helped up, searched or finished; someone bound searched,
/// treated, shown a paper or released; anyone else is met, offered something, shown a paper, treated,
/// enlisted, promoted or discharged by a superior whose rank allows it, or bound by whoever has the
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
		var show = new Verb( VerbIds.PersonShow, "Show a paper" );
		if ( restrained ) return new[] { new Verb( VerbIds.PersonSearch, "Search" ), treat, show, new Verb( VerbIds.PersonRelease, "Release" ) };
		return new[]
		{
			new Verb( VerbIds.PersonIntroduce, "Introduce yourself" ), new Verb( VerbIds.PersonOffer, "Offer something" ), show, treat,
			new Verb( VerbIds.PersonEnlist, "Enlist", Rank: RankPower.Enlist ), new Verb( VerbIds.PersonRank, "Promote or demote", Rank: RankPower.Promote ),
			new Verb( VerbIds.PersonDischarge, "Discharge", Rank: RankPower.Discharge ), new Verb( VerbIds.PersonRestrain, "Restrain", Capability.Restrain )
		};
	}
}
