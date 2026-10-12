#nullable enable

using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

// A building's locks are changed by whoever holds its title, or carries its office's stamp while it is
// the office's. The new lock goes in from a door's edge while it stands open, or into a locker they can
// open, and the old one comes out in their hands, cut as it was, to be kept, sold or fitted elsewhere.
// Every key cut to the old lock turns nothing there any more; the keys made with the new one do.
public sealed partial class Player
{
	/// <summary>How far from a door or a locker a character can be to fit a lock to it.</summary>
	public const float FitReach = 100f;

	/// <summary>Host: fits a lock this character carries to the door or the locker in front of it.</summary>
	private Result FitLock( HexagonServices game, ItemStack stack, ItemDefinition? definition )
	{
		var character = _character!;
		var target = HostLockableInFront();
		var lockable = target as ILockable;
		var title = target is null ? null : Property.Of( target );
		var actsFor = title is not null && Titles.ActsFor( title.HostOwner, title.Office, character.Id, StampMarks( character ) );
		var open = target is Door door ? door.IsOpen : lockable is not null && (lockable.HostPattern.Length == 0 || HostHasKey( lockable.HostPattern ));
		var refusal = Titles.ToFitLock( definition is { IsLock: true }, target is not null, actsFor, target is Door, open );
		if ( refusal is not null ) return Result.Fail( ErrorCode.Denied, refusal );
		if ( stack.Pattern is not { Length: > 0 } cut ) return Result.Fail( ErrorCode.Invalid, "It is cut to nothing: no key would turn it." );
		var was = lockable!.HostPattern;
		if ( Keys.Fits( cut, was ) ) return Result.Fail( ErrorCode.Conflict, "The lock in it is cut the same already." );
		// The old lock comes out into the hands the new one left, which makes room for it, unless the new one was one of a pile.
		if ( was.Length > 0 && stack.Count > 1 && !HasRoom( character.Inventory, stack.Width, stack.Height ) )
			return Result.Fail( ErrorCode.Conflict, "You have no room for the lock that comes out." );
		var by = Actor.Of( character );
		var gone = game.Transfers!.Destroy( Sinks.Fitted, character, stack.Id, by, 1 );
		if ( !gone.Ok ) return gone;
		if ( was.Length > 0 ) definition!.IssueTo( game.Transfers, Sources.Fitted, character, by, 1, was );
		lockable.HostFit( cut );
		game.SaveTogether( character, game.World );
		HostRefresh( character );
		var subject = $"{(target is Door ? "Door" : "Container")}:{target!.GameObject.Id:N}";
		HostRecord( JournalKinds.LockFit, subject, true, ("property", title!.Title), ("target", target.GameObject.Name), ("pattern", cut), ("was", was) );
		if ( Network.Owner is { } owner ) Chat.Tell( owner, was.Length > 0 ? $"You fit the lock, and the old one comes out." : "You fit the lock." );
		return Result.Success();
	}

	/// <summary>Host: the door or the locker the character looks at, within reach, or else the nearest within reach.</summary>
	private Component? HostLockableInFront()
	{
		var near = Scene.GetAllComponents<Door>().Cast<Component>().Concat( Scene.GetAllComponents<Container>() )
			.Select( value => (Thing: value, Distance: value.GameObject.GetBounds().ClosestPoint( HostPosition ).Distance( HostPosition )) )
			.Where( value => value.Distance <= FitReach )
			.OrderBy( value => value.Distance )
			.Select( value => value.Thing )
			.ToList();
		if ( near.Count == 0 || Controller is not { } controller ) return near.FirstOrDefault();
		var eye = HostPosition + Vector3.Up * EyeHeight;
		var seen = Scene.Trace.Ray( eye, eye + controller.EyeAngles.Forward * FitReach * 1.5f )
			.WithoutTags( "player", "item", "corpse", "sleeper" )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();
		return seen.Hit && seen.GameObject is { } hit ? near.FirstOrDefault( value => hit.IsAncestor( value.GameObject ) ) ?? near[0] : near[0];
	}

	/// <summary>The marks of the stamps a character carries: the offices it acts for.</summary>
	private static IEnumerable<string> StampMarks( CharacterData character ) =>
		character.Inventory.Items.Select( item => ItemDefinition.Find( item.Definition )?.StampMark ?? string.Empty ).Where( mark => mark.Length > 0 );

	/// <summary>Whether a thing of this size fits anywhere in an inventory as it is.</summary>
	private static bool HasRoom( InventoryData inventory, int width, int height ) =>
		Enumerable.Range( 0, inventory.Height ).Any( y => Enumerable.Range( 0, inventory.Width ).Any( x => InventoryGrid.Fits( inventory, x, y, width, height ) ) );
}
