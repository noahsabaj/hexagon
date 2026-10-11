#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What carries a character in the world: a player's pawn, or a <see cref="Sleeper"/> left where the
/// player went. Each tells its <see cref="CharacterBody"/> who it is and what anyone can see of it.
/// </summary>
public interface IEmbodiment
{
	/// <summary>A face: the same person is the same id tomorrow. It says nothing about who they are.</summary>
	Guid CharacterId { get; }
	bool IsDown { get; }
	bool IsRestrained { get; }
	/// <summary>What an onlooker sees of them.</summary>
	string CharacterDescription { get; }
	/// <summary>Whether this client shows them, and so can look at them and act on them.</summary>
	bool IsShownHere { get; }
	/// <summary>Host: where the character is, as the host believes.</summary>
	Vector3 HostPosition { get; }
	/// <summary>Host: the character this body carries now, or null.</summary>
	CharacterData? HostCharacter { get; }
}

/// <summary>
/// A person as other people meet them, whichever body carries them. It is what is looked at, acted
/// on and harmed, by the engine's own pressing and damage, so the same things can be done to someone
/// standing in front of you and to someone whose player left them lying in the street. What each act
/// does is the host's <see cref="CharacterService"/>.
/// </summary>
[Title( "Hexagon Character Body" ), Category( "Hexagon" ), Icon( "accessibility_new" )]
public sealed class CharacterBody : Component, Component.IPressable, Component.IDamageable, IVerbTarget
{
	private IEmbodiment? Embodiment => GetComponent<IEmbodiment>();

	public Guid CharacterId => Embodiment?.CharacterId ?? Guid.Empty;
	public bool IsDown => Embodiment?.IsDown ?? false;
	public bool IsRestrained => Embodiment?.IsRestrained ?? false;
	public string CharacterDescription => Embodiment?.CharacterDescription ?? string.Empty;

	/// <summary>Host: the character this body carries now, or null.</summary>
	public CharacterData? HostCharacter => Networking.IsHost ? Embodiment?.HostCharacter : null;

	public IReadOnlyList<Verb> Verbs => PersonVerbs.For( IsDown, IsRestrained, HexagonServices.Current?.FinishRequires );

	float IVerbTarget.Reach => Player.PersonReach;

	// Telling someone your name, or tying their hands, is never what bumping into them with a key does.
	bool IVerbTarget.UseOpensMenu => true;

	string? IVerbTarget.JournalSubject => $"character:{CharacterId:N}";

	protected override void OnFixedUpdate()
	{
		// Time passes for someone lying in the street, whether or not their player is watching.
		if ( Networking.IsHost && HostCharacter is { IsDown: true } character && HexagonServices.Current?.Characters is { } characters )
			characters.Bleed( character, Time.Delta, Embodiment!.HostPosition );
	}

	bool IPressable.CanPress( IPressable.Event e ) => Embodiment?.IsShownHere == true;

	bool IPressable.Press( IPressable.Event e )
	{
		if ( Player.Local is { } local ) local.MenuTarget = this;
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		Player.Local is null || Embodiment?.IsShownHere != true ? null : new IPressable.Tooltip( Player.Local.LabelFor( this ), "person", CharacterDescription );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( HostCharacter is not { } character || actor.HostCharacter is not { } other || character == other || HexagonServices.Current?.Characters is not { } characters )
			return Result.Fail( ErrorCode.NotFound, "There is nobody there." );
		switch ( verb.Id )
		{
			case VerbIds.PersonIntroduce: return characters.Introduce( other, character );
			case VerbIds.PersonRevive: return characters.Revive( character );
			case VerbIds.PersonSearch:
				// Searching is opening someone's belongings as a holder, for as long as they cannot stop it.
				if ( !character.IsDown && !character.IsRestrained ) return Result.Fail( ErrorCode.Conflict, "They would not let you." );
				actor.HostOpen( this, character, "Belongings", onlyWhile: () => this.IsValid() && HostCharacter == character && (character.IsDown || character.IsRestrained) );
				characters.Tell( character, "Someone is going through your belongings." );
				return Result.Success();
			case VerbIds.PersonRestrain: return characters.Restrain( character, other );
			case VerbIds.PersonRelease: return characters.Release( character );
			// What is offered is chosen from the inventory, and sent with RequestOffer.
			case VerbIds.PersonOffer: return Result.Fail( ErrorCode.Invalid, "Choose what to offer from your inventory." );
			case VerbIds.PersonFinish:
				var can = Vitals.CanFinish( character );
				if ( can.Ok ) characters.Die( character, Embodiment!.HostPosition, "finished", other );
				return can;
			default: return Result.Fail( ErrorCode.Invalid, "That cannot be done." );
		}
	}

	/// <summary>Host: harm, from a weapon, a fist, or anything else the engine deals damage with.</summary>
	void IDamageable.OnDamage( in DamageInfo damage )
	{
		if ( !Networking.IsHost || HostCharacter is not { } character || HexagonServices.Current?.Characters is not { } characters ) return;
		var by = damage.Attacker?.GetComponent<Player>()?.HostCharacter;
		var cause = damage is CharacterHarm harm ? harm.Cause : damage.Weapon?.Name ?? "harm";
		characters.Damage( character, (int)MathF.Ceiling( damage.Damage ), by, cause, Embodiment!.HostPosition );
	}
}

/// <summary>Damage that says in words what did it, for the journal.</summary>
public sealed class CharacterHarm : DamageInfo
{
	public string Cause { get; set; } = "harm";
}
