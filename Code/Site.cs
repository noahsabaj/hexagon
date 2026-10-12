#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A place in the world that yields something to whoever works it: a mine, a farm, a forest, a scrap
/// yard. It holds a stock that grows back over time up to the most it holds, and each piece of work,
/// holding Use until it is done with the right tool in hand, takes some out, into the worker's hands.
/// An empty site yields nothing until it has grown back. See <see cref="Sites"/>.
/// </summary>
[Title( "Hexagon Site" ), Category( "Hexagon" ), Icon( "landscape" )]
public sealed class Site : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Scrap yard";
	/// <summary>What the work is called, such as "Salvage scrap".</summary>
	[Property] public string Work { get; set; } = "Salvage";
	[Property] public ItemDefinition? Yields { get; set; }
	/// <summary>How many things one piece of work takes out, when the site holds that many.</summary>
	[Property, Range( 1, 100 )] public int PerWork { get; set; } = 5;
	/// <summary>How long one piece of work takes, for as long as the worker keeps at it.</summary>
	[Property, Range( 0.5f, 120f )] public float Seconds { get; set; } = 5f;
	/// <summary>The most it holds, and what it holds before anyone has worked it.</summary>
	[Property, Range( 1, 1000 )] public int Most { get; set; } = 20;
	/// <summary>How many things grow back in an hour.</summary>
	[Property, Range( 0f, 1000f )] public float PerHour { get; set; } = 10f;
	/// <summary>What the thing in hand must grant to work it, such as "site.salvage". Empty: bare hands do.</summary>
	[Property] public string Needs { get; set; } = string.Empty;

	public IReadOnlyList<Verb> Verbs { get; } = new[] { new Verb( VerbIds.SiteWork, "Work" ) };

	private bool _joined;

	protected override void OnUpdate()
	{
		if ( !_joined ) _joined = HostScene.Join( this );
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	// Use is held for as long as the work goes on, and letting go stops it.
	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, VerbIds.SiteWork );
		return true;
	}

	void IPressable.Release( IPressable.Event e ) => Player.Local?.RequestStopWork();

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Work the {Title.ToLowerInvariant()}", "landscape",
			$"{Work}: {Yields?.Title} x{PerWork}. Hold to work it{(Needs.Length > 0 ? ", with the right tool in hand" : string.Empty)}." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( !Yields.IsValid() ) return Result.Fail( ErrorCode.Invalid, "There is nothing here to take." );
		Guid? tool = null;
		if ( Needs.Length > 0 )
		{
			if ( actor.HostInHand( Needs ) is not { } held ) return Result.Fail( ErrorCode.Denied, "You need the right tool in hand to work it." );
			tool = held.Id;
		}
		if ( Sites.Take( Held(), PerWork ) < 1 ) return Result.Fail( ErrorCode.Conflict, Sites.Empty );
		return actor.HostStartWork( this, Work, Seconds, tool, () => HostYield( actor ) );
	}

	/// <summary>Host: a piece of work is done, and what it took out is in the worker's hands, or nothing changes. Returns what the worker is told.</summary>
	private string HostYield( Player actor )
	{
		if ( HexagonServices.Current is not { Transfers: { } transfers } game || actor.HostCharacter is not { } character ) return "You stop working.";
		var count = Sites.Take( Held(), PerWork );
		if ( count < 1 ) return Sites.Empty;
		var taken = Yields!.IssueTo( transfers, Sources.Site, character, Actor.Of( character ), count );
		if ( !taken.Ok ) return taken.Message;
		Remembered().Held -= count;
		game.SaveTogether( character, game.World );
		Player.HostRefresh( character );
		return $"Done: {Yields.Title} x{count}.";
	}

	/// <summary>Host: what it holds now, grown back since it was last looked at.</summary>
	private double Held()
	{
		var data = Remembered();
		var now = DateTime.UtcNow;
		data.Held = Sites.Grown( data.Held, data.Since, now, PerHour, Most );
		data.Since = now;
		return data.Held;
	}

	/// <summary>Host: what it remembers, kept with the world. Before anyone has worked it, it holds the most it can.</summary>
	private SiteData Remembered()
	{
		var world = HexagonServices.Current!.World;
		if ( !world.Sites.TryGetValue( GameObject.Id, out var data ) ) world.Sites[GameObject.Id] = data = new SiteData { Held = Most, Since = DateTime.UtcNow };
		return data;
	}
}
