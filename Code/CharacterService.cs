#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// Host: everything that is done to a character, whichever body it has. A character in the city is
/// in its pawn, one whose player left while it was down or bound is a <see cref="Sleeper"/>, and one
/// that is neither is only its document; staff reach all three. Each act is decided here once, saved,
/// and shown wherever the character is: to its player, to whoever is looking into what it carries,
/// and on the body others see.
/// </summary>
public sealed class CharacterService
{
	private readonly HexagonServices _game;

	public CharacterService( HexagonServices game ) => _game = game;

	private CharacterRoster Roster => _game.Roster!;

	/// <summary>The character's player, if it is in the city.</summary>
	private static Player? Loaded( CharacterData character ) => Player.HostLoaded( character.Id );

	/// <summary>What carries the character in the world now: a body of its own, lying or standing, or else its pawn. Null when nothing does.</summary>
	private static IEmbodiment? BodyOf( CharacterData character ) =>
		(IEmbodiment?)Sleeper.Find( character.Id ) ?? Loaded( character );

	/// <summary>Tells the character's player something, if they are here to be told.</summary>
	public void Tell( CharacterData character, string message )
	{
		if ( Loaded( character )?.Network.Owner is { } owner ) Chat.Tell( owner, message );
	}

	/// <summary>The character changed: its player, whoever is looking into what it carries, and its body are shown it.</summary>
	public void Changed( CharacterData character ) => Player.HostRefresh( character );

	/// <summary>Saves the character, with where it stands if it is in the city.</summary>
	private void Save( CharacterData character )
	{
		if ( Loaded( character ) is { } player ) player.HostSave();
		else Roster.Save( character );
	}

	/// <summary>
	/// Harm, at a place on the body, from someone or from nothing. A weapon wounds an arm or a leg, and
	/// a wounded arm drops what it held. A weapon made to kill, to the head, kills. Otherwise harm never
	/// kills: at no health the character goes down, and its body falls limp from where it stood.
	/// </summary>
	public void Damage( CharacterData character, Blow blow, CharacterData? by, string cause, Vector3 at, Impact? impact = null )
	{
		var struck = Vitals.Strike( character, blow );
		if ( struck.Harm == Harm.None ) return;
		if ( struck.Harm == Harm.Killed )
		{
			Die( character, at, cause, by, blow.Place, impact );
			return;
		}
		var loaded = Loaded( character );
		if ( struck.Wound is { } wound )
		{
			Player.HostRecordAt( JournalKinds.CharacterWounded, Actor.Of( character ), at, by?.HolderLabel, data: new[] { ("place", wound), ("cause", cause) } );
			Tell( character, $"You are hit in the {wound}." );
			// What a wounded arm held falls to the ground, where anyone may pick it up.
			if ( Places.KindOf( wound ) == Places.Arm ) loaded?.HostDropHeld();
		}
		// Whoever goes down lets go of what was in hand, and stays down, where they fell, after a restart.
		if ( struck.Harm == Harm.Downed ) character.Equipped = null;
		Save( character );
		var body = BodyOf( character );
		if ( struck.Harm == Harm.Downed )
		{
			// Someone sitting falls out first, beside the vehicle, rather than lying inside it.
			loaded?.HostLeaveSeat();
			loaded?.HostClose();
			Player.HostRecordAt( JournalKinds.CharacterDowned, Actor.Of( character ), at, by?.HolderLabel, data: new[] { ("cause", cause), ("place", blow.Place) } );
			Tell( character, "You are down. Someone may help you up, or not." );
			var from = body?.Renderer;
			Sleeper.HostFall( character, from, new Transform( body?.HostPosition ?? at, Rotation.FromYaw( from?.WorldRotation.Yaw() ?? 0f ) ), impact );
		}
		else if ( impact is { } blowAt && (body as Component)?.GetComponent<BodyCues>() is { } cues )
			cues.Flinch( blowAt.Position, blowAt.Direction, Math.Clamp( blow.Amount / 40f, 0.2f, 1f ) );
		Changed( character );
	}

	/// <summary>Helped up, the character stands where its body lies, wounds and all.</summary>
	public Result Revive( CharacterData character )
	{
		var standsAt = Sleeper.Find( character.Id ) is { IsLimp: true } body ? body.HostGroundPoint : (Vector3?)null;
		var revived = Vitals.Revive( character );
		if ( !revived.Ok ) return revived;
		if ( standsAt is { } at )
		{
			if ( Loaded( character ) is { } player ) player.HostTeleport( at );
			else character.Position = new[] { at.x, at.y, at.z };
		}
		Save( character );
		Changed( character );
		Tell( character, "You are back on your feet, barely." );
		return revived;
	}

