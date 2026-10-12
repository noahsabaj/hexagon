#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Rank is given by a superior's act, in person. A recruiter offers a newcomer a place in the faction,
// which they accept or refuse like anything handed over; a superior gives those below them another
// rank, or discharges them; and anyone can walk out. What their rank allows each superior is the
// faction's to say, rank by rank.
public sealed partial class Player
{
	/// <summary>Owner: whether this character's rank lets it do something to those below it, one of <see cref="RankPower"/>.</summary>
	public bool HasRankPower( string power )
	{
		if ( Faction is not { } faction ) return false;
		var ranks = faction.Rules;
		var standing = Logic.Ranks.Standing( ranks, RankTitle );
		return power switch
		{
			RankPower.Enlist => Logic.Ranks.CanEnlist( ranks, standing ),
			RankPower.Promote => Logic.Ranks.Giveable( ranks, standing ).Count > 0,
			RankPower.Discharge => Logic.Ranks.CanDischarge( ranks, standing ),
			_ => false
		};
	}

	/// <summary>Owner: the ranks this character may give those below it, lowest first.</summary>
	public IReadOnlyList<FactionRank> GiveableRanks => Faction is { } faction
		? Logic.Ranks.Giveable( faction.Rules, Logic.Ranks.Standing( faction.Rules, RankTitle ) ).Select( index => faction.Ranks[index] ).ToArray()
		: Array.Empty<FactionRank>();

	/// <summary>Gives someone under this character another rank, one its own rank lets it give.</summary>
	[Rpc.Host]
	public void RequestSetRank( CharacterBody person, string rank )
	{
		if ( !Authorize( nameof( RequestSetRank ), out var caller, out var game ) ) return;
		HostAct( caller, nameof( RequestSetRank ), person, VerbIds.PersonRank,
			( _, _ ) => person.HostCharacter is { } member && member != _character
				? game.Characters!.SetRank( member, _character!, rank ?? string.Empty )
				: Result.Fail( ErrorCode.NotFound, "There is nobody there." ),
			("rank", Clip( rank ?? string.Empty )) );
	}

	/// <summary>This character walks out of its faction of its own accord, keeping whatever it was issued.</summary>
	[Rpc.Host]
	public void RequestLeaveFaction()
	{
		if ( !Authorize( nameof( RequestLeaveFaction ), out var caller, out var game ) ) return;
		// Nobody walks out of anything with their hands bound, or lying in the street.
		var left = IsIncapable ? Result.Fail( ErrorCode.Denied, "You cannot do that now." ) : game.Characters!.Leave( _character! );
		if ( left.Ok ) return;
		HostRecord( JournalKinds.FactionLeave, ok: false, data: ("reason", left.Code.ToString()) );
		Chat.Tell( caller, left.Message );
	}

	/// <summary>
	/// Host: offers someone a place in this character's faction, at its lowest rank, if its rank lets
	/// it enlist. Whether they may join is weighed again when they accept.
	/// </summary>
	internal Result HostOfferEnlistment( Player recruit )
	{
		var (faction, ranks, standing) = CharacterService.StandingOf( _character! );
		var can = faction is null ? Result.Fail( ErrorCode.Denied, Logic.Ranks.NoPower )
			: Logic.Ranks.ToEnlist( ranks, standing, recruit.IsValid() && recruit._character?.Faction.Length > 0 );
		if ( !can.Ok ) return can;
		return HostPlaceOffer( recruit, new Offer( this, Guid.Empty, 0, $"a place in {faction!.Title}, as {Ranks.Article( ranks[0].Title )}", 0, faction ) );
	}
}
