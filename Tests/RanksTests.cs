using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Ranks: a superior's act, as far as their rank allows, and only on those below them.</summary>
[TestClass]
public sealed class RanksTests
{
	// Lowest first, as a faction's asset lists them. An officer enlists, and promotes as far as a unit;
	// the commander does everything, up to officer.
	private static readonly RankRule[] Police =
	{
		new( "Recruit" ),
		new( "Unit" ),
		new( "Officer", Enlists: true, Promotes: true, PromotesTo: 1 ),
		new( "Commander", Enlists: true, Promotes: true, Discharges: true )
	};

	private const int Recruit = 0, Unit = 1, Officer = 2, Commander = 3;

	[TestMethod]
	public void ARankIsFoundByItsTitleAndAMemberAlwaysStandsSomewhere()
	{
		Assert.AreEqual( Officer, Ranks.IndexOf( Police, "officer" ), "titles are not case sensitive" );
		Assert.AreEqual( -1, Ranks.IndexOf( Police, "General" ) );
		Assert.AreEqual( Recruit, Ranks.Standing( Police, "General" ), "a rank the faction no longer has is the bottom, for a superior to set right" );
		Assert.AreEqual( Commander, Ranks.Standing( Police, "Commander" ) );
		Assert.AreEqual( -1, Ranks.Standing( System.Array.Empty<RankRule>(), "Commander" ), "a faction without ranks gives nobody a place in it" );
	}

	[TestMethod]
	public void OnlyARankThatEnlistsTakesInARecruitWhoBelongsNowhere()
	{
		Assert.IsTrue( Ranks.ToEnlist( Police, Officer, recruitBelongs: false ).Ok );
		Assert.IsTrue( Ranks.ToEnlist( Police, Commander, recruitBelongs: false ).Ok );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToEnlist( Police, Unit, recruitBelongs: false ).Code, "a unit enlists nobody" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToEnlist( Police, -1, recruitBelongs: false ).Code, "nor does someone outside it" );
		Assert.AreEqual( ErrorCode.Conflict, Ranks.ToEnlist( Police, Officer, recruitBelongs: true ).Code, "a member of any faction leaves it first" );
		Assert.AreEqual( ErrorCode.Invalid, Ranks.ToEnlist( System.Array.Empty<RankRule>(), 0, recruitBelongs: false ).Code );
	}

	[TestMethod]
	public void ARankIsGivenOnlyBelowOnesOwnAndUpToItsLimit()
	{
		CollectionAssert.AreEqual( new[] { Recruit, Unit }, Ranks.Giveable( Police, Officer ).ToArray(), "an officer makes units, not officers" );
		CollectionAssert.AreEqual( new[] { Recruit, Unit, Officer }, Ranks.Giveable( Police, Commander ).ToArray(), "no limit is any rank below one's own" );
		Assert.IsEmpty( Ranks.Giveable( Police, Unit ), "a unit promotes nobody" );
		var proud = new[] { new RankRule( "Private" ), new RankRule( "Sergeant", Promotes: true, PromotesTo: 1 ) };
		CollectionAssert.AreEqual( new[] { 0 }, Ranks.Giveable( proud, 1 ).ToArray(), "a limit at or above one's own rank is one's own rank, less one" );

		Assert.IsTrue( Ranks.ToSetRank( Police, Officer, sameFaction: true, Recruit, Unit ).Ok, "promoted" );
		Assert.IsTrue( Ranks.ToSetRank( Police, Commander, sameFaction: true, Officer, Recruit ).Ok, "demoted" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToSetRank( Police, Officer, sameFaction: true, Unit, Officer ).Code, "not to one's own rank" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToSetRank( Police, Officer, sameFaction: true, Officer, Recruit ).Code, "not someone of one's own rank" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToSetRank( Police, Officer, sameFaction: true, Commander, Recruit ).Code, "nor a superior" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToSetRank( Police, Commander, sameFaction: false, Recruit, Unit ).Code, "nor someone of another faction" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToSetRank( Police, Unit, sameFaction: true, Recruit, Unit ).Code, "a unit promotes nobody" );
		Assert.AreEqual( ErrorCode.Invalid, Ranks.ToSetRank( Police, Commander, sameFaction: true, Recruit, -1 ).Code, "a rank the faction does not have" );
		Assert.AreEqual( ErrorCode.Conflict, Ranks.ToSetRank( Police, Commander, sameFaction: true, Unit, Unit ).Code );
	}

	[TestMethod]
	public void ARankIsSaidWithTheArticleItIsHeardWith()
	{
		Assert.AreEqual( "a Unit", Ranks.Article( "Unit" ), "a u said as you" );
		Assert.AreEqual( "an Officer", Ranks.Article( "Officer" ) );
		Assert.AreEqual( "an Unteroffizier", Ranks.Article( "Unteroffizier" ) );
		Assert.AreEqual( "a Divisional Leader", Ranks.Article( "Divisional Leader" ) );
		Assert.AreEqual( "A Unit", Ranks.Article( "Unit", capital: true ) );
		Assert.AreEqual( "An Officer", Ranks.Article( "Officer", capital: true ) );
	}

	[TestMethod]
	public void OnlyARankThatDischargesSendsAMemberBelowItAway()
	{
		Assert.IsTrue( Ranks.ToDischarge( Police, Commander, sameFaction: true, Officer ).Ok );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToDischarge( Police, Officer, sameFaction: true, Recruit ).Code, "an officer enlists, but discharges nobody" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToDischarge( Police, Commander, sameFaction: true, Commander ).Code, "not someone of one's own rank" );
		Assert.AreEqual( ErrorCode.Denied, Ranks.ToDischarge( Police, Commander, sameFaction: false, Recruit ).Code, "nor someone of another faction" );
	}
}