	/// <summary>
	/// Someone treats a character with something they carry, which is used up: on themselves, or on
	/// someone else from their hand. It closes one wound of the kinds it treats, the oldest first, and
	/// gives back the health it heals. On someone it can do nothing for, it is not used up.
	/// </summary>
	public Result Treat( CharacterData patient, CharacterData medic, ItemStack item, ItemDefinition definition )
	{
		var treated = Vitals.Treat( patient, definition.Treats, definition.Heals );
		if ( !treated.Ok ) return treated.ToResult();
		var used = _game.Transfers!.Destroy( Sinks.Consumed, medic, item.Id, Actor.Of( medic ), count: 1 );
		if ( !used.Ok ) return used;
		// The bandage used up and the wound it closed are one change: a crash keeps both or neither.
		_game.SaveTogether( medic, patient );
		var (wound, healed) = treated.Value;
		Player.HostRecordAt( JournalKinds.CharacterTreated, Actor.Of( medic ), BodyOf( patient )?.HostPosition, patient.HolderLabel,
			data: new[] { ("item", definition.ResourceName), ("wound", wound ?? "none"), ("healed", healed.ToString()) } );
		Changed( medic );
		if ( patient != medic ) Changed( patient );
		if ( wound is not null ) Tell( patient, $"Your {wound} is treated." );
		return Result.Success();
	}

	/// <summary>Binds the character's hands with something the binder carries, which is used up: a restraint is cut off, not untied.</summary>
	public Result Restrain( CharacterData character, CharacterData by )
	{
		if ( character.IsRestrained ) return Result.Fail( ErrorCode.Conflict, "They are already restrained." );
		var tie = by.Inventory.Items.FirstOrDefault( item => ItemDefinition.Find( item.Definition )?.Grants.Contains( Capability.Restrain ) == true );
		if ( tie is null ) return Result.Fail( ErrorCode.Denied, "You have nothing to bind them with." );
		_game.Transfers!.Destroy( Sinks.Consumed, by, tie.Id, Actor.Of( by ), count: 1 );
		character.IsRestrained = true;
		character.Equipped = null;
		// The tie used up and the hands it binds are one change: a crash keeps both or neither.
		_game.SaveTogether( by, character );
		Loaded( character )?.HostClose();
		Changed( by );
		Changed( character );
		Tell( character, "Your hands are bound." );
		return Result.Success();
	}

	/// <summary>Frees the character's hands, whether someone cut the tie or staff did.</summary>
	public Result Release( CharacterData character )
	{
		if ( !character.IsRestrained ) return Result.Fail( ErrorCode.Conflict, "Their hands are not bound." );
		character.IsRestrained = false;
		Roster.Save( character );
		Changed( character );
		Tell( character, "Your hands are free." );
		return Result.Success();
	}

	/// <summary>One character tells another its name.</summary>
	public Result Introduce( CharacterData teller, CharacterData listener )
	{
		var told = Recognition.Introduce( teller, listener );
		if ( !told.Ok ) return told;
		Roster.Save( listener );
		Changed( listener );
		Tell( listener, $"{Recognition.Stranger( teller.Description )} is {teller.Name}." );
		Tell( teller, "You introduce yourself." );
		return told;
	}

	/// <summary>A character's faction, that faction's ranks as the rules weigh them, and where the character stands among them: -1 for nowhere.</summary>
	public static (FactionDefinition? Faction, IReadOnlyList<RankRule> Ranks, int Standing) StandingOf( CharacterData character )
	{
		if ( FactionDefinition.Find( character.Faction ) is not { } faction ) return (null, Array.Empty<RankRule>(), -1);
		var ranks = faction.Rules;
		return (faction, ranks, Logic.Ranks.Standing( ranks, character.Rank ));
	}

	/// <summary>
	/// A recruit takes up a recruiter's offer and joins the recruiter's faction at its lowest rank. Only
	/// the record changes: papers and a uniform are the recruiter's to fill in and hand over.
	/// </summary>
	public Result Enlist( CharacterData recruit, CharacterData recruiter, FactionDefinition faction )
	{
		var (theirs, ranks, standing) = StandingOf( recruiter );
		if ( theirs != faction ) return Result.Fail( ErrorCode.Denied, "They no longer belong to it." );
		var can = Logic.Ranks.ToEnlist( ranks, standing, recruit.Faction.Length > 0 );
		if ( !can.Ok ) return can;
		recruit.Faction = faction.ResourcePath;
		recruit.Rank = ranks[0].Title;
		Save( recruit );
		Changed( recruit );
		Player.HostRecordAt( JournalKinds.FactionJoin, Actor.Of( recruiter ), BodyOf( recruit )?.HostPosition, recruit.HolderLabel,
			data: new[] { ("faction", faction.ResourceName), ("rank", recruit.Rank) } );
		Tell( recruit, $"You join {faction.Title} as {Ranks.Article( recruit.Rank )}." );
		Tell( recruiter, $"They join {faction.Title} as {Ranks.Article( recruit.Rank )}." );
		return Result.Success();
	}

