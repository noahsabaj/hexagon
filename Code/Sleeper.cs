#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A character left in the city by a player who went while it was down or bound. Leaving is not a
/// way out of a scene: the character stays where it was, keeps bleeding, and can be helped up,
/// searched, released or finished like anyone else in that state. It is the host's object, sent
/// only to clients near enough to see it, and its player picks up from here on coming back.
/// </summary>
[Title( "Hexagon Sleeper" ), Category( "Hexagon" ), Icon( "bedtime" )]
public sealed class Sleeper : Component, Component.IPressable, Component.INetworkVisible, IVerbTarget
{
	/// <summary>
	/// Every client is told a new object exists, and is sent it for a moment before the network
	/// stops sending it to those who cannot see it. What the character looks like waits that long.
	/// </summary>
	public const float ShowAfterSeconds = 3f;

	[Sync( SyncFlags.FromHost )] public Guid CharacterId { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsShown { get; set; }
	[Sync( SyncFlags.FromHost )] public string CharacterDescription { get; set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string Look { get; set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public bool IsDown { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsRestrained { get; set; }

	private RealTimeSince _sinceLeft;
	private string? _shownLook;
	private bool? _shownDown;

	/// <summary>Host: the character lying, or standing bound, here.</summary>
	public CharacterData? HostCharacter => Networking.IsHost ? GameManager.Instance?.Roster?.Find( CharacterId ) : null;

	public IReadOnlyList<Verb> Verbs => IsDown || IsRestrained ? Player.VerbsFor( IsDown, IsRestrained ) : Array.Empty<Verb>();
	float IVerbTarget.Reach => Player.PersonReach;
	bool IVerbTarget.UseOpensMenu => true;
	string? IVerbTarget.JournalSubject => $"character:{CharacterId:N}";

	/// <summary>Host: leaves a character where it is, if it is down or bound and is not lying somewhere already.</summary>
	public static Sleeper? HostLeave( CharacterData character, Vector3 at )
	{
		if ( !Networking.IsHost || !(character.IsDown || character.IsRestrained) || Find( character.Id ) is not null ) return null;
		var body = new GameObject( true, "Sleeper" );
		body.WorldPosition = at;
		body.Tags.Add( "sleeper" );
		var figure = new GameObject( body, true, "Figure" );
		figure.AddComponent<SkinnedModelRenderer>().Model = Model.Load( Looks.DefaultBody );
		body.AddComponent<BoxCollider>().Static = true;
		var sleeper = body.AddComponent<Sleeper>();
		sleeper.CharacterId = character.Id;
		sleeper.HostSync( character );
		// Only the host knows where everyone is, so it alone decides which clients are sent this.
		var options = NetworkSpawnOptions.Default;
		options.AlwaysTransmit = false;
		body.NetworkSpawn( options );
		return sleeper;
	}

	/// <summary>The sleeper of this character, if there is one.</summary>
	public static Sleeper? Find( Guid characterId ) =>
		Game.ActiveScene?.GetAllComponents<Sleeper>().FirstOrDefault( value => value.CharacterId == characterId && !value.GameObject.IsDestroyed );

	/// <summary>Host: the character is back in the city, in its own body.</summary>
	public static void HostRemove( Guid characterId ) => Find( characterId )?.GameObject.Destroy();

	/// <summary>Host: brings a sleeper in line with its character, and removes it once the character is up and free, or gone.</summary>
	public static void HostRefresh( CharacterData character )
	{
		if ( !Networking.IsHost || Find( character.Id ) is not { } sleeper ) return;
		if ( GameManager.Instance?.Roster?.Contains( character ) != true || !(character.IsDown || character.IsRestrained) )
		{
			sleeper.GameObject.Destroy();
			return;
		}
		sleeper.HostSync( character );
	}

	private void HostSync( CharacterData character )
	{
		IsDown = character.IsDown;
		IsRestrained = character.IsRestrained;
		if ( !IsShown ) return;
		CharacterDescription = character.Description;
		Look = LookOf( character );
	}

	private static string LookOf( CharacterData character ) => Looks.Of( FactionDefinition.Find( character.Faction ) );

	protected override void OnAwake() => _sinceLeft = 0;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || GameManager.Instance is not { Roster: { } roster } game ) return;
		// Gone, up and free, or back in its own body in the city: there is nobody left lying here.
		if ( roster.Find( CharacterId ) is not { } character || !(character.IsDown || character.IsRestrained) || Player.HostLoaded( CharacterId ) is not null )
		{
			GameObject.Destroy();
			return;
		}
		if ( !IsShown && _sinceLeft > ShowAfterSeconds )
		{
			IsShown = true;
			HostSync( character );
		}
		// Time passes for someone lying in the street, whether or not their player is watching.
		if ( !Vitals.Bleed( character, Time.Delta, game.DownedSeconds ) ) return;
		if ( game.BleedOutKills )
		{
			Player.HostDie( game, character, WorldPosition, LookOf( character ), "bled out", null );
			return;
		}
		Vitals.Revive( character );
		roster.Save( character );
		HostRefresh( character );
	}

	protected override void OnUpdate()
	{
		if ( GetComponentInChildren<SkinnedModelRenderer>( true ) is not { } renderer ) return;
		renderer.Enabled = IsShown;
		// A dedicated server draws nobody, so it dresses nobody either, as with players.
		if ( _shownLook != Look && !Application.IsDedicatedServer )
		{
			_shownLook = Look;
			Looks.Apply( renderer, Look );
		}
		if ( _shownDown == IsDown || GetComponent<BoxCollider>() is not { } collider ) return;
		_shownDown = IsDown;
		// Down, they lie as the downed do. Bound, they stand where they were left.
		renderer.GameObject.LocalPosition = IsDown ? Vector3.Up * 8f : Vector3.Zero;
		renderer.GameObject.LocalRotation = IsDown ? Rotation.From( -90, 0, 0 ) : Rotation.Identity;
		collider.Scale = IsDown ? new Vector3( 76, 28, 20 ) : new Vector3( 32, 32, 72 );
		collider.Center = IsDown ? new Vector3( -36, 0, 10 ) : new Vector3( 0, 0, 36 );
	}

	/// <summary>Host: a client is sent this only while its own character is near enough to see it.</summary>
	bool INetworkVisible.IsVisibleToConnection( Connection connection, in BBox worldBounds ) =>
		!Networking.IsHost || connection.IsHost || GameManager.Instance?.Sees( connection, WorldPosition ) == true;

	bool IPressable.CanPress( IPressable.Event e ) => IsShown;

	bool IPressable.Press( IPressable.Event e )
	{
		if ( Player.Local is { } local ) local.MenuTarget = this;
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		Player.Local is null || !IsShown ? null : new IPressable.Tooltip( Player.Local.LabelFor( this ), "person", CharacterDescription );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( HostCharacter is not { } character || GameManager.Instance is not { Roster: { } roster } game || actor.HostCharacter is not { } other )
			return Result.Fail( ErrorCode.NotFound, "There is nobody there." );
		switch ( verb.Id )
		{
			case Player.ReviveVerb:
			{
				var revived = Vitals.Revive( character );
				if ( !revived.Ok ) return revived;
				roster.Save( character );
				HostRefresh( character );
				return revived;
			}
			case Player.SearchVerb:
				actor.HostOpen( this, character, "Belongings", onlyWhile: () => !GameObject.IsDestroyed && (character.IsDown || character.IsRestrained) );
				return Result.Success();
			case Player.ReleaseVerb:
			{
				if ( !character.IsRestrained ) return Result.Fail( ErrorCode.Conflict, "Their hands are not bound." );
				character.IsRestrained = false;
				roster.Save( character );
				HostRefresh( character );
				return Result.Success();
			}
			case Player.FinishVerb:
			{
				var can = Vitals.CanFinish( character );
				if ( can.Ok ) Player.HostDie( game, character, WorldPosition, LookOf( character ), "finished", other );
				return can;
			}
			default:
				return Result.Fail( ErrorCode.Invalid, "That cannot be done." );
		}
	}

	/// <summary>Host: harm done to someone who cannot get away from it.</summary>
	internal void HostDamage( int amount, Player by, string cause )
	{
		if ( HostCharacter is not { } character || GameManager.Instance?.Roster is not { } roster ) return;
		var harm = Vitals.Damage( character, amount );
		if ( harm == Harm.None ) return;
		if ( harm == Harm.Downed ) character.Equipped = null;
		roster.Save( character );
		if ( harm == Harm.Downed )
			Player.HostRecordAt( "character.downed", Actor.Of( character ), WorldPosition, by.HostCharacter?.HolderLabel, data: new[] { ("cause", cause) } );
		HostRefresh( character );
	}
}
