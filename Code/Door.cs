#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A door anyone can open. Who can lock it is the door's to say: <see cref="LockRequires"/> names a
/// capability, and whoever bought it can always lock it. It lists its verbs and carries them out;
/// <see cref="Player.RequestAct"/> judges who may. State is host-written and replicated; the swing
/// is animated locally on every client. It persists by the scene object's id, which the editor
/// keeps stable across loads.
/// </summary>
[Title( "Hexagon Door" ), Category( "Hexagon" ), Icon( "door_front" )]
public sealed class Door : Component, Component.IPressable, IVerbTarget
{
	/// <summary>How far a pawn may stand from the door and still work it.</summary>
	public const float Reach = 150f;

	[Property] public Angles OpenRotation { get; set; } = new( 0, 90, 0 );
	[Property] public float SwingSpeed { get; set; } = 6f;
	[Property] public bool StartsLocked { get; set; }
	/// <summary>What the door costs to own. Zero means it is not for sale.</summary>
	[Property] public long Price { get; set; }
	/// <summary>
	/// The capability that locks it, such as what a key grants. Empty means only its owner can. A door
	/// for sale should ask for one that nobody else has, or owning it secures nothing.
	/// </summary>
	[Property] public string LockRequires { get; set; } = Capability.DoorLock;

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }
	/// <summary>Whether someone has bought it. Who is not anyone else's business.</summary>
	[Sync( SyncFlags.FromHost )] public bool IsOwned { get; set; }

	/// <summary>Whether it is still moving. A door on the move pushes people about; nobody walks through it.</summary>
	public bool IsSwinging { get; private set; }

	private Rotation _closed;
	private bool _restored;

	protected override void OnStart() => _closed = LocalRotation;

	protected override void OnUpdate()
	{
		if ( !_restored ) HostRestore();
		var target = IsOpen ? _closed * OpenRotation.ToRotation() : _closed;
		LocalRotation = Rotation.Slerp( LocalRotation, target, Time.Delta * SwingSpeed );
		IsSwinging = LocalRotation.Distance( target ) > 1f;
	}

	/// <summary>
	/// Host: join the network session and adopt the saved state, or the authored default. This waits
	/// for the lobby, which opens after the scene has loaded: a door restored before then is not
	/// networked, so its synced state reaches nobody and its host requests have nothing to travel on.
	/// </summary>
	private void HostRestore()
	{
		if ( !HostScene.Join( this ) ) return;
		_restored = true;
		if ( GameManager.Instance!.World.Doors.TryGetValue( GameObject.Id, out var saved ) )
		{
			IsOpen = saved.IsOpen;
			IsLocked = saved.IsLocked;
			_owner = saved.Owner;
			IsOwned = saved.Owner is not null;
		}
		else
		{
			IsLocked = StartsLocked;
		}
	}

	private static readonly Verb Use = new( "door.use", "Open or close" );
	// Locking asks for no capability up front: the owner may lock it too, which only the door knows.
	private static readonly Verb Lock = new( "door.lock", "Lock or unlock" );
	private static readonly Verb Buy = new( "door.buy", "Buy" );

	private Guid? _owner;

	public IReadOnlyList<Verb> Verbs => Price > 0 && !IsOwned ? new[] { Use, Lock, Buy } : new[] { Use, Lock };
	float IVerbTarget.Reach => Reach;

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, Use.Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new IPressable.Tooltip(
		IsLocked ? "Locked door" : IsOpen ? "Close door" : "Open door",
		IsLocked ? "lock" : "door_front",
		Price > 0 && !IsOwned ? $"For sale: {Price} tokens." : "Locking it is in the menu, for those able to." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( verb == Buy ) return Purchase( actor );
		if ( verb == Lock )
		{
			if ( !CanLock( actor ) ) return Result.Fail( ErrorCode.Denied, "You have nothing that lets you do that." );
			IsLocked = !IsLocked;
			if ( IsLocked ) IsOpen = false;
		}
		else
		{
			if ( IsLocked ) return Result.Fail( ErrorCode.Conflict, "The door is locked." );
			IsOpen = !IsOpen;
		}
		Persist();
		return Result.Success();
	}

	private bool CanLock( Player actor ) =>
		(_owner is not null && _owner == actor.HostCharacter?.Id) || (LockRequires.Length > 0 && actor.HostCan( LockRequires ));

	/// <summary>The price leaves the world through the property sink: it is paid to the city, not to a player.</summary>
	private Result Purchase( Player actor )
	{
		if ( Price <= 0 || IsOwned || actor.HostCharacter is not { } buyer ) return Result.Fail( ErrorCode.Conflict, "It is not for sale." );
		var game = GameManager.Instance!;
		var paid = game.Transfers!.DestroyTokens( Sinks.Property, buyer, Price, Actor.Of( buyer ) );
		if ( !paid.Ok ) return Result.Fail( paid.Code, $"That costs {Price} tokens." );
		_owner = buyer.Id;
		IsOwned = true;
		game.World.Doors[GameObject.Id] = Saved();
		// The price paid and the deed are one change: a crash keeps both or neither.
		game.SaveTogether( buyer, game.World );
		Player.HostRefresh( buyer );
		return Result.Success();
	}

	/// <summary>Host: staff work the door directly, whoever owns it and whatever it asks for.</summary>
	public Result HostOperate( string action, Actor by )
	{
		switch ( action )
		{
			case "open":
				IsLocked = false;
				IsOpen = true;
				break;
			case "close":
				IsOpen = false;
				break;
			case "lock":
				IsLocked = true;
				IsOpen = false;
				break;
			case "unlock":
				IsLocked = false;
				break;
			case "release":
				if ( _owner is null ) return Result.Fail( ErrorCode.Conflict, "Nobody owns it." );
				HostRelease( by );
				return Result.Success();
			default:
				return Result.Fail( ErrorCode.Invalid, "A door can be opened, closed, locked, unlocked or released." );
		}
		Persist();
		return Result.Success();
	}

	/// <summary>Host: nobody owns it any more, and it is for sale again if it ever was.</summary>
	public void HostRelease( Actor by )
	{
		if ( _owner is not { } owner || GameManager.Instance is not { } game ) return;
		_owner = null;
		IsOwned = false;
		Persist();
		game.Journal?.Record( "door.release", by, $"Door:{GameObject.Id:N}", data: ("owner", game.Roster?.Find( owner )?.Name ?? $"character:{owner:N}") );
	}

	/// <summary>Host: a character that leaves the world for good leaves no deeds behind it.</summary>
	public static void HostReleaseOwnedBy( Guid characterId, Actor by )
	{
		if ( GameManager.Instance is not { } game ) return;
		foreach ( var door in game.Scene.GetAllComponents<Door>().Where( value => value._owner == characterId ).ToArray() ) door.HostRelease( by );
		// A door not yet restored, or no longer in the scene, is cleared in the saved world instead.
		var saved = game.World.Doors.Values.Where( value => value.Owner == characterId ).ToArray();
		foreach ( var door in saved ) door.Owner = null;
		if ( saved.Length > 0 ) game.SaveWorld();
	}

	/// <summary>Host: one line for staff, saying whether it is open, locked or for sale, and who owns it.</summary>
	internal string HostDescribe( Func<Guid, string?> nameOf ) =>
		$"{GameObject.Name}: {(IsOpen ? "open" : "closed")}, {(IsLocked ? "locked" : "unlocked")}" +
		(_owner is { } owner ? $", owned by {nameOf( owner ) ?? $"character:{owner:N}"}" : Price > 0 ? $", for sale at {Price} tokens" : string.Empty);

	private DoorData Saved() => new() { IsOpen = IsOpen, IsLocked = IsLocked, Owner = _owner };

	private void Persist()
	{
		if ( GameManager.Instance is not { } game ) return;
		game.World.Doors[GameObject.Id] = Saved();
		game.SaveWorld();
	}
}
