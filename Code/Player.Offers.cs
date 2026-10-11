#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Handing something over takes two. One character offers; the other accepts or refuses, and nothing
// changes hands until they accept. An offer that waits too long, or whose two sides part, lapses.
// The title to a door is handed over the same way as anything carried.
public sealed partial class Player
{
	/// <summary>Owner-side: what this character is being offered, in words, or empty.</summary>
	public string OfferText { get; private set; } = string.Empty;

	/// <summary>Host: an offer waiting on this character's answer. What it names is checked again when it is accepted.</summary>
	private sealed record Offer( Player From, Guid ItemId, int Count, Door? Door, string What, RealTimeSince Made );

	private Offer? _offer;

	/// <summary>Offers some of a pile to someone within reach. They are asked; nothing moves until they accept.</summary>
	[Rpc.Host]
	public void RequestOffer( Player recipient, Guid itemId, int count )
	{
		if ( !Authorize( nameof( RequestOffer ), out var caller, out _ ) || HostItem( nameof( RequestOffer ), itemId ) is not { } stack ) return;
		if ( count < 1 || count > stack.Count )
		{
			HostInvalid( nameof( RequestOffer ), "count", count.ToString() );
			return;
		}
		HostMakeOffer( caller, recipient, new Offer( this, itemId, count, null, Describe( stack, count ), 0 ),
			("item", itemId.ToString( "N" )), ("definition", stack.Definition), ("count", count.ToString()) );
	}

	/// <summary>Offers the title to a door this character holds. The keys are a separate matter: they are handed over like anything else.</summary>
	[Rpc.Host]
	public void RequestOfferDoor( Player recipient, Guid doorId )
	{
		if ( !Authorize( nameof( RequestOfferDoor ), out var caller, out _ ) ) return;
		var door = Scene.GetAllComponents<Door>().FirstOrDefault( value => value.GameObject.Id == doorId );
		if ( door is null || door.HostOwner != _character!.Id )
		{
			HostInvalid( nameof( RequestOfferDoor ), "door", doorId.ToString( "N" ) );
			return;
		}
		HostMakeOffer( caller, recipient, new Offer( this, Guid.Empty, 0, door, $"the title to {door.GameObject.Name}", 0 ),
			("door", $"Door:{door.GameObject.Id:N}") );
	}

	/// <summary>The answer to whatever this character is being offered.</summary>
	[Rpc.Host]
	public void RequestAnswerOffer( bool accept )
	{
		if ( !Authorize( nameof( RequestAnswerOffer ), out var caller, out var game ) ) return;
		if ( _offer is not { } offer )
		{
			// It may have lapsed while the answer was on its way.
			Chat.Tell( caller, "Nobody is offering you anything." );
			return;
		}
		HostDropOffer();
		var giver = offer.From;
		var from = giver.IsValid() ? giver._character : null;
		var told = giver.IsValid() ? giver.Network.Owner : null;
		if ( !accept )
		{
			HostRecord( JournalKinds.OfferRefuse, from?.HolderLabel );
			if ( told is not null ) Chat.Tell( told, "Your offer is refused." );
			return;
		}
		// What was true when it was offered is checked again: the giver may have gone, or used it, or dropped it.
		var outcome = from is null ? Result.Fail( ErrorCode.NotFound, "They are gone." )
			: IsIncapable ? Result.Fail( ErrorCode.Denied, "You cannot do that now." )
			: giver.IsIncapable ? Result.Fail( ErrorCode.Denied, "They cannot hand it over now." )
			: giver.HostPosition.Distance( HostPosition ) > PersonReach ? Result.Fail( ErrorCode.Denied, "They are too far away." )
			: offer.Door is { } door ? HostTakeTitle( door, from, _character! )
			: HostTakeOffered( game, from, offer );
		HostRecord( JournalKinds.OfferAccept, from?.HolderLabel, outcome.Ok, ("reason", outcome.Code.ToString()) );
		if ( !outcome.Ok )
		{
			Chat.Tell( caller, outcome.Message );
			if ( told is not null ) Chat.Tell( told, "Your offer could not be taken." );
			return;
		}
		Chat.Tell( caller, $"You take {offer.What}." );
		if ( told is not null ) Chat.Tell( told, "Your offer is taken." );
	}

