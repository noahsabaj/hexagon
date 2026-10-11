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
			HostRecord( "character.create", ok: false, data: new[] { ("reason", "faction"), ("faction", Clip( factionPath ?? string.Empty )) } );
			Chat.Tell( caller, "That faction does not exist." );
			return;
		}
		var steamId = SteamIdOf( caller );
		if ( faction.RequiresWhitelist && !game.Account( steamId ).Whitelists.Contains( faction.ResourcePath ) )
		{
			HostRecord( "character.create", faction.ResourceName, ok: false, ("reason", "whitelist") );
			Chat.Tell( caller, $"You are not whitelisted for {faction.Title}." );
			return;
		}

		var created = game.Roster!.Create( steamId, name, description, faction.ResourcePath, DateTimeOffset.UtcNow );
		if ( !created.Ok )
		{
			// The name is kept: an attempt to pass as someone else is worth knowing about.
			HostRecord( "character.create", faction.ResourceName, ok: false, ("reason", created.Code.ToString()), ("name", Clip( name ?? string.Empty )) );
			Chat.Tell( caller, created.Message );
			return;
		}
		var by = Actor.Of( created.Value );
		game.Journal!.Record( "character.create", by, created.Value.HolderLabel, data: ("faction", faction.ResourceName) );
		foreach ( var start in faction.StartingItems.Where( value => value.Item.IsValid() ) )
			start.Item!.IssueTo( game.Transfers!, Sources.CharacterStart, created.Value, by, start.Count );
		if ( faction.StartingTokens > 0 ) game.Transfers!.IssueTokens( Sources.CharacterStart, created.Value, faction.StartingTokens, by );
		game.Roster.Save( created.Value );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestDeleteCharacter( Guid characterId )
	{
		if ( !Authorize( nameof( RequestDeleteCharacter ), out var caller, out var game, needsCharacter: false ) ) return;
		var steamId = SteamIdOf( caller );
		var doomed = game.Roster!.Find( characterId );
		// "Not yours" and "does not exist" get the same answer, so ids cannot be probed.
		var refusal = doomed is null || doomed.SteamId != steamId ? "Character was not found."
			// On any connection: two copies of the game on one account are still one account.
			: HostLoaded( characterId ) is not null ? "Leave the city with that character before deleting it."
			// Someone lying in the street, or bound, is still part of a scene that is not over.
			: doomed.IsDown || doomed.IsRestrained ? "That character cannot be deleted while it is down or bound."
			: null;
		if ( doomed is null || refusal is not null )
		{
			HostRecord( "character.delete", $"character:{characterId:N}", ok: false, ("reason", refusal ?? "unknown") );
			Chat.Tell( caller, refusal ?? "Character was not found." );
			return;
		}
		// What the character held leaves the world on the record, not by vanishing with the file.
		var by = Actor.Of( doomed );
		game.Transfers!.DestroyAll( Sinks.CharacterDeleted, doomed, by );
		Door.HostReleaseOwnedBy( doomed.Id, by );
		game.Journal!.Record( "character.delete", by, doomed.HolderLabel );
		game.Roster.Delete( steamId, characterId );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestEnterCity( Guid characterId )
	{
		if ( !Authorize( nameof( RequestEnterCity ), out var caller, out var game, needsCharacter: false ) ) return;
		var character = game.Roster!.Find( characterId );
		var steamId = SteamIdOf( caller );
		var faction = character is null ? null : FactionDefinition.Find( character.Faction );
		var refusal = _character is not null ? "Leave the city first."
			// "Not yours" and "does not exist" get the same answer, so ids cannot be probed.
			: character is null || character.SteamId != steamId ? "Character was not found."
			: HostLoaded( characterId ) is not null ? "That character is already in the city."
			// A whitelist taken away takes the faction's characters with it until it is given back.
			: faction is { RequiresWhitelist: true } && !game.Account( steamId ).Whitelists.Contains( faction.ResourcePath ) ? $"You are no longer whitelisted for {faction.Title}."
			: null;
		if ( character is null || refusal is not null )
		{
			HostRecord( "city.enter", $"character:{characterId:N}", ok: false, ("reason", refusal ?? "unknown") );
			Chat.Tell( caller, refusal ?? "Character was not found." );
			return;
		}

		// Whoever was left lying, or bound, where they were picks up from there.
		Sleeper.HostRemove( character.Id );
		_character = character;
		CharacterDescription = character.Description;
		CharacterId = character.Id;
		HasCharacter = true;
		var position = character.Position is { Length: 3 } saved
			? new Vector3( saved[0], saved[1], saved[2] )
			: game.FindSpawn().Position;
		HostTeleport( position );
		SendPrivateState();
		HostRecord( "city.enter" );
	}

	[Rpc.Host]
	public void RequestLeaveCity()
	{
		if ( !Authorize( nameof( RequestLeaveCity ), out var caller, out var game ) ) return;
		if ( IsIncapable )
		{
			// Walking out of a scene that has gone badly is not on offer. Disconnecting is no way out
			// either: the character stays where it is, as anyone else can see.
			HostRecord( "city.leave", ok: false, data: ("reason", IsDown ? "down" : "restrained") );
			Chat.Tell( caller, "You cannot leave the city like this." );
			return;
		}
		HostSave();
		HostRecord( "city.leave" );
		HostUnload();
		SendCharacterList( caller, game );
	}

	/// <summary>Host: staff took away what this character needs to be in the city. It is saved, and its player is back in the menu.</summary>
	internal void HostSendToMenu( string message )
	{
		if ( _character is null || GameManager.Instance is not { } game ) return;
		HostSave();
		HostRecord( "city.leave", data: ("reason", "sent") );
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
		// Someone down or bound does not get up because their player went: they stay where they are.
		if ( _character is { } character && GameManager.Instance?.Roster?.Contains( character ) == true ) Sleeper.HostLeave( character, HostPosition );
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
		HostRecord( "chat", ok: false, data: new[] { ("reason", message.Code.ToString()), ("text", Clip( text ?? string.Empty )) } );
		Chat.Tell( caller, message.Message );
	}

	[Rpc.Host]
	public void RequestMoveItem( Guid itemId, int x, int y )
	{
		if ( !Authorize( nameof( RequestMoveItem ), out var caller, out var game ) || HostItem( nameof( RequestMoveItem ), itemId ) is null ) return;
		var character = _character!;
		// Put down on a pile of the same thing, it joins it. Otherwise it is only rearranged.
		var under = character.Inventory.Items.FirstOrDefault( value => value.Id != itemId && value.Max > 1 &&
			x >= value.X && x < value.X + value.Width && y >= value.Y && y < value.Y + value.Height );
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
		HostRecord( $"verb.{verb.Id}", subject, outcome.Ok, ("target", target.GameObject.Name), ("reason", outcome.Code.ToString()) );
		if ( !outcome.Ok ) Chat.Tell( caller, outcome.Message );
	}

	private Result Judge( Component target, IVerbTarget actable, Verb verb )
	{
		// The host measures this itself, from where it believes the character is; a client saying
		// "I am next to it" counts for nothing.
		var nearest = target.GameObject.GetBounds().ClosestPoint( HostPosition );
		if ( nearest.Distance( HostPosition ) > actable.Reach ) return Result.Fail( ErrorCode.Denied, "You are too far away." );
		if ( verb.Requires is { } capability && !HostCan( capability ) )
			return Result.Fail( ErrorCode.Denied, "You have nothing that lets you do that." );
		return Result.Success();
	}
}
