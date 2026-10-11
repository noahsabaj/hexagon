#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

public enum DeathPolicy
{
	/// <summary>The character wakes at a spawn point, whole, without what it was carrying. For tests, and games that want it.</summary>
	Respawn,
	/// <summary>
	/// The character is dead, and its player returns to the menu. It is kept, dead, so staff can
	/// restore it if the death broke the rules; nothing else brings it back.
	/// </summary>
	Permanent
}

// Violence and its consequences. Hexagon supplies the pipeline: harm, going down, being helped up
// or finished, and a body that holds what the character carried. What death means is the game's
// choice, set on the game manager, and unless a game says otherwise it is final.
public sealed partial class Player
{
	public const int UnarmedDamage = 8;
	public const float UnarmedRange = 70f;
	public const float UnarmedCooldown = 0.8f;

	// Anyone can see these: someone lying on the ground, bound hands, a weapon in hand.
	[Sync( SyncFlags.FromHost )] public bool IsDown { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsRestrained { get; set; }
	[Sync( SyncFlags.FromHost )] public string HeldItemPath { get; set; } = string.Empty;

	/// <summary>Owner-side. How hurt someone else is cannot be read off them.</summary>
	public int Health { get; private set; }

	/// <summary>Unable to act on the world: no verbs, no item use, no attacks.</summary>
	public bool IsIncapable => IsDown || IsRestrained;

	private RealTimeSince _sinceAttack;
	// The faction whose look the body has on, so staff moving a character to another one changes what is seen.
	private string? _dressedAs;

	private ItemStack? HostHeld => _character?.Equipped is { } id ? _character.Inventory.Items.FirstOrDefault( value => value.Id == id ) : null;

	/// <summary>Host: brings what everyone can see in line with the character's document.</summary>
	private void HostSyncVitals()
	{
		IsDown = _character?.IsDown ?? false;
		IsRestrained = _character?.IsRestrained ?? false;
		HeldItemPath = HostHeld?.Definition ?? string.Empty;
		var faction = _character?.Faction;
		if ( faction == _dressedAs ) return;
		_dressedAs = faction;
		Look = faction is null ? string.Empty : Looks.Of( FactionDefinition.Find( faction ) );
	}

	[Rpc.Host]
	public void RequestAttack( Vector3 aim )
	{
		if ( !Authorize( nameof( RequestAttack ), out var caller, out var game ) ) return;
		var character = _character!;
		if ( IsIncapable )
		{
			HostRecord( JournalKinds.CombatAttack, ok: false, data: ("reason", IsDown ? "down" : "restrained") );
			return;
		}
		// An aim that is not a number would poison every trace and position it touched.
		if ( !float.IsFinite( aim.x ) || !float.IsFinite( aim.y ) || !float.IsFinite( aim.z ) )
		{
			HostInvalid( nameof( RequestAttack ), "aim" );
			return;
		}
		var weapon = HostHeld is { } held ? ItemDefinition.Find( held.Definition ) : null;
		if ( weapon is { Damage: <= 0 } ) weapon = null;
		// A swing still under way is not a new one. It is on record all the same, as a refusal: a test
		// that fires faster than the weapon allows, or a client that does, shows up in the journal. Holding
		// the button down is written as a count, not a line each time.
		if ( _sinceAttack < (weapon?.Cooldown ?? UnarmedCooldown) )
		{
			game.HostRefuse( caller, JournalKinds.CombatAttack, null, ("weapon", weapon?.ResourceName ?? "unarmed"), ("reason", "cooldown") );
			return;
		}
		_sinceAttack = 0;

		if ( weapon?.Ammo is { } ammo )
		{
			// A shot is a round leaving the world, like anything else that is used up.
			var round = character.Inventory.Items.FirstOrDefault( value => value.Definition == ammo.ResourcePath );
			if ( round is null )
			{
				HostRecord( JournalKinds.CombatAttack, ok: false, data: new[] { ("weapon", weapon.ResourceName), ("reason", "empty") } );
				Chat.Tell( caller, "Click. It is empty." );
				return;
			}
			game.Transfers!.Destroy( Sinks.Consumed, character, round.Id, Actor.Of( character ), count: 1 );
			game.Roster!.Save( character );
			SendPrivateState();
		}

		// Where the attacker stands is the host's belief. Where they aim is their claim, as it must be.
		var from = HostPosition + Vector3.Up * 60f;
		var direction = aim.IsNearZeroLength ? Vector3.Forward : aim.Normal;
		var trace = Scene.Trace.Ray( from, from + direction * (weapon?.Range ?? UnarmedRange) ).IgnoreGameObjectHierarchy( GameObject ).Run();
		var hit = trace.Hit ? trace.GameObject?.Root : null;
		var victim = hit?.GetComponent<Player>() is { _character: not null } person ? person : null;
		var lying = victim is null ? hit?.GetComponent<Sleeper>() : null;
		var struck = victim?._character?.HolderLabel ?? lying?.HostCharacter?.HolderLabel;
		var damage = weapon?.Damage ?? UnarmedDamage;
		// A miss is an attack all the same: it is on record, with nothing hit.
		HostRecord( JournalKinds.CombatAttack, struck, data: new[] { ("weapon", weapon?.ResourceName ?? "unarmed"), ("damage", damage.ToString()), ("hit", struck is null ? "nothing" : "person") } );
		victim?.HostDamage( damage, this, weapon?.Title ?? "fists" );
		lying?.HostDamage( damage, this, weapon?.Title ?? "fists" );
	}

	/// <summary>Host: harms this character. Harm alone never kills; at nothing it goes down.</summary>
	public void HostDamage( int amount, Player? by, string cause )
	{
		if ( !Networking.IsHost || _character is null ) return;
		var harm = Vitals.Damage( _character, amount );
		if ( harm == Harm.None ) return;
		// Whoever goes down lets go of what was in hand, and stays down, where they fell, after a restart.
		if ( harm == Harm.Downed ) _character.Equipped = null;
		HostSave();
		if ( harm == Harm.Downed )
		{
			HostClose();
			HostRecord( JournalKinds.CharacterDowned, by?._character?.HolderLabel, data: new[] { ("cause", cause) } );
			if ( Network.Owner is { } owner ) Chat.Tell( owner, "You are down. Someone may help you up, or not." );
		}
		HostSyncVitals();
		SendPrivateState();
	}

	public Result HostRevive()
	{
		if ( _character is null ) return Result.Fail( ErrorCode.NotFound, "There is nobody there." );
		var revived = Vitals.Revive( _character );
		if ( !revived.Ok ) return revived;
		HexagonServices.Current?.Roster?.Save( _character );
		HostSyncVitals();
		SendPrivateState();
		if ( Network.Owner is { } owner ) Chat.Tell( owner, "You are back on your feet, barely." );
		return revived;
	}

	/// <summary>Host: the character dies. What that means is the game manager's policy.</summary>
	public void HostDie( string cause, Player? by )
	{
		if ( !Networking.IsHost || _character is not { } character || HexagonServices.Current is not { } game ) return;
		HostClose();
		if ( HostDie( game, character, HostPosition, Look, cause, by?._character ) )
		{
			var owner = Network.Owner;
			// Dead, so nothing is left lying where it fell but the body.
			HostUnload();
			if ( owner is null ) return;
			SendCharacterList( owner, game );
			Chat.Tell( owner, $"{character.Name} has died." );
			return;
		}
		HostTeleport( character.Position is { Length: 3 } at ? new Vector3( at[0], at[1], at[2] ) : game.FindSpawn().Position );
		HostSyncVitals();
		SendPrivateState();
		if ( Network.Owner is { } waking ) Chat.Tell( waking, "You wake somewhere else, with nothing." );
	}

	/// <summary>
	/// Host: a character dies, whether in the city or left lying where its player went. What it
	/// carried stays where it fell, and the rest is the game manager's policy. True when the
	/// character is dead, kept where it fell; otherwise it is whole again, saved at a spawn point.
	/// </summary>
	internal static bool HostDie( HexagonServices game, CharacterData character, Vector3 at, string look, string cause, CharacterData? by )
	{
		HostRecordAt( JournalKinds.CharacterDeath, Actor.Of( character ), at, by?.HolderLabel, data: new[] { ("cause", cause), ("policy", game.Death.ToString()) } );
		var permanent = game.Death == DeathPolicy.Permanent;
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
			var spawn = game.FindSpawn().Position;
			character.Position = new[] { spawn.x, spawn.y, spawn.z };
		}
		// The body is saved with the death, as one change: a crash keeps both or neither.
		if ( game.DeathDropsBelongings && character.Inventory.Items.Count > 0 ) LeaveBody( game, character, at, look );
		if ( permanent )
		{
			// Whatever no body took leaves the world on the record, not by vanishing with the character.
			game.Transfers!.DestroyAll( Sinks.CharacterDied, character, actor );
			Door.HostReleaseOwnedBy( character.Id, actor );
		}
		game.Roster!.Save( character );
		Sleeper.HostRefresh( character );
		if ( permanent ) HostResendCharacters( game, character.SteamId );
		return permanent;
	}

