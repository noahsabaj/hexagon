#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Everything a client can ask of the host. Each request re-derives who is asking from the
// connection, validates its arguments on the host, saves before replying, and is journaled,
// refusals included. Acting on anything in the world goes through RequestAct and nowhere else.
public sealed partial class Player
{
	[Rpc.Host]
	public void RequestCharacters()
	{
		if ( Authorize( nameof( RequestCharacters ), out var caller, out var game, needsCharacter: false ) ) SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestCreateCharacter( string name, string description, string factionPath )
	{
		if ( !Authorize( nameof( RequestCreateCharacter ), out var caller, out var game, needsCharacter: false ) ) return;
		if ( FactionDefinition.Find( factionPath ) is not { } faction )
		{
			HostRecord( JournalKinds.CharacterCreate, ok: false, data: new[] { ("reason", "faction"), ("faction", Clip( factionPath ?? string.Empty )) } );
			Chat.Tell( caller, "That faction does not exist." );
			return;
		}
		var steamId = SteamIdOf( caller );
		if ( faction.RequiresWhitelist && !game.Account( steamId ).Whitelists.Contains( faction.ResourcePath ) )
		{
			HostRecord( JournalKinds.CharacterCreate, faction.ResourceName, ok: false, ("reason", "whitelist") );
			Chat.Tell( caller, $"You are not whitelisted for {faction.Title}." );
			return;
		}

		// A new character arrives with nothing but its clothes. Whatever it needs, it finds or is given.
		var created = game.Roster!.Create( steamId, name, description, faction.ResourcePath, DateTimeOffset.UtcNow, game.Lives );
		if ( !created.Ok )
		{
			// The name is kept: an attempt to pass as someone else is worth knowing about.
			HostRecord( JournalKinds.CharacterCreate, faction.ResourceName, ok: false, ("reason", created.Code.ToString()), ("name", Clip( name ?? string.Empty )) );
			Chat.Tell( caller, created.Message );
			return;
		}
		var data = created.Value.ArrivesAt is { } arrives
			? new[] { ("faction", faction.ResourceName), ("arrives", arrives.ToString( "u" )) }
			: new[] { ("faction", faction.ResourceName) };
		game.Journal!.Record( JournalKinds.CharacterCreate, Actor.Of( created.Value ), created.Value.HolderLabel, data: data );
		SendCharacterList( caller, game );
	}

	/// <summary>Deletes a character that never entered the city, freeing its name. One that has lived is retired instead.</summary>
	[Rpc.Host]
	public void RequestDeleteCharacter( Guid characterId )
	{
		if ( !Authorize( nameof( RequestDeleteCharacter ), out var caller, out var game, needsCharacter: false ) ) return;
		var steamId = SteamIdOf( caller );
		var doomed = game.Roster!.Find( characterId );
		var refusal = Admission.ToDelete( doomed, steamId );
		if ( doomed is null || refusal is not null )
		{
			HostRecord( JournalKinds.CharacterDelete, $"character:{characterId:N}", ok: false, ("reason", refusal ?? "unknown") );
			Chat.Tell( caller, refusal ?? Admission.NotFound );
			return;
		}
		// Staff may have given it something already. That leaves the world on the record, not by vanishing with the file.
		var by = Actor.Of( doomed );
		game.Transfers!.DestroyAll( Sinks.CharacterDeleted, doomed, by );
		Door.HostReleaseOwnedBy( doomed.Id, by );
		game.Journal!.Record( JournalKinds.CharacterDelete, by, doomed.HolderLabel );
		game.Roster.Delete( steamId, characterId );
		SendCharacterList( caller, game );
	}

	/// <summary>
	/// Ends a life that has been lived, by its player's choice. It is a death: the character takes
	/// what it carries with it, it is kept as dead, and the next one waits as after any other death.
	/// </summary>
	[Rpc.Host]
	public void RequestRetireCharacter( Guid characterId )
	{
		if ( !Authorize( nameof( RequestRetireCharacter ), out var caller, out var game, needsCharacter: false ) ) return;
		var steamId = SteamIdOf( caller );
		var retiring = game.Roster!.Find( characterId );
		var refusal = Admission.ToRetire( retiring, steamId, loaded: HostLoaded( characterId ) is not null );
		if ( retiring is null || refusal is not null )
		{
			HostRecord( JournalKinds.CharacterRetire, $"character:{characterId:N}", ok: false, ("reason", refusal ?? "unknown") );
			Chat.Tell( caller, refusal ?? Admission.NotFound );
			return;
		}
		var by = Actor.Of( retiring );
		game.Transfers!.DestroyAll( Sinks.CharacterRetired, retiring, by );
		Door.HostReleaseOwnedBy( retiring.Id, by );
		Vitals.Die( retiring, DateTimeOffset.UtcNow );
		game.Roster.Save( retiring );
		game.Journal!.Record( JournalKinds.CharacterRetire, by, retiring.HolderLabel );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestEnterCity( Guid characterId )
	{
		if ( !Authorize( nameof( RequestEnterCity ), out var caller, out var game, needsCharacter: false ) ) return;
		var character = game.Roster!.Find( characterId );
		var steamId = SteamIdOf( caller );
		var faction = character is null ? null : FactionDefinition.Find( character.Faction );
		var now = DateTimeOffset.UtcNow;
		var whitelistedOut = faction is { RequiresWhitelist: true } && !game.Account( steamId ).Whitelists.Contains( faction.ResourcePath ) ? faction.Title : null;
		var refusal = Admission.ToEnter( character, steamId, now, inCity: _character is not null, loadedElsewhere: HostLoaded( characterId ) is not null, whitelistedOut );
		if ( character is null || refusal is not null )
		{
			HostRecord( JournalKinds.CityEnter, $"character:{characterId:N}", ok: false, ("reason", refusal ?? "unknown") );
			Chat.Tell( caller, refusal ?? Admission.NotFound );
			return;
		}

		// Whoever was left lying, or bound, where they were picks up from there.
		Sleeper.HostRemove( character.Id );
		// From now on it has lived, and leaves the city only by dying or retiring.
		if ( character.FirstEntered is null )
		{
			character.FirstEntered = now;
			game.Roster.Save( character );
		}
		_character = character;
		CharacterDescription = character.Description;
		CharacterId = character.Id;
		HasCharacter = true;
		var position = character.Position is { Length: 3 } saved
			? new Vector3( saved[0], saved[1], saved[2] )
			: game.FindSpawn().Position;
		HostTeleport( position );
		SendPrivateState();
		HostRecord( JournalKinds.CityEnter );
	}

	[Rpc.Host]
	public void RequestLeaveCity()
	{
		if ( !Authorize( nameof( RequestLeaveCity ), out var caller, out var game ) ) return;
		if ( IsIncapable )
		{
			// Walking out of a scene that has gone badly is not on offer. Disconnecting is no way out
			// either: the character stays where it is, as anyone else can see.
			HostRecord( JournalKinds.CityLeave, ok: false, data: ("reason", IsDown ? "down" : "restrained") );
			Chat.Tell( caller, "You cannot leave the city like this." );
			return;
		}
		HostSave();
		HostRecord( JournalKinds.CityLeave );
		HostUnload();
		SendCharacterList( caller, game );
	}

	/// <summary>Host: staff took away what this character needs to be in the city. It is saved, and its player is back in the menu.</summary>
	internal void HostSendToMenu( string message )
	{
		if ( _character is null || HexagonServices.Current is not { } game ) return;
		HostSave();
		HostRecord( JournalKinds.CityLeave, data: ("reason", "sent") );
		HostUnload();
		if ( Network.Owner is not { } owner ) return;
		SendCharacterList( owner, game );
		Chat.Tell( owner, message );
	}

	/// <summary>
	/// Host: its player has gone. The character is saved and let go of before the pawn goes, which is
	/// not at once, so nothing takes it for still being in the city meanwhile: whoever is down or
	/// bound is left where they are.
	/// </summary>
	internal void HostDisconnected()
	{
		HostSave();
		HostUnload();
	}

	/// <summary>Host: the character stops being in the city. The pawn stays, back in the menu.</summary>
	private void HostUnload()
	{
		HostClose();
		HostDropOffer( "left" );
		// Someone down or bound does not get up because their player went: they stay where they are.
		if ( _character is { } character && HexagonServices.Current?.Roster?.Contains( character ) == true ) Sleeper.HostLeave( character, HostPosition );
		_character = null;
		HasCharacter = false;
		CharacterId = Guid.Empty;
		CharacterDescription = string.Empty;
		HostSyncVitals();
	}

	[Rpc.Host]
	public void RequestSay( string text )
	{
		if ( !Authorize( nameof( RequestSay ), out var caller, out _ ) ) return;
		var message = ChatRules.Parse( text );
		if ( message.Ok )
		{
			Chat.Deliver( this, message.Value );
			return;
		}
		HostRecord( JournalKinds.Chat, ok: false, data: new[] { ("reason", message.Code.ToString()), ("text", Clip( text ?? string.Empty )) } );
		Chat.Tell( caller, message.Message );
	}

	[Rpc.Host]
	public void RequestMoveItem( Guid itemId, int x, int y )
	{
		if ( !Authorize( nameof( RequestMoveItem ), out var caller, out var game ) || HostItem( nameof( RequestMoveItem ), itemId ) is null ) return;
		var character = _character!;
		// Put down on a pile of the same thing, it joins it. Otherwise it is only rearranged.
		var under = InventoryGrid.PileAt( character.Inventory, x, y, except: itemId );
		var moved = under is not null && game.Transfers!.Merge( character, itemId, under.Id, Actor.Of( character ) ) is { Ok: true } merged
			? merged
			: InventoryGrid.Move( character.Inventory, itemId, x, y );
		if ( moved.Ok ) game.Roster!.Save( character );
		else Chat.Tell( caller, moved.Message );
		SendPrivateState();
	}

	/// <summary>Sets part of a pile aside as a pile of its own, to hand over or drop.</summary>
	[Rpc.Host]
	public void RequestSplitItem( Guid itemId, int count )
	{
		if ( !Authorize( nameof( RequestSplitItem ), out var caller, out var game ) || HostItem( nameof( RequestSplitItem ), itemId ) is null ) return;
		var split = game.Transfers!.Split( _character!, itemId, count, Actor.Of( _character! ) );
		if ( split.Ok ) game.Roster!.Save( _character! );
		else
		{
			// Want of room is on record as the split that failed. A count that is not part of the pile was never a split.
			if ( split.Code == ErrorCode.Invalid ) HostInvalid( nameof( RequestSplitItem ), "count", count.ToString() );
			Chat.Tell( caller, split.Message );
		}
		SendPrivateState();
	}

	/// <summary>
	/// The one way a character acts on something in the world. The target only says what can be
	/// done to it; this decides whether this character may, here, now, and records the answer.
	/// </summary>
	[Rpc.Host]
	public void RequestAct( Component target, string verbId )
	{
		if ( !Authorize( nameof( RequestAct ), out var caller, out _ ) ) return;
		if ( !target.IsValid() || target is not IVerbTarget actable )
		{
			HostInvalid( nameof( RequestAct ), "target", verbId );
			return;
		}
		// Asking for something the target does not offer, such as finishing someone still standing.
		if ( actable.Verbs.FirstOrDefault( value => value.Id == verbId ) is not { } verb )
		{
			HostInvalid( nameof( RequestAct ), "verb", verbId );
			return;
		}

		var subject = actable.JournalSubject ?? $"{target.GetType().Name}:{target.GameObject.Id:N}";
		var outcome = IsIncapable ? Result.Fail( ErrorCode.Denied, "You cannot do that now." ) : Judge( target, actable, verb );
		if ( outcome.Ok ) outcome = actable.Perform( this, verb );
		HostRecord( JournalKinds.Verb( verb.Id ), subject, outcome.Ok, ("target", target.GameObject.Name), ("reason", outcome.Code.ToString()) );
		if ( !outcome.Ok ) Chat.Tell( caller, outcome.Message );
	}

	private Result Judge( Component target, IVerbTarget actable, Verb verb )
	{
		// The host measures this itself, from where it believes the character is; a client saying
		// "I am next to it" counts for nothing.
		var nearest = target.GameObject.GetBounds().ClosestPoint( HostPosition );
		if ( nearest.Distance( HostPosition ) > actable.Reach ) return Result.Fail( ErrorCode.Denied, "You are too far away." );
		if ( !HostCanReach( target ) ) return Result.Fail( ErrorCode.Denied, "You cannot reach it from here." );
		if ( verb.Requires is { } capability && !HostCan( capability ) )
			return Result.Fail( ErrorCode.Denied, "You have nothing that lets you do that." );
		return Result.Success();
	}

	/// <summary>Eye height above where a character stands, as the host reckons it.</summary>
	public const float EyeHeight = 64f;

	/// <summary>
	/// Host: whether a hand could get to the target from where the character is believed to stand: a
	/// line from its eyes to the target, to its nearest part or its middle, that nothing solid crosses.
	/// Being near a crate through a wall, or a person through a shut door, is not being able to touch
	/// them. The target's own parts, people, and things lying about are not in the way.
	/// </summary>
	private bool HostCanReach( Component target )
	{
		var eye = HostPosition + Vector3.Up * EyeHeight;
		var bounds = target.GameObject.GetBounds();
		// Aimed a little inside the nearest point, so a trace that ends on the target's face is not
		// stopped by the floor it stands on or the frame it hangs in.
		return Clear( eye, Vector3.Lerp( bounds.ClosestPoint( eye ), bounds.Center, 0.1f ), target ) || Clear( eye, bounds.Center, target );
	}

	private bool Clear( Vector3 from, Vector3 to, Component target )
	{
		var trace = Scene.Trace.Ray( from, to )
			.WithoutTags( "player", "item", "corpse", "sleeper" )
			.IgnoreGameObjectHierarchy( GameObject )
			.IgnoreGameObjectHierarchy( target.GameObject )
			.Run();
		if ( !trace.Hit || trace.StartedSolid ) return true;
		// A door still swinging is no wall, as for walking.
		return trace.GameObject?.GetComponentInParent<Door>() is { IsSwinging: true };
	}
}
