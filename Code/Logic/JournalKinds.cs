#nullable enable

namespace Hexagon.Logic;

/// <summary>
/// What each journal entry is, by name. Staff search the journal by these and tests count them, so
/// each is written in one place and every entry of a kind is spelled the same.
/// </summary>
public static class JournalKinds
{
	public const string ConnectionJoin = "connection.join";
	public const string ConnectionLeave = "connection.leave";

	public const string CharacterCreate = "character.create";
	public const string CharacterDelete = "character.delete";
	public const string CharacterRetire = "character.retire";
	public const string CharacterDowned = "character.downed";
	/// <summary>A blow left a wound, with where and from what.</summary>
	public const string CharacterWounded = "character.wounded";
	/// <summary>A wound closed, or health given back, by whoever treated it, on the character treated.</summary>
	public const string CharacterTreated = "character.treated";
	public const string CharacterDeath = "character.death";

	public const string CityEnter = "city.enter";
	public const string CityLeave = "city.leave";

	/// <summary>Something said that could not be: too long, empty, or on no channel.</summary>
	public const string Chat = "chat";
	/// <summary>Speech is journaled by its channel: chat.say, chat.whisper, chat.yell, chat.me, chat.ooc.</summary>
	public static string Spoken( ChatChannel channel ) => $"chat.{channel.ToString().ToLowerInvariant()}";
	public const string ChatRadio = "chat.radio";

	public const string CombatAttack = "combat.attack";
	public const string MovementImplausible = "movement.implausible";

	public const string ItemIssue = "item.issue";
	public const string ItemMove = "item.move";
	public const string ItemDestroy = "item.destroy";
	public const string ItemSplit = "item.split";
	public const string ItemMerge = "item.merge";

	/// <summary>A line written below whatever was on a sheet, with the line itself: the journal knows whose hand it was.</summary>
	public const string PaperWrite = "paper.write";
	/// <summary>A blank on a printed form filled in, with what it says.</summary>
	public const string PaperFill = "paper.fill";
	/// <summary>A stamp put on a sheet, with which.</summary>
	public const string PaperStamp = "paper.stamp";
	/// <summary>A sheet held up for someone to read, without letting go of it.</summary>
	public const string PaperShow = "paper.show";

	/// <summary>A line drawn on a map laid out on a table, in grease pencil.</summary>
	public const string MapDraw = "map.draw";
	/// <summary>A word written on a map, with the word.</summary>
	public const string MapWrite = "map.write";
	/// <summary>A line or a word wiped off a map.</summary>
	public const string MapWipe = "map.wipe";
	/// <summary>One of a map's counters stood on it, moved, or put back in its box.</summary>
	public const string MapMove = "map.move";

	/// <summary>A recruit joins a faction, at its lowest rank, having accepted the recruiter's offer.</summary>
	public const string FactionJoin = "faction.join";
	/// <summary>A superior gives a member of their faction another rank, higher or lower.</summary>
	public const string FactionRank = "faction.rank";
	/// <summary>A superior discharges a member of their faction.</summary>
	public const string FactionDischarge = "faction.discharge";
	/// <summary>A member walks out of their faction of their own accord.</summary>
	public const string FactionLeave = "faction.leave";

	public const string OfferAccept = "offer.accept";
	public const string OfferRefuse = "offer.refuse";
	public const string OfferLapse = "offer.lapse";

	public const string DoorAssign = "door.assign";
	public const string DoorRelease = "door.release";

	/// <summary>A train stood at its station, with what it brought.</summary>
	public const string TrainArrive = "train.arrive";
	/// <summary>A train left its station, with what it took.</summary>
	public const string TrainDepart = "train.depart";
	/// <summary>Someone got out of a truck.</summary>
	public const string VehicleLeave = "vehicle.leave";
	/// <summary>A truck struck someone at speed, under whoever was at its wheel.</summary>
	public const string VehicleStrike = "vehicle.strike";

	/// <summary>Asked for faster than the budget allows.</summary>
	public const string RequestLimited = "request.limited";
	/// <summary>Sent in the name of someone else's pawn.</summary>
	public const string RequestForged = "request.forged";
	/// <summary>A request no honest client would make, such as for an item it does not have.</summary>
	public const string RequestInvalid = "request.invalid";

	public const string StaffCommand = "staff.command";
	public const string OperatorStaff = "operator.staff";
	public const string OperatorHurt = "operator.hurt";
	public const string OperatorRevive = "operator.revive";
	public const string OperatorRelease = "operator.release";
	public const string OperatorRestore = "operator.restore";
	public const string OperatorFaction = "operator.faction";
	public const string OperatorDoor = "operator.door";
	public const string OperatorTeleport = "operator.teleport";
	public const string OperatorTrain = "operator.train";

	/// <summary>An act on something in the world, or on an item, is journaled under its verb.</summary>
	public static string Verb( string verbId ) => $"verb.{verbId}";
}