	/// <summary>
	/// Host: staff undo a death that broke the rules. The character gets up where its body lies, or
	/// where it fell if it left none, with whatever is still on the body. Anything already taken from
	/// it is for staff to move back.
	/// </summary>
	internal static Result HostRestore( HexagonServices game, CharacterData character, Actor by )
	{
		var restored = game.Roster!.Restore( character, game.Lives );
		if ( !restored.Ok ) return restored;
		if ( game.Holders!.BodyOf( character.Id ) is { } body )
		{
			if ( body.Position is { Length: 3 } lying ) character.Position = lying;
			var taken = game.Transfers!.MoveAll( body, character, by );
			if ( !taken.Ok ) Log.Warning( $"[restore] {character.HolderLabel} could not take back all of its body: {taken.Message}" );
			game.SaveTogether( character, body );
			HostRefresh( body );
			Corpse.RemoveIfEmpty( body );
		}
		else game.Roster.Save( character );
		HostResendCharacters( game, character.SteamId );
		return restored;
	}

	/// <summary>Host: every connection of the account that is in the menu is shown its list as it now is.</summary>
	internal static void HostResendCharacters( HexagonServices game, long steamId )
	{
		foreach ( var player in Game.ActiveScene.GetAllComponents<Player>().Where( value => value._character is null ).ToArray() )
			if ( player.Network.Owner is { } owner && SteamIdOf( owner ) == steamId ) player.SendCharacterList( owner, game );
	}

