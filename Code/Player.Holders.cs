#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// A character's dealings with holders: its own inventory, and whatever other holder it has open.
// A crate, a searched person and a corpse are all "the open holder"; only how it got opened differs.
public sealed partial class Player
{
	// Owner-side. Null when nothing is open.
	public string OpenTitle { get; private set; } = string.Empty;
	public InventoryData? OpenInventory { get; private set; }
	/// <summary>What was opened: a crate, a person, a body.</summary>
	public Component? OpenTarget { get; private set; }

	// Host-side.
	private IHolder? _open;
	private Component? _openTarget;
	private string _openTitle = string.Empty;
	private Func<bool>? _openWhile;

	/// <summary>Host: shows this character what a holder contains, for as long as it stays in reach of the target.</summary>
	public void HostOpen( Component target, IHolder holder, string title, Func<bool>? onlyWhile = null )
	{
		if ( !Networking.IsHost || _character is null || Network.Owner is not { } owner ) return;
		_open = holder;
		_openTarget = target;
		_openTitle = title;
		_openWhile = onlyWhile;
		using ( Rpc.FilterInclude( owner ) ) ReceiveOpenHolder( target, title, Shown( holder ) );
	}

	/// <summary>What a player is shown of a holder: its keys by their cuts, never what they open.</summary>
	private static string Shown( IHolder holder ) => JsonSerializer.Serialize( Keys.AsSeen( holder.Inventory ) );

	public void HostClose()
	{
		if ( _open is null ) return;
		_open = null;
		_openTarget = null;
		_openWhile = null;
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveOpenHolder( null, string.Empty, string.Empty );
	}

	/// <summary>Host: whether the open holder may still be used. Walking away, or the reason it was open ending, closes it.</summary>
	private bool HostOpenIsValid()
	{
		if ( _open is null || !_openTarget.IsValid() || IsIncapable ) return false;
		if ( _openWhile is not null && !_openWhile() ) return false;
		var reach = (_openTarget as IVerbTarget)?.Reach ?? 150f;
		return _openTarget!.GameObject.GetBounds().ClosestPoint( HostPosition ).Distance( HostPosition ) <= reach;
	}

	/// <summary>
	/// Host: everyone looking into this holder sees the change, the owner of a character sees their
	/// own, and someone left lying where they were shows what was done to them.
	/// </summary>
	internal static void HostRefresh( IHolder holder )
	{
		foreach ( var player in Game.ActiveScene.GetAllComponents<Player>() )
		{
			if ( ReferenceEquals( player._character, holder ) ) player.SendPrivateState();
			if ( !ReferenceEquals( player._open, holder ) || player.Network.Owner is not { } owner ) continue;
			using ( Rpc.FilterInclude( owner ) ) player.ReceiveOpenHolder( player._openTarget, player._openTitle, Shown( holder ) );
		}
		if ( holder is CharacterData character ) Sleeper.HostRefresh( character );
	}

	/// <summary>Host: moves one item between two holders, saves both as one change, and tells everyone who can see either.</summary>
	internal Result HostMove( IHolder from, IHolder to, Guid itemId )
	{
		if ( _character is null || GameManager.Instance is not { Transfers: { } transfers } game ) return Result.Fail( ErrorCode.Internal, "The city is not ready." );
		var moved = transfers.Move( from, to, itemId, Actor.Of( _character ) );
		if ( !moved.Ok ) return moved;
		game.SaveTogether( from, to );
		HostRefresh( from );
		HostRefresh( to );
		return moved;
	}

	/// <summary>Host: this character takes an item out of a holder.</summary>
	public Result HostTakeFrom( IHolder holder, Guid itemId ) =>
		_character is null ? Result.Fail( ErrorCode.Denied, "You have no character." ) : HostMove( holder, _character, itemId );

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveOpenHolder( Component? target, string title, string inventoryJson )
	{
		OpenTarget = target;
		OpenTitle = title;
		OpenInventory = inventoryJson.Length == 0 ? null : JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		PrivateVersion++;
	}

	[Rpc.Host]
	public void RequestTake( Guid itemId ) => Exchange( nameof( RequestTake ), itemId, taking: true );

	[Rpc.Host]
	public void RequestPut( Guid itemId ) => Exchange( nameof( RequestPut ), itemId, taking: false );

