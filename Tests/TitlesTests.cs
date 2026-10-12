using System;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Titles: whoever holds one acts for it, and while nobody does, whoever carries its office's stamp.</summary>
[TestClass]
public sealed class TitlesTests
{
	private const string Office = "Combine Civil Authority";
	private static readonly Guid Owner = Guid.NewGuid();
	private static readonly Guid Clerk = Guid.NewGuid();

	[TestMethod]
	public void AnOwnerActsForTheirTitleAndNobodyElseDoes()
	{
		Assert.IsTrue( Titles.ActsFor( Owner, Office, Owner, Array.Empty<string>() ) );
		Assert.IsFalse( Titles.ActsFor( Owner, Office, Clerk, new[] { Office } ), "the office's stamp sells nothing a character holds" );
	}

	[TestMethod]
	public void WhileNobodyHoldsItWhoeverCarriesItsOfficesStampActsForIt()
	{
		Assert.IsTrue( Titles.ActsFor( null, Office, Clerk, new[] { "Some other office", " combine civil authority " } ), "the mark is the office's whatever its case" );
		Assert.IsFalse( Titles.ActsFor( null, Office, Clerk, new[] { "Some other office" } ) );
		Assert.IsFalse( Titles.ActsFor( null, Office, Owner, Array.Empty<string>() ), "a faction is nothing: only the stamp counts" );
		Assert.IsFalse( Titles.ActsFor( null, string.Empty, Clerk, new[] { string.Empty, Office } ), "a building with no office is nobody's to sell" );
	}

	[TestMethod]
	public void ALockIsFittedOnlyByWhoeverActsForItWhereTheyCanGetAtIt()
	{
		Assert.AreEqual( Titles.NothingToFit, Titles.ToFitLock( false, true, true, true, true ) );
		Assert.AreEqual( Titles.NothingThere, Titles.ToFitLock( true, false, true, true, true ) );
		Assert.AreEqual( Titles.NotYoursToChange, Titles.ToFitLock( true, true, false, true, true ) );
		Assert.AreEqual( Titles.DoorShut, Titles.ToFitLock( true, true, true, true, false ), "a lock is changed from a door's edge, so it must stand open" );
		Assert.AreEqual( Titles.LockerLocked, Titles.ToFitLock( true, true, true, false, false ) );
		Assert.IsNull( Titles.ToFitLock( true, true, true, true, true ) );
		Assert.IsNull( Titles.ToFitLock( true, true, true, false, true ) );
	}

	[TestMethod]
	public void ANewPatternIsOneStaffCouldTypeAndNeverTheSameTwice()
	{
		var first = Keys.NewPattern();
		Assert.IsTrue( Keys.Pattern( first ).Ok, first );
		Assert.AreEqual( first, Keys.Pattern( first ).Value, "it is already as a typed one is kept" );
		Assert.AreNotEqual( first, Keys.NewPattern() );
	}
}