	/// <summary>A superior gives a member below them another rank, higher or lower. The insignia that shows it is theirs to hand over.</summary>
	public Result SetRank( CharacterData member, CharacterData superior, string title )
	{
		var (faction, ranks, standing) = StandingOf( superior );
		var (theirs, _, place) = StandingOf( member );
		var to = Logic.Ranks.IndexOf( ranks, title );
		var can = Logic.Ranks.ToSetRank( ranks, standing, faction is not null && theirs == faction, place, to );
		if ( !can.Ok ) return can;
		var from = member.Rank;
		member.Rank = ranks[to].Title;
		Save( member );
		Changed( member );
		Player.HostRecordAt( JournalKinds.FactionRank, Actor.Of( superior ), BodyOf( member )?.HostPosition, member.HolderLabel,
			data: new[] { ("faction", faction!.ResourceName), ("from", from), ("to", member.Rank) } );
		Tell( member, $"You are {(to > place ? "promoted" : "demoted")} to {member.Rank}." );
		var insignia = faction.Rank( member.Rank )?.Insignia;
		Tell( superior, $"You make them {Ranks.Article( member.Rank )}." + (insignia is null ? string.Empty : $" {Ranks.Article( member.Rank, capital: true )} wears the {insignia.Title.ToLowerInvariant()}.") );
		return Result.Success();
	}

	/// <summary>A superior discharges a member below them. What they were issued stays with them, for the faction to ask back.</summary>
	public Result Discharge( CharacterData member, CharacterData superior )
	{
		var (faction, ranks, standing) = StandingOf( superior );
		var (theirs, _, place) = StandingOf( member );
		var can = Logic.Ranks.ToDischarge( ranks, standing, faction is not null && theirs == faction, place );
		if ( !can.Ok ) return can;
		var rank = member.Rank;
		member.Faction = string.Empty;
		member.Rank = string.Empty;
		Save( member );
		Changed( member );
		Player.HostRecordAt( JournalKinds.FactionDischarge, Actor.Of( superior ), BodyOf( member )?.HostPosition, member.HolderLabel,
			data: new[] { ("faction", faction!.ResourceName), ("rank", rank) } );
		Tell( member, $"You are discharged from {faction.Title}." );
		Tell( superior, "You discharge them." );
		return Result.Success();
	}

	/// <summary>A member walks out of their faction of their own accord. Nothing they were issued leaves them by it.</summary>
	public Result Leave( CharacterData member )
	{
		if ( member.Faction.Length == 0 ) return Result.Fail( ErrorCode.Conflict, "You belong to no faction." );
		// A faction the game no longer has can still be walked out of.
		var faction = FactionDefinition.Find( member.Faction );
		var (name, rank) = (faction?.ResourceName ?? member.Faction, member.Rank);
		member.Faction = string.Empty;
		member.Rank = string.Empty;
		Save( member );
		Changed( member );
		Player.HostRecordAt( JournalKinds.FactionLeave, Actor.Of( member ), BodyOf( member )?.HostPosition, member.HolderLabel,
			data: new[] { ("faction", name), ("rank", rank) } );
		Tell( member, $"You walk out of {faction?.Title ?? "your faction"}." );
		return Result.Success();
	}

	/// <summary>Time passes for someone who is down. Bleeding out is the game's policy.</summary>
	public void Bleed( CharacterData character, float seconds, Vector3 at )
	{
		if ( !character.IsDown || !Vitals.Bleed( character, seconds, _game.DownedSeconds ) ) return;
		if ( _game.BleedOutKills ) Die( character, at, "bled out", null );
		else Revive( character );
	}

	/// <summary>The character's look, as its body shows it.</summary>
	public static string LookOf( CharacterData character ) => Looks.Of( character );

	/// <summary>A body's look: the body its character was made with, and whatever it still wears.</summary>
	public string LookOf( HolderData body ) =>
		Looks.Of( body.Character is { } id && Roster.Find( id ) is { } character ? character.Body : new List<string>(), body.Inventory );

