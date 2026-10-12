#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A door anyone can open. Locking it takes a key cut to its lock, whoever holds the key: owning the
/// building it is part of is title only (see <see cref="Property"/>), though whoever holds that title
/// may fit a new lock in place of the one in it. It lists its verbs and carries them out;
/// <see cref="Player.RequestAct"/> judges who may. State is host-written and replicated; the swing is
/// animated locally on every client. It persists by the scene object's id, which the editor keeps
/// stable across loads.
/// </summary>
[Title( "Hexagon Door" ), Category( "Hexagon" ), Icon( "door_front" )]
public sealed class Door : Component, Component.IPressable, IVerbTarget, ILockable
{
	/// <summary>How far a pawn may stand from the door and still work it.</summary>
	public const float Reach = 150f;

	[Property] public Angles OpenRotation { get; set; } = new( 0, 90, 0 );
	[Property] public float SwingSpeed { get; set; } = 6f;
	[Property] public bool StartsLocked { get; set; }
	/// <summary>
	/// What the lock it is built with is cut to. Every key cut the same turns it, so a ministry's doors
	/// can share one pattern while each flat has its own. Empty means it is built with no lock.
	/// </summary>
	[Property] public string LockPattern { get; set; } = string.Empty;

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }
	/// <summary>Whether it has a lock, the one it was built with or one fitted since. What the lock is cut to is never sent.</summary>
	[Sync( SyncFlags.FromHost )] public bool HasLock { get; set; }

	/// <summary>Host: what its lock is cut to now, the one fitted last or else the one it was built with. Empty is no lock.</summary>
	public string HostPattern => HexagonServices.Current is { } game && game.World.Locks.TryGetValue( GameObject.Id, out var fitted ) ? fitted : LockPattern;

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
		}
		else
		{
			IsLocked = StartsLocked;
		}
		HasLock = HostPattern.Length > 0;
	}

	private static readonly Verb Use = new( VerbIds.DoorUse, "Open or close" );
	// Locking asks for no capability up front: what turns a lock is a key, which only the door can judge.
	private static readonly Verb Lock = new( VerbIds.DoorLock, "Lock or unlock" );

	public IReadOnlyList<Verb> Verbs => HasLock ? new[] { Use, Lock } : new[] { Use };
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
		HasLock ? "It locks with a key." : string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( verb == Lock )
		{
			if ( !actor.HostHasKey( HostPattern ) ) return Result.Fail( ErrorCode.Denied, "You have no key that fits." );
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
			default:
				return Result.Fail( ErrorCode.Invalid, "A door can be opened, closed, locked or unlocked." );
		}
		Persist();
		return Result.Success();
	}

	/// <summary>Host: a new lock goes in, cut to <paramref name="pattern"/>, and the door is left unlocked, as it stood open to fit it. See <see cref="ILockable.HostFit"/>.</summary>
	public void HostFit( string pattern )
	{
		if ( HexagonServices.Current is not { } game ) return;
		game.World.Locks[GameObject.Id] = pattern;
		HasLock = pattern.Length > 0;
		IsLocked = false;
		game.World.Doors[GameObject.Id] = Saved();
	}

	/// <summary>Host: one line for staff, saying whether it is open or locked, and what its lock is cut to.</summary>
	internal string HostDescribe() =>
		$"{GameObject.Name}: {(IsOpen ? "open" : "closed")}, {(IsLocked ? "locked" : "unlocked")}" +
		(HostPattern is { Length: > 0 } pattern ? $", lock cut to {pattern}{(HostPattern != LockPattern ? " (fitted since)" : string.Empty)}" : ", no lock");

	private DoorData Saved() => new() { IsOpen = IsOpen, IsLocked = IsLocked };

	private void Persist()
	{
		if ( HexagonServices.Current is not { } game ) return;
		game.World.Doors[GameObject.Id] = Saved();
		game.SaveWorld();
	}
}
