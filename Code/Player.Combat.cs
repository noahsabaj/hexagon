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

// Violence, from the side of whoever does it. What harm does to a character, going down, being helped
// up or finished, and the body left holding what it carried, is the CharacterService's. What death
// means is the game's choice, set on HexagonServices, and unless a game says otherwise it is final.
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
		// Whatever was hit takes the harm as the engine deals it, so a person standing or left lying,
		// and anything else that can be hurt, is struck the same way.
		var target = trace.Hit ? trace.GameObject?.GetComponentInParent<Component.IDamageable>() : null;
		var struck = (target as CharacterBody)?.HostCharacter?.HolderLabel;
		var damage = weapon?.Damage ?? UnarmedDamage;
		// A miss is an attack all the same: it is on record, with nothing hit.
		HostRecord( JournalKinds.CombatAttack, struck, data: new[] { ("weapon", weapon?.ResourceName ?? "unarmed"), ("damage", damage.ToString()), ("hit", struck is not null ? "person" : target is not null ? "thing" : "nothing") } );
		target?.OnDamage( new CharacterHarm { Attacker = GameObject, Damage = damage, Origin = from, Position = trace.HitPosition, Cause = weapon?.Title ?? "fists" } );
	}

	/// <summary>
	/// Host: this pawn's character has just died. For good, its player goes back to the menu and only the
	/// body is left where it fell; otherwise it wakes at the spawn point it was given, with nothing.
	/// </summary>
	internal void HostDied( CharacterData character, bool permanent )
	{
		var owner = Network.Owner;
		if ( permanent )
		{
			HostUnload();
			if ( owner is null || HexagonServices.Current is not { } game ) return;
			SendCharacterList( owner, game );
			Chat.Tell( owner, $"{character.Name} has died." );
			return;
		}
		HostTeleport( character.Position is { Length: 3 } at ? new Vector3( at[0], at[1], at[2] ) : HexagonServices.Current!.FindSpawn().Position );
		SendPrivateState();
		if ( owner is not null ) Chat.Tell( owner, "You wake somewhere else, with nothing." );
	}

	/// <summary>Host: every connection of the account that is in the menu is shown its list as it now is.</summary>
	internal static void HostResendCharacters( HexagonServices game, long steamId )
	{
		foreach ( var player in Game.ActiveScene.GetAllComponents<Player>().Where( value => value._character is null ).ToArray() )
			if ( player.Network.Owner is { } owner && SteamIdOf( owner ) == steamId ) player.SendCharacterList( owner, game );
	}
}
