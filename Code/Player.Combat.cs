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

// Violence, from the side of whoever does it. The host judges every blow: where the attacker stands
// and how high their eyes are is its own belief, and only the aim is theirs. A shot is traced against
// the hitboxes of the bodies in its way, which say where on the body it lands. What harm does to a
// character, wounds, going down, being helped up or finished, and the body left holding what it
// carried, is the CharacterService's. What death means is the game's choice, set on HexagonServices,
// and unless a game says otherwise it is final.
public sealed partial class Player
{
	public const int UnarmedDamage = 8;
	public const float UnarmedRange = 70f;
	public const float UnarmedCooldown = 0.8f;

	// Anyone can see these: someone lying on the ground, bound hands, a weapon in hand, a limp.
	[Sync( SyncFlags.FromHost )] public bool IsDown { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsRestrained { get; set; }
	[Sync( SyncFlags.FromHost )] public string HeldItemPath { get; set; } = string.Empty;
	/// <summary>Walking on a wounded leg: slowly, and never running.</summary>
	[Sync( SyncFlags.FromHost )] public bool IsLimping { get; set; }
	/// <summary>Breathing hard, badly hurt, as anyone close can hear. Someone down breathes from their body on the ground.</summary>
	[Sync( SyncFlags.FromHost )] public bool IsBreathingHard { get; set; }

	/// <summary>Owner-side. How hurt someone else is cannot be read off them.</summary>
	public int Health { get; private set; }

	/// <summary>Unable to act on the world: no verbs, no item use, no attacks.</summary>
	public bool IsIncapable => IsDown || IsRestrained;

	private RealTimeSince _sinceAttack;

	/// <summary>Host: what this character holds in hand.</summary>
	internal ItemStack? HostHeld => _character?.Equipped is { } id ? _character.Inventory.Items.FirstOrDefault( value => value.Id == id ) : null;

	/// <summary>Host: brings what everyone can see in line with the character's document.</summary>
	private void HostSyncVitals()
	{
		IsDown = _character?.IsDown ?? false;
		IsRestrained = _character?.IsRestrained ?? false;
		HeldItemPath = HostHeld?.Definition ?? string.Empty;
		var limping = _character is not null && Wounds.Limps( _character );
		if ( limping && !IsLimping ) _sinceLimping = 0;
		IsLimping = limping;
		IsBreathingHard = _character is { IsDown: false } && Vitals.BreathesHard( _character );
		// The body and what is worn on it: it changes only when someone puts something on or takes it off.
		var look = _character is null ? string.Empty : Looks.Of( _character );
		if ( Look != look ) Look = look;
		HostTellLyingIn();
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
		// Both hands are under someone carried over the shoulder.
		if ( !Holds.LeavesAHand( HostHolding ) )
		{
			HostRecord( JournalKinds.CombatAttack, ok: false, data: ("reason", "hands") );
			Chat.Tell( caller, Holds.HandsFull );
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

		// Where the attacker stands is the host's belief, and so is how high their eyes are, standing or
		// ducking. Where they aim is their claim, as it must be.
		var from = HostPosition + Vector3.Up * HostEyeHeight;
		var direction = aim.IsNearZeroLength ? Vector3.Forward : aim.Normal;
		var trace = Scene.Trace.Ray( from, from + direction * (weapon?.Range ?? UnarmedRange) )
			.UseHitboxes()
			// A body's capsule is only for walking into: a shot finds the body itself.
			.WithoutTags( "capsule" )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		// Whatever was hit takes the harm as the engine deals it, so a person standing or left lying,
		// and anything else that can be hurt, is struck the same way.
		var target = trace.Hit ? trace.GameObject?.GetComponentInParent<Component.IDamageable>() : null;
		var struck = (target as CharacterBody)?.HostCharacter?.HolderLabel;
		var damage = weapon?.Damage ?? UnarmedDamage;
		// Where on a body: the hitbox struck, or the part of a limp body, which is named for its bone.
		var place = trace.Hitbox is { } hitbox ? Places.OfTags( hitbox.Tags.TryGetAll() )
			: trace.GameObject is { } part ? Places.OfBone( part.Name ) : Places.Body;
		// A miss is an attack all the same: it is on record, with nothing hit.
		var hit = struck is not null ? "person" : target is not null ? "thing" : "nothing";
		HostRecord( JournalKinds.CombatAttack, struck, data: struck is null
			? new[] { ("weapon", weapon?.ResourceName ?? "unarmed"), ("damage", damage.ToString()), ("hit", hit) }
			: new[] { ("weapon", weapon?.ResourceName ?? "unarmed"), ("damage", damage.ToString()), ("hit", hit), ("place", place) } );
		// Anything loose that it strikes is pushed, a limp body as much as a crate.
		if ( trace.Body is { MotionEnabled: true } loose ) loose.ApplyImpulseAt( trace.HitPosition, direction * loose.Mass * Bodies.Push );
		target?.OnDamage( new CharacterHarm
		{
			Attacker = GameObject, Damage = damage, Origin = from, Position = trace.HitPosition, Hitbox = trace.Hitbox,
			Cause = weapon?.Title ?? "fists", Place = place, Wounding = weapon is not null, Lethal = weapon?.Lethal ?? false
		} );
	}

	/// <summary>Host: how high the character's eyes are above where it stands, standing or ducking.</summary>
	private float HostEyeHeight => Controller is { } controller ? controller.CurrentHeight - controller.EyeDistanceFromTop : EyeHeight;

	/// <summary>
	/// Host: this pawn's character has just died. For good, its player goes back to the menu and only the
	/// body is left where it fell; otherwise it wakes at the spawn point it was given, with nothing.
	/// </summary>
	internal void HostDied( CharacterData character, bool permanent )
	{
		HostLetGo( "died" );
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
