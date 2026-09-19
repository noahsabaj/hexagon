#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A shop placed in the scene. It is a holder: its stock is items it holds and its till is tokens
/// it holds, so it can run out of either. Goods enter the world from its declared restock source,
/// up to a limit, and the till starts from a declared float. Nothing else is ever created: what a
/// customer pays goes into the till, and what the vendor pays for goods comes out of it.
/// </summary>
[Title( "Hexagon Vendor" ), Category( "Hexagon" ), Icon( "storefront" )]
public sealed class Vendor : Component, Component.IPressable, IVerbTarget
{
	public const string RestockSource = "vendor.restock";
	public const string FloatSource = "vendor.float";

	[Property] public string Title { get; set; } = "Vendor";
	[Property] public List<ItemDefinition> Stock { get; set; } = new();
	/// <summary>How many of each item it keeps on the shelf.</summary>
	[Property, Range( 1, 10 )] public int StockLimit { get; set; } = 3;
	[Property] public float RestockSeconds { get; set; } = 600f;
	/// <summary>Tokens in the till the first time the vendor opens, from the float source.</summary>
	[Property] public long Float { get; set; } = 200;
	/// <summary>What it charges, as a multiple of an item's value.</summary>
	[Property] public float SellFactor { get; set; } = 1f;
	/// <summary>What it pays for goods it stocks, as a multiple of their value. Zero means it does not buy.</summary>
	[Property] public float BuyFactor { get; set; } = 0.5f;
	/// <summary>A capability needed to trade here, such as a permit, or empty for anyone.</summary>
	[Property] public string Requires { get; set; } = string.Empty;

	private bool _joined;
	private RealTimeSince _sinceRestock;

	public IReadOnlyList<Verb> Verbs => new[] { new Verb( "vendor.trade", "Trade", Requires.Length == 0 ? null : Requires ) };

	protected override void OnUpdate()
	{
		if ( !_joined )
		{
			_joined = HostScene.Join( this );
			if ( _joined ) Restock( first: true );
		}
		else if ( Networking.IsHost && _sinceRestock > RestockSeconds ) Restock( first: false );
	}

	/// <summary>Host: the vendor's holder, created with its float the first time.</summary>
	public HolderData Holder()
	{
		var game = GameManager.Instance!;
		var existed = game.Holders!.Find( GameObject.Id ) is not null;
		var holder = game.Holders.GetOrCreate( GameObject.Id, HolderKinds.Vendor, Title, 8, 6 );
		if ( !existed && Float > 0 )
		{
			game.Transfers!.IssueTokens( FloatSource, holder, Float, Actor.Console );
			game.Holders.Save( holder );
		}
		return holder;
	}

	private void Restock( bool first )
	{
		_sinceRestock = 0;
		if ( GameManager.Instance is not { Transfers: { } transfers, Holders: { } holders } ) return;
		var holder = Holder();
		var changed = false;
		foreach ( var definition in Stock.Where( value => value.IsValid() ) )
		{
			var missing = StockLimit - holder.Inventory.Items.Count( item => item.Definition == definition.ResourcePath );
			for ( var index = 0; index < missing; index++ )
				changed |= transfers.Issue( RestockSource, holder, definition.ResourcePath, definition.Width, definition.Height, Actor.Console ).Ok;
		}
		if ( !changed ) return;
		holders.Save( holder );
		Player.HostRefresh( holder );
	}

	public long SellPrice( ItemDefinition? definition ) => Trade.Price( definition?.Value ?? 0, SellFactor );

	/// <summary>What the vendor pays for one, or null if it does not deal in it.</summary>
	public long? BuyPrice( ItemDefinition? definition ) =>
		definition is not null && BuyFactor > 0 && Stock.Contains( definition ) ? Trade.Price( definition.Value, BuyFactor ) : null;

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, "vendor.trade" );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new IPressable.Tooltip( $"Trade with {Title}", "storefront", string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		actor.HostOpen( this, Holder(), Title );
		return Result.Success();
	}
}
