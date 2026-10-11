#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A door anyone can open. Locking it takes a key cut to its <see cref="LockPattern"/>, whoever holds
/// the key: owning a door is title only, the right to sell or rent it, and an owner hands it over by
/// offering it, like anything else they have. It lists its verbs and carries them out;
/// <see cref="Player.RequestAct"/> judges who may. State is host-written and replicated; the swing is
/// animated locally on every client. It persists by the scene object's id, which the editor keeps
/// stable across loads.
/// </summary>
[Title( "Hexagon Door" ), Category( "Hexagon" ), Icon( "door_front" )]
public sealed class Door : Component, Component.IPressable, IVerbTarget
{
	/// <summary>How far a pawn may stand from the door and still work it.</summary>
	public const float Reach = 150f;

	[Property] public Angles OpenRotation { get; set; } = new( 0, 90, 0 );
	[Property] public float SwingSpeed { get; set; } = 6f;
	[Property] public bool StartsLocked { get; set; }
	/// <summary>
	/// What its lock is cut to. Every key cut the same turns it, so a ministry's doors can share one
	/// pattern while each flat has its own. Empty means it has no lock.
	/// </summary>
	[Property] public string LockPattern { get; set; } = string.Empty;

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }

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
		if ( HexagonServices.Current!.World.Doors.TryGetValue( GameObject.Id, out var saved ) )
		{
			IsOpen = saved.IsOpen;
			IsLocked = saved.IsLocked;
			_owner = saved.Owner;
		}
		else
		{
			IsLocked = StartsLocked;
		}
	}

	private static readonly Verb Use = new( VerbIds.DoorUse, "Open or close" );
	// Locking asks for no capability up front: what turns a lock is a key, which only the door can judge.
	private static readonly Verb Lock = new( VerbIds.DoorLock, "Lock or unlock" );

	private Guid? _owner;

	/// <summary>Host: the character who holds its title, if anyone does.</summary>
	public Guid? HostOwner => _owner;

	public IReadOnlyList<Verb> Verbs => LockPattern.Length > 0 ? new[] { Use, Lock } : new[] { Use };
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
		LockPattern.Length > 0 ? "It locks with a key." : string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( verb == Lock )
		{
			if ( !actor.HostHasKey( LockPattern ) ) return Result.Fail( ErrorCode.Denied, "You have no key that fits." );
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

	/// <summary>Host: staff work the door directly, whatever key it asks for.</summary>
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
				return Result.Fail( ErrorCode.Invalid, "A door can be opened, closed, locked, unlocked, assigned or released." );
		}
		Persist();
		return Result.Success();
	}

	/// <summary>Host: the title passes to a character, whether staff assign it or its owner's offer is accepted. The keys do not come with it.</summary>
	public void HostAssign( CharacterData to, Actor by )
	{
		if ( HexagonServices.Current is not { } game ) return;
		var was = _owner;
		_owner = to.Id;
		Persist();
		var data = new List<(string, string)> { ("owner", to.Name) };
		var previous = was is { } from ? game.Roster?.Find( from ) : null;
		if ( was is { } before ) data.Add( ("from", previous?.Name ?? $"character:{before:N}") );
		game.Journal?.Record( JournalKinds.DoorAssign, by, $"Door:{GameObject.Id:N}", data: data.ToArray() );
		// Each owner is shown the doors it now holds.
		Player.HostRefresh( to );
		if ( previous is not null ) Player.HostRefresh( previous );
	}

	/// <summary>Host: nobody owns it any more. A building nobody owns is for staff, or a player office, to assign.</summary>
	public void HostRelease( Actor by )
	{
		if ( _owner is not { } owner || HexagonServices.Current is not { } game ) return;
		_owner = null;
		Persist();
		var was = game.Roster?.Find( owner );
		game.Journal?.Record( JournalKinds.DoorRelease, by, $"Door:{GameObject.Id:N}", data: ("owner", was?.Name ?? $"character:{owner:N}") );
		if ( was is not null ) Player.HostRefresh( was );
	}

	/// <summary>Host: a character that leaves the world for good leaves no deeds behind it.</summary>
	public static void HostReleaseOwnedBy( Guid characterId, Actor by )
	{
		if ( HexagonServices.Current is not { } game ) return;
		foreach ( var door in game.Scene.GetAllComponents<Door>().Where( value => value._owner == characterId ).ToArray() ) door.HostRelease( by );
		// A door not yet restored, or no longer in the scene, is cleared in the saved world instead.
		var saved = game.World.Doors.Values.Where( value => value.Owner == characterId ).ToArray();
		foreach ( var door in saved ) door.Owner = null;
		if ( saved.Length > 0 ) game.SaveWorld();
	}

	/// <summary>Host: one line for staff, saying whether it is open or locked, what its lock is cut to, and who owns it.</summary>
	internal string HostDescribe( Func<Guid, string?> nameOf ) =>
		$"{GameObject.Name}: {(IsOpen ? "open" : "closed")}, {(IsLocked ? "locked" : "unlocked")}" +
		(LockPattern.Length > 0 ? $", lock cut to {LockPattern}" : ", no lock") +
		(_owner is { } owner ? $", owned by {nameOf( owner ) ?? $"character:{owner:N}"}" : string.Empty);

	private DoorData Saved() => new() { IsOpen = IsOpen, IsLocked = IsLocked, Owner = _owner };

	private void Persist()
	{
		if ( HexagonServices.Current is not { } game ) return;
		game.World.Doors[GameObject.Id] = Saved();
		game.SaveWorld();
	}
}
