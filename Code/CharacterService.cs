#nullable enable

using System;
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

	/// <summary>Harm, at a place, from someone or from nothing. Harm alone never kills; at no health the character goes down.</summary>
	public void Damage( CharacterData character, int amount, CharacterData? by, string cause, Vector3 at )
	{
		var harm = Vitals.Damage( character, amount );
		if ( harm == Harm.None ) return;
		// Whoever goes down lets go of what was in hand, and stays down, where they fell, after a restart.
		if ( harm == Harm.Downed ) character.Equipped = null;
		Save( character );
		if ( harm == Harm.Downed )
		{
			Loaded( character )?.HostClose();
			Player.HostRecordAt( JournalKinds.CharacterDowned, Actor.Of( character ), at, by?.HolderLabel, data: new[] { ("cause", cause) } );
			Tell( character, "You are down. Someone may help you up, or not." );
		}
		Changed( character );
	}

	public Result Revive( CharacterData character )
	{
		var revived = Vitals.Revive( character );
		if ( !revived.Ok ) return revived;
		Roster.Save( character );
		Changed( character );
		Tell( character, "You are back on your feet, barely." );
		return revived;
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

	/// <summary>Time passes for someone who is down. Bleeding out is the game's policy.</summary>
	public void Bleed( CharacterData character, float seconds, Vector3 at )
	{
		if ( !character.IsDown || !Vitals.Bleed( character, seconds, _game.DownedSeconds ) ) return;
		if ( _game.BleedOutKills ) Die( character, at, "bled out", null );
		else Revive( character );
	}

	/// <summary>The character's look, as its body shows it.</summary>
	public static string LookOf( CharacterData character ) => Looks.Of( FactionDefinition.Find( character.Faction ) );

	/// <summary>
	/// The character dies where it is. What it carried stays where it fell, and the rest is the game's
	/// policy: for good, kept as dead so staff can restore it, or whole again at a spawn point.
	/// </summary>
	public void Die( CharacterData character, Vector3 at, string cause, CharacterData? by )
	{
		var loaded = Loaded( character );
		loaded?.HostClose();
		Player.HostRecordAt( JournalKinds.CharacterDeath, Actor.Of( character ), at, by?.HolderLabel, data: new[] { ("cause", cause), ("policy", _game.Death.ToString()) } );
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
		if ( _game.DeathDropsBelongings && character.Inventory.Items.Count > 0 ) LeaveBody( character, at );
		if ( permanent )
		{
			// Whatever no body took leaves the world on the record, not by vanishing with the character.
			_game.Transfers!.DestroyAll( Sinks.CharacterDied, character, actor );
			Door.HostReleaseOwnedBy( character.Id, actor );
		}
		Roster.Save( character );
		Sleeper.HostRefresh( character );
		if ( permanent ) Player.HostResendCharacters( _game, character.SteamId );
		loaded?.HostDied( character, permanent );
	}

	/// <summary>
	/// What the character carried stays where it fell, in a holder like any other. The body is the
	/// size of the inventory and every pile keeps its place, so all of it goes: nothing is left on
	/// someone who has died.
	/// </summary>
	private void LeaveBody( CharacterData character, Vector3 at )
	{
		var body = _game.Holders!.GetOrCreate( Guid.NewGuid(), HolderKinds.Corpse, "Body", character.Inventory.Width, character.Inventory.Height );
		body.Position = new[] { at.x, at.y, at.z };
		body.Look = LookOf( character );
		body.Character = character.Id;
		var moved = _game.Transfers!.MoveAll( character, body, Actor.Of( character ) );
		if ( !moved.Ok ) Log.Warning( $"[death] {character.HolderLabel} kept some of what it carried: {moved.Message}" );
		_game.SaveTogether( character, body );
		Corpse.Spawn( body );
	}

	/// <summary>
	/// Staff undo a death that broke the rules. The character gets up where its body lies, or where
	/// it fell if it left none, with whatever is still on the body. Anything already taken from it is
	/// for staff to move back.
	/// </summary>
	public Result Restore( CharacterData character, Actor by )
	{
		var restored = Roster.Restore( character, _game.Lives );
		if ( !restored.Ok ) return restored;
		if ( _game.Holders!.BodyOf( character.Id ) is { } body )
		{
			if ( body.Position is { Length: 3 } lying ) character.Position = lying;
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