	private void Exchange( string request, Guid itemId, bool taking )
	{
		if ( !Authorize( request, out var caller, out _ ) || _character is null ) return;
		if ( !HostOpenIsValid() )
		{
			HostInvalid( request, "closed" );
			HostClose();
			return;
		}
		var open = _open!;
		if ( !(taking ? open : _character).Inventory.Items.Any( value => value.Id == itemId ) )
		{
			// Someone else took it first, or it was never there. Either way the client is shown what is.
			HostInvalid( request, "item", itemId.ToString( "N" ) );
			HostRefresh( open );
			Chat.Tell( caller, "That is not there any more." );
			return;
		}
		var moved = taking ? HostMove( open, _character, itemId ) : HostMove( _character, open, itemId );
		if ( !moved.Ok ) Chat.Tell( caller, moved.Message );
		Corpse.RemoveIfEmpty( open );
	}

	[Rpc.Host]
	public void RequestCloseHolder()
	{
		if ( Authorize( nameof( RequestCloseHolder ), out _, out _, needsCharacter: false ) ) HostClose();
	}

	/// <summary>What a character does with something it holds. Judged and journaled like any other act.</summary>
	[Rpc.Host]
	public void RequestItemAct( Guid itemId, string verbId )
	{
		if ( !Authorize( nameof( RequestItemAct ), out var caller, out var game ) || HostItem( nameof( RequestItemAct ), itemId ) is not { } stack ) return;
		var definition = ItemDefinition.Find( stack.Definition );
		Result outcome;
		if ( IsIncapable ) outcome = Result.Fail( ErrorCode.Denied, "You cannot do that now." );
		else if ( verbId == "item.drop" ) outcome = Drop( game, stack );
		else if ( verbId == "item.use" && definition is { Consumable: true } ) outcome = Consume( game, stack, definition );
		else if ( verbId == "item.equip" ) outcome = Equip( game, stack );
		else outcome = Result.Fail( ErrorCode.Invalid, "That cannot be done with it." );
		HostRecord( $"verb.{verbId}", $"item:{itemId:N}", outcome.Ok, ("definition", stack.Definition), ("reason", outcome.Code.ToString()) );
		if ( !outcome.Ok ) Chat.Tell( caller, outcome.Message );
	}

	/// <summary>Turns a radio's dial. The setting is the radio's, so whoever takes the radio takes it too.</summary>
	[Rpc.Host]
	public void RequestTune( Guid itemId, string frequency )
	{
		if ( !Authorize( nameof( RequestTune ), out var caller, out var game ) || HostItem( nameof( RequestTune ), itemId ) is not { } stack ) return;
		var tuned = IsIncapable ? Result<string>.Fail( ErrorCode.Denied, "You cannot do that now." )
			: ItemDefinition.Find( stack.Definition ) is not { IsRadio: true } ? Result<string>.Fail( ErrorCode.Invalid, "That is not a radio." )
			: ChatRules.Frequency( frequency );
		if ( tuned.Ok )
		{
			stack.Frequency = tuned.Value;
			game.Roster!.Save( _character! );
			SendPrivateState();
		}
		HostRecord( "verb.item.tune", $"item:{itemId:N}", tuned.Ok, ("frequency", tuned.Ok ? tuned.Value : frequency), ("reason", tuned.Code.ToString()) );
		if ( !tuned.Ok ) Chat.Tell( caller, tuned.Message );
	}

	private Result Drop( GameManager game, ItemStack stack )
	{
		var at = HostPosition + Vector3.Up * 8f + Vector3.Random.WithZ( 0 ) * 24f;
		var ground = game.Holders!.GetOrCreate( Guid.NewGuid(), HolderKinds.Ground, "Ground", 4, 4 );
		ground.Position = new[] { at.x, at.y, at.z };
		var moved = HostMove( _character!, ground, stack.Id );
		if ( moved.Ok ) WorldItem.Spawn( ground );
		else game.Holders.Delete( ground.Id );
		return moved;
	}

	/// <summary>Takes the item in hand, or puts it away if it already is. What is in hand is visible to everyone.</summary>
	private Result Equip( GameManager game, ItemStack stack )
	{
		_character!.Equipped = _character.Equipped == stack.Id ? null : stack.Id;
		game.Roster!.Save( _character );
		SendPrivateState();
		return Result.Success();
	}

	private Result Consume( GameManager game, ItemStack stack, ItemDefinition definition )
	{
		if ( definition.Heals > 0 )
		{
			var healed = Vitals.Heal( _character!, definition.Heals );
			if ( !healed.Ok ) return healed;
		}
		var used = game.Transfers!.Destroy( Sinks.Consumed, _character!, stack.Id, Actor.Of( _character! ), count: 1 );
		if ( !used.Ok ) return used;
		game.Roster!.Save( _character! );
		SendPrivateState();
		// Those nearby see it happen, as they would.
		if ( definition.UseEmote.Length > 0 ) Chat.Deliver( this, new ChatMessage( ChatChannel.Me, definition.UseEmote ) );
		return used;
	}
}