	/// <summary>
	/// The character dies where it is. What it carried stays where it fell, on its body, and the rest is
	/// the game's policy: for good, kept as dead so staff can restore it, or whole again at a spawn point.
	/// </summary>
	public void Die( CharacterData character, Vector3 at, string cause, CharacterData? by, string? place = null, Impact? impact = null )
	{
		var loaded = Loaded( character );
		// Someone dying in a vehicle falls out of it, and lies beside it.
		if ( loaded?.HostLeaveSeat() is { } outside ) at = outside;
		// Someone dying in a truck's bed stays lying in it, as a body.
		var lyingIn = character.LyingIn;
		character.LyingIn = null;
		// The body it dies in is the one that falls, before anything is taken from it.
		var from = BodyOf( character )?.Renderer;
		loaded?.HostClose();
		var data = new List<(string, string)> { ("cause", cause), ("policy", _game.Death.ToString()) };
		if ( place is not null ) data.Add( ("place", place) );
		Player.HostRecordAt( JournalKinds.CharacterDeath, Actor.Of( character ), at, by?.HolderLabel, data: data.ToArray() );
		var permanent = _game.Death == DeathPolicy.Permanent;
		var actor = Actor.Of( character );
		if ( permanent )
		{
			// Kept where it fell: staff restoring it would have it get up here.
			character.Position = new[] { at.x, at.y, at.z };
			Vitals.Die( character, DateTimeOffset.UtcNow );
		}
		else
		{
			Vitals.Respawn( character );
			character.Equipped = null;
			var spawn = _game.FindSpawn().Position;
			character.Position = new[] { spawn.x, spawn.y, spawn.z };
		}
		// The body is saved with the death, as one change: a crash keeps both or neither.
		if ( _game.DeathDropsBelongings && character.Inventory.Items.Count > 0 ) LeaveBody( character, at, from, impact, lyingIn );
		if ( permanent )
		{
			// Whatever no body took leaves the world on the record, not by vanishing with the character.
			_game.Transfers!.DestroyAll( Sinks.CharacterDied, character, actor );
			Property.HostReleaseOwnedBy( character.Id, actor, "died" );
		}
		Roster.Save( character );
		Sleeper.HostRefresh( character );
		if ( permanent ) Player.HostResendCharacters( _game, character.SteamId );
		loaded?.HostDied( character, permanent );
	}

	/// <summary>
	/// What the character carried stays where it fell, in a holder like any other. The body is the
	/// size of the inventory and every pile keeps its place, so all of it goes: nothing is left on
	/// someone who has died. One that died lying in a truck's bed lies there still, out of the world.
	/// </summary>
	private void LeaveBody( CharacterData character, Vector3 at, SkinnedModelRenderer? from, Impact? impact, Guid? lyingIn )
	{
		var body = _game.Holders!.GetOrCreate( Guid.NewGuid(), HolderKinds.Corpse, "Body", character.Inventory.Width, character.Inventory.Height );
		body.Position = new[] { at.x, at.y, at.z };
		body.LyingIn = lyingIn;
		body.Character = character.Id;
		var moved = _game.Transfers!.MoveAll( character, body, Actor.Of( character ) );
		if ( !moved.Ok ) Log.Warning( $"[death] {character.HolderLabel} kept some of what it carried: {moved.Message}" );
		// The body still wears what the character wore.
		body.Look = LookOf( body );
		_game.SaveTogether( character, body );
		if ( lyingIn is null ) Corpse.Spawn( body, from, impact );
	}

	/// <summary>
	/// Staff undo a death that broke the rules. The character gets up where its body lies, or behind
	/// the truck whose bed it lies in, or where it fell if it left none, with whatever is still on the
	/// body. Anything already taken from it is for staff to move back.
	/// </summary>
	public Result Restore( CharacterData character, Actor by )
	{
		var restored = Roster.Restore( character, _game.Lives );
		if ( !restored.Ok ) return restored;
		if ( _game.Holders!.BodyOf( character.Id ) is { } body )
		{
			if ( body.LyingIn is { } bedId && Bed.HostFind( bedId ) is { } bed )
			{
				var behind = bed.HostBehind();
				character.Position = new[] { behind.x, behind.y, behind.z };
			}
			else
			{
				Corpse.HostKeepPosition( body );
				if ( body.Position is { Length: 3 } lying ) character.Position = lying;
			}
			var taken = _game.Transfers!.MoveAll( body, character, by );
			if ( !taken.Ok ) Log.Warning( $"[restore] {character.HolderLabel} could not take back all of its body: {taken.Message}" );
			_game.SaveTogether( character, body );
			Player.HostRefresh( body );
			Corpse.RemoveIfEmpty( body );
		}
		else Roster.Save( character );
		Player.HostResendCharacters( _game, character.SteamId );
		return restored;
	}
}
