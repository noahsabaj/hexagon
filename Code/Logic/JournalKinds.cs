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

	public const string OfferAccept = "offer.accept";
	public const string OfferRefuse = "offer.refuse";
	public const string OfferLapse = "offer.lapse";

	public const string DoorAssign = "door.assign";
	public const string DoorRelease = "door.release";

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

	/// <summary>An act on something in the world, or on an item, is journaled under its verb.</summary>
	public static string Verb( string verbId ) => $"verb.{verbId}";
}
