#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// Maps, from the side of whoever leans over one. A map laid out on a table is marked up by those
// standing at it: lines and words in grease pencil, which anyone there can wipe off again, and the
// counters that came with it, moved by hand. Everyone who can see the table sees each mark once it
// is made. What goes on a map is on record with whoever put it there, which the map does not say.
public sealed partial class Player
{
	/// <summary>Owner: the map this character is leaning over, if any. Its view is the map's, from above.</summary>
	public WorldItem? LeaningOver { get; private set; }

	// Host-side: the map this character is leaning over.
	private WorldItem? _leaning;

	/// <summary>The ink of a pen, for something to write with that says no other.</summary>
	public static readonly Color PenInk = new( 0.12f, 0.16f, 0.43f );

	/// <summary>Host: this character leans over a map laid out in front of it, to mark it up.</summary>
	internal Result HostLean( WorldItem map )
	{
		if ( Network.Owner is not { } owner ) return Result.Fail( ErrorCode.Internal, "There is nobody to lean over it." );
		_leaning = map;
		using ( Rpc.FilterInclude( owner ) ) ReceiveLean( map );
		return Result.Success();
	}

	/// <summary>Owner: stands up from the map.</summary>
	public void StandUp()
	{
		LeaningOver = null;
		PrivateVersion++;
		RequestStandUp();
	}

	[Rpc.Host]
	private void RequestStandUp()
	{
		if ( Authorize( nameof( RequestStandUp ), out _, out _, needsCharacter: false ) ) HostStopLeaning();
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveLean( WorldItem? map )
	{
		LeaningOver = map;
		PrivateVersion++;
	}

	/// <summary>Host: someone leans over a map only while it is still there, they are able, and they stand at it.</summary>
	private void HostLeanTick()
	{
		if ( _leaning is null ) return;
		var still = _leaning.IsValid() && !IsIncapable
			&& _leaning.GameObject.GetBounds().ClosestPoint( HostPosition ).Distance( HostPosition ) <= ((IVerbTarget)_leaning).Reach;
		if ( !still ) HostStopLeaning( tell: true );
	}

	/// <summary>Host: this character stands up from the map. Told, its owner's view comes back to it.</summary>
	private void HostStopLeaning( bool tell = false )
	{
		if ( _leaning is null ) return;
		_leaning = null;
		if ( !tell || Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveLean( null );
	}

	/// <summary>Draws a line on the map leant over, through points as <see cref="Maps.Points"/> reads them.</summary>
	[Rpc.Host]
	public void RequestMapDraw( WorldItem map, string points )
	{
		if ( !Authorize( nameof( RequestMapDraw ), out var caller, out var game ) ) return;
		var line = Maps.Points( points );
		HostMarkMap( caller, game, map, JournalKinds.MapDraw,
			!line.Ok ? line.ToResult() : !HostCan( Capability.Write ) ? Result.Fail( ErrorCode.Denied, Papers.NothingToWriteWith ) : null,
			stack => Maps.Draw( stack, line.Value, HostInk() ), ("points", line.Ok ? (line.Value.Count / 2).ToString() : "0") );
	}

	/// <summary>Writes a word on the map leant over, starting at a place on it.</summary>
	[Rpc.Host]
	public void RequestMapWrite( WorldItem map, int x, int y, string text )
	{
		if ( !Authorize( nameof( RequestMapWrite ), out var caller, out var game ) ) return;
		var word = Papers.Clean( text );
		HostMarkMap( caller, game, map, JournalKinds.MapWrite, !HostCan( Capability.Write ) ? Result.Fail( ErrorCode.Denied, Papers.NothingToWriteWith ) : null,
			stack => Maps.Write( stack, x, y, word, HostInk() ), ("text", Clip( word )) );
	}

	/// <summary>Wipes a line or a word off the map leant over. Anyone at the table can.</summary>
	[Rpc.Host]
	public void RequestMapWipe( WorldItem map, int mark )
	{
		if ( !Authorize( nameof( RequestMapWipe ), out var caller, out var game ) ) return;
		HostMarkMap( caller, game, map, JournalKinds.MapWipe, null, stack => Maps.Wipe( stack, mark ), ("mark", mark.ToString()) );
	}

	/// <summary>Stands one of the map's counters on it at a place, or puts it back in its box when <paramref name="onMap"/> is false.</summary>
	[Rpc.Host]
	public void RequestMapMove( WorldItem map, int counter, int x, int y, bool onMap )
	{
		if ( !Authorize( nameof( RequestMapMove ), out var caller, out var game ) ) return;
		var counters = map.IsValid() ? map.Definition?.Counters.Count ?? 0 : 0;
		HostMarkMap( caller, game, map, JournalKinds.MapMove, null, stack => Maps.Move( stack, counters, counter, onMap ? (x, y) : null ),
			("counter", counter.ToString()), ("at", onMap ? $"{x},{y}" : "box") );
	}

	/// <summary>
	/// Host: puts something on the map this character leans over, saves it, and shows it to whoever can
	/// see the map. A refusal is on record like the mark itself.
	/// </summary>
	private void HostMarkMap( Connection caller, HexagonServices game, WorldItem map, string kind, Result? refusal, Func<ItemStack, Result> mark,
		params (string Key, string Value)[] more )
	{
		var holder = map.IsValid() ? game.Holders!.Find( map.HolderId ) : null;
		var stack = holder?.Inventory.Items.FirstOrDefault();
		var outcome = IsIncapable ? Result.Fail( ErrorCode.Denied, "You cannot do that now." )
			: stack is null || map != _leaning || ItemDefinition.Find( stack.Definition ) is not { IsMap: true } ? Result.Fail( ErrorCode.Denied, "Lean over the map first." )
			: refusal ?? Result.Success();
		if ( outcome.Ok ) outcome = mark( stack! );
		HostRecord( kind, stack is null ? null : $"item:{stack.Id:N}", outcome.Ok,
			new[] { ("definition", stack?.Definition ?? string.Empty) }.Concat( more ).Append( ("reason", outcome.Code.ToString()) ).ToArray() );
		if ( !outcome.Ok )
		{
			Chat.Tell( caller, outcome.Message );
			return;
		}
		game.Holders!.Save( holder! );
		map.HostShowMap( stack! );
	}

	/// <summary>Host: the colour this character marks a map in.</summary>
	private string HostInk() => (Writer( _character!.Inventory, value => value.Id == _character.Equipped )?.MapInk ?? PenInk).Hex;

	/// <summary>Owner: the colour this character would mark a map in, to show a line as it is drawn.</summary>
	public Color Ink => (Inventory is { } carried ? Writer( carried, value => value.Definition == HeldItemPath )?.MapInk : null) ?? PenInk;

	/// <summary>What a character writes with: what it holds in hand, if that writes, or else the first thing it carries that does.</summary>
	private static ItemDefinition? Writer( InventoryData inventory, Func<ItemStack, bool> inHand )
	{
		var writers = inventory.Items.Select( value => (Stack: value, Definition: ItemDefinition.Find( value.Definition )) )
			.Where( value => value.Definition is { } definition && Capabilities.Can( Capability.Write, definition.Grants ) ).ToList();
		return writers.FirstOrDefault( value => inHand( value.Stack ) ).Definition ?? writers.FirstOrDefault().Definition;
	}
}