	private void HostMakeOffer( Connection caller, Player recipient, Offer offer, params (string Key, string Value)[] data )
	{
		var there = recipient.IsValid() && recipient != this && recipient._character is not null;
		var refusal = OfferRules.ToOffer( IsIncapable, there, there ? recipient.HostPosition.Distance( HostPosition ) : 0f, PersonReach,
			there && recipient.IsIncapable, there && recipient._offer is not null );
		var subject = recipient.IsValid() ? recipient._character?.HolderLabel : null;
		HostRecord( JournalKinds.Verb( VerbIds.PersonOffer ), subject, refusal is null, refusal is null ? data : data.Append( ("reason", refusal) ).ToArray() );
		if ( refusal is not null )
		{
			Chat.Tell( caller, refusal );
			return;
		}
		recipient._offer = offer;
		recipient.HostShowOffer( $"{Recognition.Label( recipient._character!, _character! )} offers you {offer.What}." );
		Chat.Tell( caller, $"You offer {offer.What}." );
	}

	/// <summary>Host: the offered things pass from the giver's hands to this character's, as one saved change.</summary>
	private Result HostTakeOffered( HexagonServices game, CharacterData from, Offer offer )
	{
		if ( from.Inventory.Items.FirstOrDefault( value => value.Id == offer.ItemId ) is not { } stack || stack.Count < offer.Count )
			return Result.Fail( ErrorCode.NotFound, "They no longer have it." );
		var moved = game.Transfers!.Move( from, _character!, offer.ItemId, Actor.Of( from ), offer.Count );
		if ( !moved.Ok ) return moved;
		game.SaveTogether( from, _character! );
		HostRefresh( from );
		HostRefresh( _character! );
		return moved;
	}

	private static Result HostTakeTitle( Door door, CharacterData from, CharacterData to )
	{
		if ( !door.IsValid() || door.HostOwner != from.Id ) return Result.Fail( ErrorCode.NotFound, "It is no longer theirs to give." );
		door.HostAssign( to, Actor.Of( from ) );
		return Result.Success();
	}

	/// <summary>Host: an offer lapses once it has waited too long, or once either side can no longer hand it over.</summary>
	private void HostOfferTick()
	{
		if ( _offer is not { } offer ) return;
		var giver = offer.From;
		var there = giver.IsValid() && giver._character is not null;
		var why = OfferRules.Lapse( offer.Made, there, IsIncapable || (there && giver.IsIncapable), there ? giver.HostPosition.Distance( HostPosition ) : 0f, PersonReach );
		if ( why is null ) return;
		HostDropOffer( why );
		if ( Network.Owner is { } owner ) Chat.Tell( owner, "The offer lapses." );
		if ( giver.IsValid() && giver.Network.Owner is { } from ) Chat.Tell( from, "Your offer lapses." );
	}

	/// <summary>Host: whatever this character was being offered is no longer waiting on it. With a reason, it lapsed, on the record.</summary>
	private void HostDropOffer( string? lapsed = null )
	{
		if ( _offer is not { } offer ) return;
		_offer = null;
		if ( lapsed is not null ) HostRecord( JournalKinds.OfferLapse, offer.From.IsValid() ? offer.From._character?.HolderLabel : null, data: ("reason", lapsed) );
		HostShowOffer( string.Empty );
	}

	private void HostShowOffer( string text )
	{
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveOffer( text );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveOffer( string text )
	{
		OfferText = text;
		PrivateVersion++;
	}

	/// <summary>What an offer is called: how many of what, and for a key its cut, which anyone can compare.</summary>
	private static string Describe( ItemStack stack, int count )
	{
		var title = ItemDefinition.Find( stack.Definition )?.Title ?? "something";
		var what = count > 1 ? $"{count} {title}" : title;
		return stack.Pattern is { } pattern ? $"{what}, cut {Keys.Cut( pattern )}" : what;
	}
}
