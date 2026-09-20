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
	/// <summary>What the door costs to own. Zero means it is not for sale.</summary>
	[Property] public long Price { get; set; }

	[Sync( SyncFlags.FromHost )] public bool IsOpen { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsLocked { get; set; }
	/// <summary>Whether someone has bought it. Who is not anyone else's business.</summary>
	[Sync( SyncFlags.FromHost )] public bool IsOwned { get; set; }

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
		Price > 0 && !IsOwned ? $"For sale: {Price} tokens." : "Press Reload to lock or unlock, if you are able." );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( verb == Buy ) return Purchase( actor );
		if ( verb == Lock )
		{
			if ( !actor.HostCan( Capability.DoorLock ) && (_owner is null || _owner != actor.HostCharacter?.Id) )
				return Result.Fail( ErrorCode.Denied, "You have nothing that lets you do that." );
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

	/// <summary>The price leaves the world through the property sink: it is paid to the city, not to a player.</summary>
	private Result Purchase( Player actor )
	{
		if ( Price <= 0 || IsOwned || actor.HostCharacter is not { } buyer ) return Result.Fail( ErrorCode.Conflict, "It is not for sale." );
		var game = GameManager.Instance!;
		var paid = game.Transfers!.DestroyTokens( Sinks.Property, buyer, Price, Actor.Of( buyer ) );
		if ( !paid.Ok ) return Result.Fail( paid.Code, $"That costs {Price} tokens." );
		game.Roster!.Save( buyer );
		Player.HostRefresh( buyer );
		_owner = buyer.Id;
		IsOwned = true;
		Persist();
		return Result.Success();
	}

	private void Persist()
	{
		if ( GameManager.Instance is not { } game ) return;
		game.World.Doors[GameObject.Id] = new DoorData { IsOpen = IsOpen, IsLocked = IsLocked, Owner = _owner };
		game.SaveWorld();
	}
}
