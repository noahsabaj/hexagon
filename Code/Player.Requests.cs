#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Everything a client can ask of the host. Each request re-derives who is asking from the
// connection, validates its arguments on the host, saves before replying, and is journaled.
// Acting on anything in the world goes through RequestAct and nowhere else.
public sealed partial class Player
{
	[Rpc.Host]
	public void RequestCharacters()
	{
		if ( Authorize( out var caller, out var game ) ) SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestCreateCharacter( string name, string description, string factionPath )
	{
		if ( !Authorize( out var caller, out var game ) ) return;
		if ( FactionDefinition.Find( factionPath ) is not { } faction )
		{
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
			Chat.Tell( caller, created.Message );
			return;
		}
		var by = Actor.Of( created.Value );
		game.Journal!.Record( "character.create", by, created.Value.HolderLabel, data: ("faction", faction.ResourceName) );
		foreach ( var item in faction.StartingItems.Where( value => value.IsValid() ) )
			game.Transfers!.Issue( Sources.CharacterStart, created.Value, item.ResourcePath, item.Width, item.Height, by );
		if ( faction.StartingTokens > 0 ) game.Transfers!.IssueTokens( Sources.CharacterStart, created.Value, faction.StartingTokens, by );
		game.Roster.Save( created.Value );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestDeleteCharacter( Guid characterId )
	{
		if ( !Authorize( out var caller, out var game ) ) return;
		if ( _character?.Id == characterId )
		{
			Chat.Tell( caller, "Leave that character before deleting it." );
			return;
		}
		var steamId = SteamIdOf( caller );
		// What the character held leaves the world on the record, not by vanishing with the file.
		if ( game.Roster!.Find( characterId ) is { } doomed && doomed.SteamId == steamId )
		{
			game.Transfers!.DestroyAll( Sinks.CharacterDeleted, doomed, Actor.Of( doomed ) );
			game.Journal!.Record( "character.delete", Actor.Of( doomed ), doomed.HolderLabel );
		}
		var deleted = game.Roster.Delete( steamId, characterId );
		if ( !deleted.Ok ) Chat.Tell( caller, deleted.Message );
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestEnterCity( Guid characterId )
	{
		if ( !Authorize( out var caller, out var game ) || _character is not null ) return;
		// "Not yours" and "does not exist" get the same answer, so ids cannot be probed.
		if ( game.Roster!.Find( characterId ) is not { } character || character.SteamId != SteamIdOf( caller ) )
		{
			Chat.Tell( caller, "Character was not found." );
			return;
		}
		if ( Scene.GetAllComponents<Player>().Any( other => other._character?.Id == characterId ) )
		{
			Chat.Tell( caller, "That character is already in the city." );
			return;
		}

		_character = character;
		CharacterDescription = character.Description;
		HasCharacter = true;
		var position = character.Position is { Length: 3 } saved
			? new Vector3( saved[0], saved[1], saved[2] )
			: game.FindSpawn().Position;
		using ( Rpc.FilterInclude( caller ) ) ReceiveTeleport( position );
		SendPrivateState();
		HostRecord( "city.enter" );
	}

	[Rpc.Host]
	public void RequestLeaveCity()
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		HostSave();
		HostRecord( "city.leave" );
		_character = null;
		HasCharacter = false;
		CharacterDescription = string.Empty;
		SendCharacterList( caller, game );
	}

	[Rpc.Host]
	public void RequestSay( string text )
	{
		if ( !Authorize( out var caller, out _ ) ) return;
		if ( _character is null )
		{
			Chat.Tell( caller, "Enter the city before speaking." );
			return;
		}
		var message = ChatRules.Parse( text );
		if ( message.Ok ) Chat.Deliver( this, message.Value );
		else Chat.Tell( caller, message.Message );
	}

	[Rpc.Host]
	public void RequestMoveItem( Guid itemId, int x, int y )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		var moved = InventoryGrid.Move( _character.Inventory, itemId, x, y );
		if ( moved.Ok ) game.Roster!.Save( _character );
		else Chat.Tell( caller, moved.Message );
		SendPrivateState();
	}

	[Rpc.Host]
	public void RequestDiscardItem( Guid itemId )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		var removed = game.Transfers!.Destroy( Sinks.Discard, _character, itemId, Actor.Of( _character ) );
		if ( removed.Ok ) game.Roster!.Save( _character );
		else Chat.Tell( caller, removed.Message );
		SendPrivateState();
	}

	/// <summary>
	/// The one way a character acts on something in the world. The target only says what can be
	/// done to it; this decides whether this character may, here, now, and records the answer.
	/// </summary>
	[Rpc.Host]
	public void RequestAct( Component target, string verbId )
	{
		if ( !Authorize( out var caller, out _ ) || _character is null ) return;
		if ( !target.IsValid() || target is not IVerbTarget actable ) return;
		if ( actable.Verbs.FirstOrDefault( value => value.Id == verbId ) is not { } verb ) return;

		var subject = $"{target.GetType().Name}:{target.GameObject.Id:N}";
		var outcome = Judge( target, actable, verb );
		if ( outcome.Ok ) outcome = actable.Perform( this, verb );
		HostRecord( $"verb.{verb.Id}", subject, outcome.Ok, ("target", target.GameObject.Name), ("reason", outcome.Code.ToString()) );
		if ( !outcome.Ok ) Chat.Tell( caller, outcome.Message );
	}

	private Result Judge( Component target, IVerbTarget actable, Verb verb )
	{
		// The host measures this itself; a client saying "I am next to it" counts for nothing.
		var nearest = target.GameObject.GetBounds().ClosestPoint( WorldPosition );
		if ( nearest.Distance( WorldPosition ) > actable.Reach ) return Result.Fail( ErrorCode.Denied, "You are too far away." );
		if ( verb.Requires is { } capability && !HostCan( capability ) )
			return Result.Fail( ErrorCode.Denied, "You have nothing that lets you do that." );
		return Result.Success();
	}
}