	/// <summary>
	/// What the character carried stays where it fell, in a holder like any other. The body is the
	/// size of the inventory and every pile keeps its place, so all of it goes: nothing is left on
	/// someone who has died.
	/// </summary>
	private static void LeaveBody( HexagonServices game, CharacterData character, Vector3 at, string look )
	{
		var body = game.Holders!.GetOrCreate( Guid.NewGuid(), HolderKinds.Corpse, "Body", character.Inventory.Width, character.Inventory.Height );
		body.Position = new[] { at.x, at.y, at.z };
		body.Look = look;
		body.Character = character.Id;
		var moved = game.Transfers!.MoveAll( character, body, Actor.Of( character ) );
		if ( !moved.Ok ) Log.Warning( $"[death] {character.HolderLabel} kept some of what it carried: {moved.Message}" );
		game.SaveTogether( character, body );
		Corpse.Spawn( body );
	}

	/// <summary>Host: time passes for someone who is down. Bleeding out is the game manager's policy.</summary>
	private void HostVitalsTick( HexagonServices game )
	{
		if ( _character is not { IsDown: true } character || !Vitals.Bleed( character, Time.Delta, game.DownedSeconds ) ) return;
		if ( game.BleedOutKills ) HostDie( "bled out", null );
		else HostRevive();
	}
}
