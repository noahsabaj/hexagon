#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Handing something over takes two. One character offers; the other accepts or refuses, and nothing
// changes hands until they accept. An offer that waits too long, or whose two sides part, lapses.
// The title to a building is handed over the same way as anything carried, by whoever holds it, or by
// whoever carries its office's stamp while it is the office's; someone who carries that stamp may take
// a title offered to them back for the office. A place in a faction is offered too: a recruiter offers
// it, and the recruit accepts or refuses.
public sealed partial class Player
{
	/// <summary>Owner-side: what this character is being offered, in words, or empty.</summary>
	public string OfferText { get; private set; } = string.Empty;
	/// <summary>Owner-side: the office this character may take the title on offer back for, as it carries the office's stamp, or empty.</summary>
	public string OfferOffice { get; private set; } = string.Empty;

	/// <summary>
	/// Host: an offer waiting on this character's answer: some of a pile, a place in a faction, or the
	/// title to a building, held when it was offered by <paramref name="Owner"/>, or by its office while
	/// that is null. What it names is checked again when it is accepted.
	/// </summary>
	private sealed record Offer( Player From, Guid ItemId, int Count, string What, RealTimeSince Made, FactionDefinition? Faction = null, Property? Title = null, Guid? Owner = null );

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
		if ( stack.Worn )
		{
			Chat.Tell( caller, Wearing.TakeOffFirst );
			return;
		}
		HostMakeOffer( caller, recipient, new Offer( this, itemId, count, Describe( stack, count ), 0 ),
			("item", itemId.ToString( "N" )), ("definition", stack.Definition), ("count", count.ToString()) );
	}

	/// <summary>
	/// Offers the title to a building: one this character holds, or one its office holds while this
	/// character carries the office's stamp. The keys are a separate matter, handed over like anything
	/// else, and so is the money.
	/// </summary>
	[Rpc.Host]
	public void RequestOfferTitle( Player recipient, Guid propertyId )
	{
		if ( !Authorize( nameof( RequestOfferTitle ), out var caller, out _ ) ) return;
		var title = Scene.GetAllComponents<Property>().FirstOrDefault( value => value.GameObject.Id == propertyId );
		if ( title is null )
		{
			HostInvalid( nameof( RequestOfferTitle ), "property", propertyId.ToString( "N" ) );
			return;
		}
		// What the client was shown may be out of date: the stamp put down a moment ago, the title just sold.
		if ( !Titles.ActsFor( title.HostOwner, title.Office, _character!.Id, StampMarks( _character ) ) )
		{
			Chat.Tell( caller, Titles.NotYours );
			return;
		}
		HostMakeOffer( caller, recipient, new Offer( this, Guid.Empty, 0, $"the title to {title.Title}", 0, Title: title, Owner: title.HostOwner ),
			("property", $"Property:{title.GameObject.Id:N}") );
	}

	/// <summary>The answer to whatever this character is being offered.</summary>
	[Rpc.Host]
	public void RequestAnswerOffer( bool accept )
	{
		if ( Authorize( nameof( RequestAnswerOffer ), out var caller, out var game ) ) HostAnswerOffer( caller, game, accept, false );
	}

	/// <summary>Takes the title on offer back for its office, whose stamp this character carries, rather than for itself.</summary>
	[Rpc.Host]
	public void RequestTakeForOffice()
	{
		if ( Authorize( nameof( RequestTakeForOffice ), out var caller, out var game ) ) HostAnswerOffer( caller, game, true, true );
	}

	private void HostAnswerOffer( Connection caller, HexagonServices game, bool accept, bool forOffice )
	{
		if ( _offer is not { } offer )
		{
			// It may have lapsed while the answer was on its way.
			Chat.Tell( caller, "Nobody is offering you anything." );
			return;
		}
		if ( forOffice && offer.Title is null )
		{
			Chat.Tell( caller, "That is not a title, to take for an office." );
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
			: offer.Title is { } title ? HostTakeTitle( offer, title, from, forOffice )
			: offer.Faction is { } faction ? game.Characters!.Enlist( _character!, from, faction )
			: HostTakeOffered( game, from, offer );
		var record = new List<(string, string)> { ("reason", outcome.Code.ToString()) };
		if ( forOffice ) record.Add( ("for", offer.Title!.Office) );
		HostRecord( JournalKinds.OfferAccept, from?.HolderLabel, outcome.Ok, record.ToArray() );
		if ( !outcome.Ok )
		{
			Chat.Tell( caller, outcome.Message );
			if ( told is not null ) Chat.Tell( told, "Your offer could not be taken." );
			return;
		}
		// Joining a faction is told in the faction's own words, to both.
		if ( offer.Faction is not null ) return;
		Chat.Tell( caller, forOffice ? $"You take {offer.What} for {offer.Title!.Office}." : $"You take {offer.What}." );
		if ( told is not null ) Chat.Tell( told, "Your offer is taken." );
	}

	private void HostMakeOffer( Connection caller, Player recipient, Offer offer, params (string Key, string Value)[] data )
	{
		var made = HostPlaceOffer( recipient, offer );
		var subject = recipient.IsValid() ? recipient._character?.HolderLabel : null;
		HostRecord( JournalKinds.Verb( VerbIds.PersonOffer ), subject, made.Ok, made.Ok ? data : data.Append( ("reason", made.Message) ).ToArray() );
		if ( !made.Ok ) Chat.Tell( caller, made.Message );
	}

	/// <summary>Host: puts an offer to someone within reach, if both are free to hand it over, and tells them both.</summary>
	private Result HostPlaceOffer( Player recipient, Offer offer )
	{
		var there = recipient.IsValid() && recipient != this && recipient._character is not null;
		var refusal = OfferRules.ToOffer( IsIncapable, there, there ? recipient.HostPosition.Distance( HostPosition ) : 0f, PersonReach,
			there && recipient.IsIncapable, there && recipient._offer is not null );
		if ( refusal is not null ) return Result.Fail( ErrorCode.Denied, refusal );
		recipient._offer = offer;
		// A title someone holds may be taken back for its office by whoever carries the office's stamp.
		var office = offer is { Title: { } title, Owner: not null } && Titles.ActsFor( null, title.Office, recipient._character!.Id, StampMarks( recipient._character ) )
			? title.Office : string.Empty;
		recipient.HostShowOffer( $"{Recognition.Label( recipient._character!, _character! )} offers you {offer.What}.", office );
		if ( Network.Owner is { } owner ) Chat.Tell( owner, $"You offer {offer.What}." );
		return Result.Success();
	}

	/// <summary>Host: the offered things pass from the giver's hands to this character's, as one saved change.</summary>
	private Result HostTakeOffered( HexagonServices game, CharacterData from, Offer offer )
	{
		if ( from.Inventory.Items.FirstOrDefault( value => value.Id == offer.ItemId ) is not { } stack || stack.Count < offer.Count )
			return Result.Fail( ErrorCode.NotFound, "They no longer have it." );
		if ( stack.Worn ) return Result.Fail( ErrorCode.Conflict, "They are wearing it now." );
		var moved = game.Transfers!.Move( from, _character!, offer.ItemId, Actor.Of( from ), offer.Count );
		if ( !moved.Ok ) return moved;
		game.SaveTogether( from, _character! );
		HostRefresh( from );
		HostRefresh( _character! );
		return moved;
	}

	/// <summary>
	/// Host: the title passes from whoever held it when it was offered to this character, or, taken for
	/// its office, back to the office. The giver must still act for it, and it must still be held as it was.
	/// </summary>
	private Result HostTakeTitle( Offer offer, Property title, CharacterData from, bool forOffice )
	{
		if ( !title.IsValid() || title.HostOwner != offer.Owner || !Titles.ActsFor( title.HostOwner, title.Office, from.Id, StampMarks( from ) ) )
			return Result.Fail( ErrorCode.NotFound, "It is no longer theirs to give." );
		if ( !forOffice )
		{
			title.HostAssign( _character!, Actor.Of( from ) );
			return Result.Success();
		}
		if ( offer.Owner is null ) return Result.Fail( ErrorCode.Conflict, "It is its office's already." );
		if ( !Titles.ActsFor( null, title.Office, _character!.Id, StampMarks( _character ) ) ) return Result.Fail( ErrorCode.Denied, "You carry nothing that acts for its office." );
		title.HostRelease( Actor.Of( from ), "handed back" );
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

	private void HostShowOffer( string text, string office = "" )
	{
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveOffer( text, office );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveOffer( string text, string office )
	{
		OfferText = text;
		OfferOffice = office;
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
