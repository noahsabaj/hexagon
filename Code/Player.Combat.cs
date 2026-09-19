#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

public enum DeathPolicy
{
	/// <summary>The character wakes at a spawn point, whole, without what it was carrying.</summary>
	Respawn,
	/// <summary>The character is gone for good and its player returns to the menu.</summary>
	Permanent
}

// Violence and its consequences. Hexagon supplies the pipeline: harm, going down, being helped up
// or finished, and a body that holds what the character carried. What death means is the game's
// choice, set on the game manager.
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
	private RealTimeSince _sinceDown;

	private ItemStack? HostHeld => _character?.Equipped is { } id ? _character.Inventory.Items.FirstOrDefault( value => value.Id == id ) : null;

	/// <summary>Host: brings what everyone can see in line with the character's document.</summary>
	private void HostSyncVitals()
	{
		IsDown = _character?.IsDown ?? false;
		IsRestrained = _character?.IsRestrained ?? false;
		HeldItemPath = HostHeld?.Definition ?? string.Empty;
	}

	[Rpc.Host]
	public void RequestAttack( Vector3 aim )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null || IsIncapable ) return;
		var weapon = HostHeld is { } held ? ItemDefinition.Find( held.Definition ) : null;
		if ( weapon is { Damage: <= 0 } ) weapon = null;
		if ( _sinceAttack < (weapon?.Cooldown ?? UnarmedCooldown) ) return;
		_sinceAttack = 0;

		if ( weapon?.Ammo is { } ammo )
		{
			// A shot is a round leaving the world, like anything else that is used up.
			var round = _character.Inventory.Items.FirstOrDefault( value => value.Definition == ammo.ResourcePath );
			if ( round is null )
			{
				HostRecord( "combat.attack", ok: false, data: new[] { ("weapon", weapon.ResourceName), ("reason", "empty") } );
				Chat.Tell( caller, "Click. It is empty." );
				return;
			}
			game.Transfers!.Destroy( Sinks.Consumed, _character, round.Id, Actor.Of( _character ), count: 1 );
			game.Roster!.Save( _character );
			SendPrivateState();
		}

		// Where the attacker stands is the host's belief. Where they aim is their claim, as it must be.
		var from = HostPosition + Vector3.Up * 60f;
		var direction = aim.IsNearZeroLength ? Vector3.Forward : aim.Normal;
		var trace = Scene.Trace.Ray( from, from + direction * (weapon?.Range ?? UnarmedRange) ).IgnoreGameObjectHierarchy( GameObject ).Run();
		var victim = trace.Hit ? trace.GameObject?.Root.GetComponent<Player>() : null;
		if ( victim?._character is null ) victim = null;
		var damage = weapon?.Damage ?? UnarmedDamage;
		HostRecord( "combat.attack", victim?._character!.HolderLabel, victim is not null,
			("weapon", weapon?.ResourceName ?? "unarmed"), ("damage", damage.ToString()) );
		victim?.HostDamage( damage, this, weapon?.Title ?? "fists" );
	}

	/// <summary>Host: harms this character. Harm alone never kills; at nothing it goes down.</summary>
	public void HostDamage( int amount, Player? by, string cause )
	{
		if ( !Networking.IsHost || _character is null ) return;
		var harm = Vitals.Damage( _character, amount );
		if ( harm == Harm.None ) return;
		GameManager.Instance?.Roster?.Save( _character );
		if ( harm == Harm.Downed )
		{
			_sinceDown = 0;
			_character.Equipped = null;
			HostClose();
			HostRecord( "character.downed", by?._character?.HolderLabel, data: new[] { ("cause", cause) } );
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
		GameManager.Instance?.Roster?.Save( _character );
		HostSyncVitals();
		SendPrivateState();
		if ( Network.Owner is { } owner ) Chat.Tell( owner, "You are back on your feet, barely." );
		return revived;
	}

	/// <summary>Host: the character dies. What that means is the game manager's policy.</summary>
	public void HostDie( string cause, Player? by )
	{
		if ( !Networking.IsHost || _character is not { } character || GameManager.Instance is not { } game ) return;
		HostRecord( "character.death", by?._character?.HolderLabel, data: new[] { ("cause", cause), ("policy", game.Death.ToString()) } );
		HostClose();
		if ( game.DeathDropsBelongings && (character.Inventory.Items.Count > 0 || character.Tokens > 0) ) HostLeaveBody( game, character );

		if ( game.Death == DeathPolicy.Permanent )
		{
			var owner = Network.Owner;
			HostUnload();
			game.Transfers!.DestroyAll( Sinks.CharacterDeleted, character, Actor.Of( character ) );
			game.Roster!.Delete( character.SteamId, character.Id );
			if ( owner is null ) return;
			SendCharacterList( owner, game );
			Chat.Tell( owner, $"{character.Name} has died." );
			return;
		}

		Vitals.Restore( character );
		character.Equipped = null;
		game.Roster!.Save( character );
		HostTeleport( game.FindSpawn().Position );
		HostSyncVitals();
		SendPrivateState();
		if ( Network.Owner is { } waking ) Chat.Tell( waking, "You wake somewhere else, with nothing." );
	}

	/// <summary>What the character carried stays where it fell, in a holder like any other.</summary>
	private void HostLeaveBody( GameManager game, CharacterData character )
	{
		var at = HostPosition;
		var body = game.Holders!.GetOrCreate( Guid.NewGuid(), HolderKinds.Corpse, "Body", character.Inventory.Width, character.Inventory.Height );
		body.Position = new[] { at.x, at.y, at.z };
		var actor = Actor.Of( character );
		foreach ( var item in character.Inventory.Items.ToArray() ) game.Transfers!.Move( character, body, item.Id, actor );
		if ( character.Tokens > 0 ) game.Transfers!.MoveTokens( character, body, character.Tokens, actor );
		game.Roster!.Save( character );
		game.Holders.Save( body );
		Corpse.Spawn( body );
	}

	private void HostVitalsTick( GameManager game )
	{
		if ( _character is not { IsDown: true } || game.DownedSeconds <= 0 || _sinceDown < game.DownedSeconds ) return;
		if ( game.BleedOutKills ) HostDie( "bled out", null );
		else HostRevive();
	}
}
