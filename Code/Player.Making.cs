#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// A character at work: at a machine or a site, holding Use, or with a tool in hand. The host times
// the work and finishes it once it is done, if the worker kept at it. Walking away, letting go,
// putting the tool away or being unable to go on stops it, and then nothing comes of it.
public sealed partial class Player
{
	/// <summary>Owner: what this character is working at, empty when it is not working.</summary>
	public string WorkTitle { get; private set; } = string.Empty;
	/// <summary>Owner: how long the work takes, all told.</summary>
	public float WorkSeconds { get; private set; }
	/// <summary>Owner: how long it has been at it.</summary>
	public RealTimeSince WorkBegan { get; private set; }

	/// <summary>How far a character working with a tool in hand can shift before it has stopped working.</summary>
	public const float WorkStill = 24f;

	/// <summary>
	/// Work under way: what it is at, which may be this character for a tool in hand; the tool that must
	/// stay in hand, if any; where the worker stood; when it is done; and what finishing it does, which
	/// returns what the worker is told.
	/// </summary>
	private sealed record Work( Component At, Guid? Tool, Vector3 Where, TimeUntil Done, Func<string> Finish );

	// Host-side.
	private Work? _work;

	/// <summary>
	/// Host: this character starts a piece of work at <paramref name="at"/>, which takes
	/// <paramref name="seconds"/> and is then finished by <paramref name="finish"/>. A tool's work goes on
	/// only while the tool stays in hand; work at something in the world, only while it is in reach.
	/// </summary>
	internal Result HostStartWork( Component at, string title, float seconds, Guid? tool, Func<string> finish )
	{
		if ( _character is null ) return Result.Fail( ErrorCode.Denied, "You have no character." );
		_work = new Work( at, tool, HostPosition, seconds, finish );
		if ( Network.Owner is { } owner ) using ( Rpc.FilterInclude( owner ) ) ReceiveWork( title, seconds );
		return Result.Success();
	}

	/// <summary>
	/// Host: this character starts the first piece of making there is enough for in <paramref name="from"/>,
	/// to put what it makes in <paramref name="to"/>, marked by <paramref name="maker"/>.
	/// </summary>
	internal Result HostStartMaking( Component at, IHolder from, IHolder to, IEnumerable<MakingRecipe> recipes, string maker, Machine? numbering, Guid? tool = null )
	{
		if ( recipes.Where( value => value.IsValid ).FirstOrDefault( value => Making.Enough( value.Rule, from.Inventory ) ) is not { } recipe )
			return Result.Fail( ErrorCode.NotFound, Making.NothingToMakeFrom );
		return HostStartWork( at, recipe.Title, recipe.Seconds, tool, () => HostMake( from, to, recipe, maker, numbering ) );
	}

	/// <summary>Host: the work goes on, stops, or is done.</summary>
	private void HostWorkTick()
	{
		if ( _work is not { } work ) return;
		var stopped = IsIncapable || !work.At.IsValid() ? "You stop working."
			: work.Tool is { } tool && _character?.Equipped != tool ? "You stop working."
			: work.At == this ? HostPosition.Distance( work.Where ) > WorkStill ? "You stop working." : null
			: work.At.GameObject.GetBounds().ClosestPoint( HostPosition ).Distance( HostPosition ) > ((IVerbTarget)work.At).Reach ? "You stop working: you walked away." : null;
		if ( stopped is not null )
		{
			HostStopWork( stopped );
			return;
		}
		if ( !work.Done ) return;
		// Stopped first, so whatever finishing it tells the worker is the last word.
		_work = null;
		HostStopWork( work.Finish() );
	}

	/// <summary>Host: making is done. What it was made from is used up and what it makes is there, or nothing changes. Returns what the worker is told.</summary>
	private string HostMake( IHolder from, IHolder to, MakingRecipe recipe, string maker, Machine? numbering )
	{
		if ( HexagonServices.Current is not { Transfers: { } transfers } game || _character is null ) return "You stop working.";
		var output = recipe.Output!;
		var numbered = recipe.Serial && numbering is not null;
		var first = numbered ? numbering!.Remembered().NextSerial : 0;
		var marks = Making.Marks( maker, recipe.Count, numbered ? numbering!.SerialPrefix : null, first );
		var made = new ItemStack { Definition = output.ResourcePath, Width = output.Width, Height = output.Height, Max = Math.Max( 1, output.MaxStack ) };
		var done = transfers.Make( from, to, recipe.Rule.Inputs, made, recipe.Count, marks, maker, Actor.Of( _character ) );
		if ( !done.Ok ) return done.Message;
		if ( numbered ) numbering!.Remembered().NextSerial = first + recipe.Count;
		if ( numbered ) game.SaveTogether( from, to, game.World );
		else game.SaveTogether( from, to );
		HostRefresh( from );
		if ( !ReferenceEquals( from, to ) ) HostRefresh( to );
		return $"Done: {output.Title} x{recipe.Count}.";
	}

	/// <summary>Host: the work ends, or has just finished, and the worker is told why, unless they stopped it themselves.</summary>
	private void HostStopWork( string? tell = null )
	{
		if ( _work is null && tell is null ) return;
		_work = null;
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveWork( string.Empty, 0f );
		if ( tell is not null ) Chat.Tell( owner, tell );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveWork( string title, float seconds )
	{
		WorkTitle = title;
		WorkSeconds = seconds;
		WorkBegan = 0;
		PrivateVersion++;
	}

	/// <summary>Lets go of the work: Use let go of at a machine, or the work bar's Stop.</summary>
	[Rpc.Host]
	public void RequestStopWork()
	{
		if ( Authorize( nameof( RequestStopWork ), out _, out _, needsCharacter: false ) ) HostStopWork();
	}

	/// <summary>Host: works with the tool in hand, on what the character carries.</summary>
	private Result MakeWith( ItemStack tool, ItemDefinition definition )
	{
		if ( _character!.Equipped != tool.Id ) return Result.Fail( ErrorCode.Denied, $"Hold the {definition.InSentence} to work with it." );
		return HostStartMaking( this, _character, _character, definition.Makes, $"tool:{tool.Id:N}", null, tool.Id );
	}

	/// <summary>Host: the item in this character's hand, when it grants <paramref name="capability"/>.</summary>
	internal ItemStack? HostInHand( string capability )
	{
		if ( _character?.Equipped is not { } equipped || _character.Inventory.Items.FirstOrDefault( value => value.Id == equipped ) is not { } held ) return null;
		return Capabilities.Can( capability, ItemDefinition.Find( held.Definition )?.Grants ?? Enumerable.Empty<string>() ) ? held : null;
	}

	/// <summary>Host: looks hard at a pile with the right tool, and is told whether its things were made alike.</summary>
	private Result Compare( Connection caller, ItemStack pile )
	{
		if ( !HostCan( Capability.Compare ) ) return Result.Fail( ErrorCode.Denied, "You have nothing to tell them apart with." );
		Chat.Tell( caller, Making.Compare( pile ) );
		return Result.Success();
	}
}
