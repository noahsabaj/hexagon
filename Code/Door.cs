#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A door anyone can open and only a character with <c>door.lock</c> can lock. It lists its verbs and
/// carries them out; <see cref="Player.RequestAct"/> judges who may. State is host-written and replicated;
/// the swing is animated locally on every client. It persists by the scene object's id, which the
/// editor keeps stable across loads.
/// </summary>
[Title( "Hexagon Door" ), Category( "Hexagon" ), Icon( "door_front" )]
public sealed class Door : Component, Component.IPressable, IVerbTarget
{
	/// <summary>How far a pawn may stand from the door and still work it.</summary>
	public const float Reach = 150f;

	[Property] public Angles OpenRotation { get; set; } = new( 0, 90, 0 );
	[Property] public float SwingSpeed { get; set; } = 6f;
	[Property] public bool StartsLocked { get; set; }

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }

	private Rotation _closed;
	private bool _restored;

	protected override void OnStart() => _closed = LocalRotation;

	protected override void OnUpdate()
	{
		if ( !_restored ) HostRestore();
		var target = IsOpen ? _closed * OpenRotation.ToRotation() : _closed;
		LocalRotation = Rotation.Slerp( LocalRotation, target, Time.Delta * SwingSpeed );
	}

	/// <summary>
	/// Host: join the network session and adopt the saved state, or the authored default. This waits
	/// for the lobby, which opens after the scene has loaded: a door restored before then is not
	/// networked, so its synced state reaches nobody and its host requests have nothing to travel on.
	/// </summary>
	private void HostRestore()
	{
		if ( !Networking.IsHost || !Networking.IsActive || GameManager.Instance?.Roster is null ) return;
		_restored = true;
		if ( !Network.Active ) GameObject.NetworkSpawn();
		if ( GameManager.Instance.World.Doors.TryGetValue( GameObject.Id, out var saved ) )
		{
			IsOpen = saved.IsOpen;
			IsLocked = saved.IsLocked;
		}
		else
		{
			IsLocked = StartsLocked;
		}
	}

	private static readonly Verb Use = new( "door.use", "Open or close" );
	private static readonly Verb Lock = new( "door.lock", "Lock or unlock", Capability.DoorLock );

	public IReadOnlyList<Verb> Verbs { get; } = new[] { Use, Lock };
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
		"Press Reload to lock or unlock, if you are able." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( verb == Lock )
		{
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

	private void Persist()
	{
		if ( GameManager.Instance is not { } game ) return;
		game.World.Doors[GameObject.Id] = new DoorData { IsOpen = IsOpen, IsLocked = IsLocked };
		game.SaveWorld();
	}
}
