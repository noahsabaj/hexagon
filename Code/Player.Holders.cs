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
	public long OpenTokens { get; private set; }

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
		using ( Rpc.FilterInclude( owner ) ) ReceiveOpenHolder( title, JsonSerializer.Serialize( holder.Inventory ), holder.Tokens );
	}

	public void HostClose()
	{
		if ( _open is null ) return;
		_open = null;
		_openTarget = null;
		_openWhile = null;
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveOpenHolder( string.Empty, string.Empty, 0 );
	}

	/// <summary>Host: whether the open holder may still be used. Walking away, or the reason it was open ending, closes it.</summary>
	private bool HostOpenIsValid()
	{
		if ( _open is null || !_openTarget.IsValid() || IsIncapable ) return false;
		if ( _openWhile is not null && !_openWhile() ) return false;
		var reach = (_openTarget as IVerbTarget)?.Reach ?? 150f;
		return _openTarget!.GameObject.GetBounds().ClosestPoint( HostPosition ).Distance( HostPosition ) <= reach;
	}

	/// <summary>Host: everyone looking into this holder sees the change, and the owner of a character sees their own.</summary>
	internal static void HostRefresh( IHolder holder )
	{
		foreach ( var player in Game.ActiveScene.GetAllComponents<Player>() )
		{
			if ( ReferenceEquals( player._character, holder ) ) player.SendPrivateState();
			if ( !ReferenceEquals( player._open, holder ) || player.Network.Owner is not { } owner ) continue;
			using ( Rpc.FilterInclude( owner ) ) player.ReceiveOpenHolder( player._openTitle, JsonSerializer.Serialize( holder.Inventory ), holder.Tokens );
		}
	}

	internal static void HostSaveHolder( IHolder holder )
	{
		if ( GameManager.Instance is not { } game ) return;
		if ( holder is CharacterData character ) game.Roster?.Save( character );
		else if ( holder is HolderData data ) game.Holders?.Save( data );
	}

	/// <summary>Host: moves one item between two holders, saves the giver first, and tells everyone who can see either.</summary>
	internal Result HostMove( IHolder from, IHolder to, Guid itemId )
	{
		if ( _character is null || GameManager.Instance?.Transfers is not { } transfers ) return Result.Fail( ErrorCode.Internal, "The city is not ready." );
		var moved = transfers.Move( from, to, itemId, Actor.Of( _character ) );
		if ( !moved.Ok ) return moved;
		HostSaveHolder( from );
		HostSaveHolder( to );
		HostRefresh( from );
		HostRefresh( to );
		return moved;
	}

	/// <summary>Host: this character takes an item out of a holder.</summary>
	public Result HostTakeFrom( IHolder holder, Guid itemId ) =>
		_character is null ? Result.Fail( ErrorCode.Denied, "You have no character." ) : HostMove( holder, _character, itemId );

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveOpenHolder( string title, string inventoryJson, long tokens )
	{
		OpenTitle = title;
		OpenTokens = tokens;
		OpenInventory = inventoryJson.Length == 0 ? null : JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		PrivateVersion++;
	}

	[Rpc.Host]
	public void RequestTake( Guid itemId ) => Exchange( itemId, taking: true );

	[Rpc.Host]
	public void RequestPut( Guid itemId ) => Exchange( itemId, taking: false );

	private void Exchange( Guid itemId, bool taking )
	{
		if ( !Authorize( out var caller, out _ ) || _character is null ) return;
		if ( !HostOpenIsValid() )
		{
			HostClose();
			return;
		}
		var open = _open!;
		var moved = taking ? HostMove( open, _character, itemId ) : HostMove( _character, open, itemId );
		if ( !moved.Ok ) Chat.Tell( caller, moved.Message );
		Corpse.RemoveIfEmpty( open );
	}

	/// <summary>Takes every token the open holder has: a body's, a searched person's.</summary>
	[Rpc.Host]
	public void RequestTakeTokens()
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		if ( !HostOpenIsValid() )
		{
			HostClose();
			return;
		}
		var open = _open!;
		var taken = game.Transfers!.MoveTokens( open, _character, open.Tokens, Actor.Of( _character ) );
		if ( !taken.Ok )
		{
			Chat.Tell( caller, taken.Message );
			return;
		}
		HostSaveHolder( open );
		HostSaveHolder( _character );
		HostRefresh( open );
		HostRefresh( _character );
		Corpse.RemoveIfEmpty( open );
	}

	[Rpc.Host]
	public void RequestCloseHolder()
	{
		if ( Authorize( out _, out _ ) ) HostClose();
	}

	/// <summary>What a character does with something it holds. Judged and journaled like any other act.</summary>
	[Rpc.Host]
	public void RequestItemAct( Guid itemId, string verbId )
	{
		if ( !Authorize( out var caller, out var game ) || _character is null ) return;
		if ( _character.Inventory.Items.FirstOrDefault( value => value.Id == itemId ) is not { } stack ) return;
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
		var used = game.Transfers!.Destroy( Sinks.Consumed, _character!, stack.Id, Actor.Of( _character! ) );
		if ( !used.Ok ) return used;
		game.Roster!.Save( _character! );
		SendPrivateState();
		// Those nearby see it happen, as they would.
		if ( definition.UseEmote.Length > 0 ) Chat.Deliver( this, new ChatMessage( ChatChannel.Me, definition.UseEmote ) );
		return used;
	}
}
