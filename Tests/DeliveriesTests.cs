using System;
using System.Linq;
using Hexagon.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hexagon.Tests;

/// <summary>Deliveries and sites: goods come by train at set times, and sites grow back slowly; nothing stacks up.</summary>
[TestClass]
public sealed class DeliveriesTests
{
	private static readonly DateTimeOffset Now = new( 2026, 10, 12, 12, 0, 0, TimeSpan.Zero );

	private static TimeSpan At( string text ) => Deliveries.TimeOfDay( text ).Value;

	[TestMethod]
	public void ATimeOfDayIsWrittenAsHoursAndMinutes()
	{
		Assert.AreEqual( new TimeSpan( 6, 0, 0 ), At( "06:00" ) );
		Assert.AreEqual( new TimeSpan( 18, 30, 0 ), At( " 18:30 " ) );
		Assert.AreEqual( new TimeSpan( 7, 5, 0 ), At( "7:05" ) );
		foreach ( var wrong in new[] { "", "6", "24:00", "noon", "06:60", null } )
			Assert.IsFalse( Deliveries.TimeOfDay( wrong ).Ok, $"'{wrong}' is not a time of day" );
	}

	[TestMethod]
	public void ATrainComesAtItsNextTimeAndOneMissedNeverComes()
	{
		var times = new[] { At( "06:00" ), At( "18:00" ) };
		var morning = new DateTime( 2026, 10, 12, 5, 59, 0 );
		Assert.AreEqual( new DateTime( 2026, 10, 12, 6, 0, 0 ), Deliveries.Next( times, morning ), "before six, the six o'clock" );
		Assert.AreEqual( new DateTime( 2026, 10, 12, 18, 0, 0 ), Deliveries.Next( times, morning.AddMinutes( 1 ) ), "at six, the next is the evening's" );
		// A server down from five in the morning until eight at night missed both: the next is tomorrow's first.
		Assert.AreEqual( new DateTime( 2026, 10, 13, 6, 0, 0 ), Deliveries.Next( times, new DateTime( 2026, 10, 12, 20, 0, 0 ) ), "missed trains never come" );
		Assert.IsNull( Deliveries.Next( Array.Empty<TimeSpan>(), morning ), "a train with no times never comes by itself" );
	}

	[TestMethod]
	public void AManifestListsEachKindOnce()
	{
		var lines = Deliveries.Lines( new[] { ("Ration", 10), ("Water", 20), ("Ration", 10) } );
		CollectionAssert.AreEqual( new[] { "Ration x20", "Water x20" }, lines.ToArray() );
		Assert.AreEqual( "Ration x20, Water x20", Deliveries.Summary( new[] { ("Ration", 20), ("Water", 20) } ) );
		Assert.AreEqual( "nothing", Deliveries.Summary( Array.Empty<(string, int)>() ) );
	}

	[TestMethod]
	public void ASiteGrowsBackSlowlyUpToItsMost()
	{
		var since = Now.UtcDateTime;
		Assert.AreEqual( 5d, Sites.Grown( 0, since, since.AddMinutes( 30 ), perHour: 10, most: 20 ), 1e-9, "half an hour grows half an hour's worth" );
		Assert.AreEqual( 20d, Sites.Grown( 15, since, since.AddHours( 5 ), perHour: 10, most: 20 ), "never past its most" );
		Assert.AreEqual( 7d, Sites.Grown( 7, since, since.AddHours( -1 ), perHour: 10, most: 20 ), "a clock set back grows nothing, and takes nothing" );
		Assert.AreEqual( 5, Sites.Take( 12.5, 5 ), "a piece of work takes what it takes" );
		Assert.AreEqual( 3, Sites.Take( 3.9, 5 ), "or what whole things are left" );
		Assert.AreEqual( 0, Sites.Take( 0.9, 5 ), "and nothing from less than one" );
	}

	[TestMethod]
	public void WhatATrainBringsAndTakesAwayIsOnTheRecord()
	{
		var journal = new Journal( new MemoryFiles(), () => Now );
		var transfers = new Transfers( journal );
		var wagon = new HolderData { Id = Guid.NewGuid(), Kind = HolderKinds.Wagon, Inventory = new InventoryData { Width = 4, Height = 2 } };
		Assert.IsTrue( transfers.Issue( Sources.Delivery, wagon, "items/ration.item", 1, 1, Actor.Console, count: 20, max: 10 ).Ok );
		Assert.IsTrue( transfers.Issue( Sources.Delivery, wagon, "items/water.item", 1, 1, Actor.Console, count: 5, max: 10 ).Ok );
		transfers.DestroyAll( Sinks.Departed, wagon, Actor.Console );
		Assert.IsEmpty( wagon.Inventory.Items, "what was left on board went with it" );
		var entries = journal.Read( Now ).ToList();
		Assert.AreEqual( 25, entries.Where( value => value.Kind == JournalKinds.ItemIssue && value.Data["source"] == Sources.Delivery ).Sum( value => int.Parse( value.Data["count"] ) ), "everything it brought came from the delivery" );
		Assert.AreEqual( 25, entries.Where( value => value.Kind == JournalKinds.ItemDestroy && value.Data["sink"] == Sinks.Departed ).Sum( value => int.Parse( value.Data["count"] ) ), "and everything it took left through departure" );
	}
}
