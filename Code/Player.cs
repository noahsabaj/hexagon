#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What a player sees of one of their characters before entering the city: whether it has lived,
/// whether it is dead, and when a new one may first enter.
/// </summary>
public sealed record CharacterSummary( Guid Id, string Name, string Faction, bool Lived, DateTimeOffset? DiedAt, DateTimeOffset? ArrivesAt )
{
	public bool IsDead => DiedAt is not null;
	/// <summary>Whether it is still waiting to enter the city, by this machine's clock.</summary>
	public bool IsWaiting => !IsDead && ArrivesAt > DateTimeOffset.UtcNow;
}

/// <summary>A door whose title a character holds, as its owner is shown it, to offer it on.</summary>
public sealed record OwnedDoor( Guid Id, string Name );

/// <summary>
/// One connection's presence in the city. Movement is the engine's <see cref="PlayerController"/>,
/// simulated by the owner. Only what another character could see is replicated through
/// <c>[Sync]</c>: that someone is there, and what they look like. It is sent only to clients whose
/// own character is near enough to see it, by where the host believes both are. A name or a
/// faction is knowledge, not appearance, so it reaches its owner alone by owner-only RPC and a client
/// cannot list who is in the city. A client changes nothing directly: it calls a <c>[Rpc.Host]</c>
/// request, and the host decides.
/// </summary>
[Title( "Hexagon Player" ), Category( "Hexagon" ), Icon( "person" )]
public sealed partial class Player : Component, Component.IPressable, Component.INetworkVisible, IVerbTarget
{
	/// <summary>The pawn this client controls, or null before it has spawned.</summary>
	public static Player? Local { get; private set; }

	[Sync( SyncFlags.FromHost )] public bool HasCharacter { get; set; }
	[Sync( SyncFlags.FromHost )] public string CharacterDescription { get; set; } = string.Empty;
	/// <summary>A face: the same person is the same id tomorrow. It says nothing about who they are.</summary>
	[Sync( SyncFlags.FromHost )] public Guid CharacterId { get; set; }
	/// <summary>
	/// The host's heartbeat. A client is only sent someone while they are in sight, and only the
	/// pawn's owner may tell a client to drop it, which the engine loses on its way through the host
	/// (see <see cref="IsPresent"/>). So a client that stops hearing this knows the person has gone
	/// out of sight.
	/// </summary>
	[Sync( SyncFlags.FromHost )] public int Pulse { get; set; }

	// Owner-side copies of private state. Empty on every other client.
	public string CharacterName { get; private set; } = string.Empty;
	public string FactionPath { get; private set; } = string.Empty;
	public FactionDefinition? Faction => FactionDefinition.Find( FactionPath );
	/// <summary>The names this character has been told, by face.</summary>
	public IReadOnlyDictionary<Guid, string> Known { get; private set; } = new Dictionary<Guid, string>();

	/// <summary>Owner: how this client's character would refer to someone it can see.</summary>
	public string LabelFor( Player other ) =>
		other == this ? CharacterName : Known.TryGetValue( other.CharacterId, out var name ) ? name : Recognition.Stranger( other.CharacterDescription );

	/// <summary>Owner: how this client's character would refer to someone left lying, or bound, where they were.</summary>
	public string LabelFor( Sleeper other ) =>
		Known.TryGetValue( other.CharacterId, out var name ) ? name : Recognition.Stranger( other.CharacterDescription );

	public IReadOnlyList<CharacterSummary> Characters { get; private set; } = Array.Empty<CharacterSummary>();
	/// <summary>What this character carries. A key shows its cut, never what it opens.</summary>
	public InventoryData? Inventory { get; private set; }
	/// <summary>The doors this character holds the title to.</summary>
	public IReadOnlyList<OwnedDoor> OwnedDoors { get; private set; } = Array.Empty<OwnedDoor>();

	/// <summary>Bumped whenever private state changes so panels can rebuild.</summary>
	public int PrivateVersion { get; private set; }

	// Host-side.
	private CharacterData? _character;
	private readonly MovementAudit _movement = new();
	// The last claim the host believed, which the next one is traced from.
	private Vector3? _lastClaim;

	// Owner: the connections the host lets see this pawn. Its movement is sent to them and the host alone.
	private readonly HashSet<Guid> _observers = new();
	// Host: who the owner was last told may see it, and which owner was told.
	private Guid[] _toldObservers = Array.Empty<Guid>();
	private Guid _toldOwner;
	private RealTimeUntil _untilObservers;

	// Client-side: when the heartbeat last changed.
	private int _seenPulse = -1;
	private RealTimeSince _sincePulse;

	/// <summary>
	/// Host: where the host believes this character is. The owner simulates movement, so
	/// <c>WorldPosition</c> is only its claim; every host rule about distance reads this.
	/// </summary>
	public Vector3 HostPosition => _movement.Position;

	/// <summary>How much faster than a run a claim may be before it is disbelieved.</summary>
	public const float SpeedTolerance = 1.5f;

	/// <summary>How often, at most, the host works out who may see this pawn, in seconds.</summary>
	public const float ObserverInterval = 0.25f;

	/// <summary>Seconds without a heartbeat before a client takes someone to be out of sight.</summary>
	public const float PulseTimeout = 2f;

	/// <summary>
	/// Whether this client is to show the person: they have a character, and, if they are someone
	/// else, the host is still sending them. Out of sight, the owner's client stops sending them here
	/// and tells this client so, for the engine to make them inactive; but that notice reaches another
	/// client through the host, which wraps it in a way the engine there does not unwrap
	/// (<c>NetworkSystem.OnTargetedMessage</c> reads only packed messages from it), so it is lost, and
	/// the heartbeat stopping is what tells.
	/// </summary>
	public bool IsPresent => HasCharacter && (!IsProxy || Networking.IsHost || _sincePulse < PulseTimeout);

	/// <summary>Builds the pawn on the host, before it is network-spawned, so clients receive it whole.</summary>
	public static void Compose( GameObject pawn )
	{
		pawn.Tags.Add( "player" );
		// The animated model sits on a child: PlayerController moves its renderer locally for the
		// duck bob, which on the root would drag the whole pawn to the origin.
		var body = new GameObject( pawn, true, "Body" );
		var renderer = body.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Model.Load( Looks.DefaultBody );
		// So that what is in hand can be hung on the hand.
		renderer.CreateBoneObjects = true;

		var controller = pawn.AddComponent<PlayerController>();
		// Left null for controllers created at runtime, yet its generated colliders need a tag set.
		controller.BodyCollisionTags = new TagSet();
		controller.BodyCollisionTags.Add( "player" );
		// Linked explicitly: the automatic link runs when the controller enables, before the child exists.
		controller.Renderer = renderer;
		pawn.AddComponent<Player>();
	}

	protected override void OnStart()
	{
		if ( !IsProxy )
		{
			Local = this;
			// Statics outlive a session in the editor, so a new one starts with a clean log.
			Chat.Clear();
			Dev.DevCommands.Reset();
			Dev.DevCommands.Announce();
		}
		ApplyPresence();
		if ( !IsProxy ) RequestCharacters();
	}

	protected override void OnDestroy()
	{
		// The pawn going, with its connection or with the city, is the last chance to keep where it stood.
		HostSave();
		if ( Local == this ) Local = null;
	}

	protected override void OnPreRender()
	{
		// A test looking at a character from outside. After everything else has placed the camera.
		if ( !IsProxy && Dev.DevCommands.HeldCamera is { } held && Scene.Camera is { } view ) view.WorldTransform = held;
	}

	protected override void OnUpdate()
	{
		if ( Pulse != _seenPulse )
		{
			_seenPulse = Pulse;
			_sincePulse = 0;
		}
		ApplyPresence();
		ApplyAppearance();
		if ( IsProxy || !HasCharacter || Dev.DevCommands.Driven ) return;
		// "Use" reaches a target's first verb through IPressable. Everything else it offers is chosen
		// from a menu, by name: nothing that hands something over or binds someone hangs off a bare key.
		if ( UiBusy ) return;
		if ( HoveredTarget is IVerbTarget { Verbs.Count: > 1 } && Input.Pressed( MenuAction ) ) MenuTarget = HoveredTarget;
		if ( Input.Pressed( "Attack1" ) && !IsIncapable && Controller is { } controller ) RequestAttack( controller.EyeAngles.Forward );
	}

	/// <summary>Owner: set by the HUD while a panel has the cursor, so a click in a menu is not a punch.</summary>
	public static bool UiBusy { get; set; }

	/// <summary>The input action that lays out everything a target offers.</summary>
	public const string MenuAction = "Flashlight";

	/// <summary>Owner: the target whose verbs the HUD is showing as a menu, if any.</summary>
	public Component? MenuTarget { get; set; }

	/// <summary>Owner: the thing being looked at, if characters can act on it.</summary>
	public Component? HoveredTarget => Controller?.Hovered?.GetComponent<IVerbTarget>() as Component;

	/// <summary>Owner: what the engine's controller says about the thing being looked at: what it is, an icon, a line about it.</summary>
	public IPressable.Tooltip? HoveredTooltip =>
		HoveredTarget is IPressable target && Controller is { } controller ? controller.Tooltips.FirstOrDefault( value => value.Pressable == target ) is { Title: not null } tip ? tip : null : null;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost ) return;
		Pulse = (int)(Time.Now * 2f);
		HostTellObservers();
		if ( _character is null || Controller is not { } controller || GameManager.Instance is not { } game ) return;
		if ( _open is not null && !HostOpenIsValid() ) HostClose();
		HostVitalsTick( game );
		// Bleeding out may have ended the character for good.
		if ( _character is null ) return;
		HostOfferTick();
		HostAuditMovement( controller );
	}

	/// <summary>
	/// Host: the owner says where it is, and the host decides whether to believe it. A claim must be
	/// reachable at the character's speed, and the way to it must be open: a claim on the far side of
	/// a wall, a shut door or a floor is a path no body could take, at any speed.
	/// </summary>
	private void HostAuditMovement( PlayerController controller )
	{
		var limit = MathF.Max( controller.RunSpeed, controller.WalkSpeed ) * SpeedTolerance;
		// A fall is judged by the gravity the world has, which a scene may set, not by the engine's default.
		_movement.Gravity = MathF.Abs( Scene.PhysicsWorld.Gravity.z );
		var claimed = WorldPosition;
		var believed = HostPosition;
		// Each claim is traced from the one before, so the path is checked the way it was walked.
		var from = _movement.Settling ? believed : _lastClaim ?? believed;
		var believable = _movement.Observe( claimed, Time.Now, limit );
		// Still on its way to where the host put it: its claims are from the old place, and are not a path.
		if ( believable && _movement.Settling ) return;
		var wall = believable && from.Distance( claimed ) > 1f && HostBlocked( from, claimed );
		if ( believable && !wall )
		{
			_lastClaim = claimed;
			return;
		}
		HostRecord( JournalKinds.MovementImplausible, ok: false, data: new[] { ("claimed", $"{claimed.x:0},{claimed.y:0},{claimed.z:0}"), ("reason", wall ? "wall" : "speed") } );
		// Back to where it was believed to be before this claim, which the audit may already have taken.
		HostTeleport( believed );
	}

	/// <summary>
	/// Host: whether anything solid to a body lies between two claims, at knee height so a step does
	/// not count. Other people, things lying about and a door still swinging are not walls.
	/// </summary>
	private bool HostBlocked( Vector3 from, Vector3 to )
	{
		var knee = Vector3.Up * 24f;
		var trace = Scene.Trace.Ray( from + knee, to + knee )
			.WithCollisionRules( "player" )
			.WithoutTags( "player", "item", "corpse", "sleeper" )
			.IgnoreGameObjectHierarchy( GameObject )
			.IgnoreDynamic()
			.Run();
		if ( !trace.Hit || trace.StartedSolid ) return false;
		return trace.GameObject?.GetComponentInParent<Door>() is not { IsSwinging: true };
	}

	/// <summary>Host: puts the character somewhere. The owner simulates movement, so it is told to move itself.</summary>
	public void HostTeleport( Vector3 position )
	{
		if ( !Networking.IsHost ) return;
		// The host's belief moves whether or not there is anyone to tell.
		_movement.Reset( position, Time.Now );
		_lastClaim = null;
		if ( Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceiveTeleport( position );
	}

	/// <summary>
	/// A client is sent this pawn only while its own character is in the city and near enough to see
	/// it, by where the host believes both are, so no claim of the client's widens what it sees. The
	/// engine asks whoever sends: the host, for what it syncs, and the owner's client, for the movement
	/// it simulates, which the host passes on to other clients unread. So the host decides, and tells
	/// the owner (<see cref="HostTellObservers"/>), and the owner sends only where it was told.
	/// </summary>
	bool INetworkVisible.IsVisibleToConnection( Connection connection, in BBox worldBounds )
	{
		// The host has everything, and the owner its own pawn.
		if ( connection.IsHost || connection == Network.Owner ) return true;
		return Networking.IsHost ? HostMaySee( connection ) : _observers.Contains( connection.Id );
	}

	/// <summary>Host: whether the connection's character is in the city and near enough to see this one.</summary>
	private bool HostMaySee( Connection connection ) =>
		_character is not null && GameManager.Instance?.Sees( connection, HostPosition ) == true;

	/// <summary>Host: tells the owner who may see its pawn, whenever that changes or the pawn changes hands.</summary>
	private void HostTellObservers()
	{
		if ( Network.Owner is not { IsHost: false } owner || _untilObservers > 0f ) return;
		_untilObservers = ObserverInterval;
		var observers = Connection.All
			.Where( value => !value.IsHost && value != owner && HostMaySee( value ) )
			.Select( value => value.Id )
			.OrderBy( value => value )
			.ToArray();
		if ( owner.Id == _toldOwner && observers.SequenceEqual( _toldObservers ) ) return;
		_toldOwner = owner.Id;
		_toldObservers = observers;
		using ( Rpc.FilterInclude( owner ) ) ReceiveObservers( observers );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveObservers( Guid[] connections )
	{
		_observers.Clear();
		_observers.UnionWith( connections );
	}

	private PlayerController? Controller => GetComponent<PlayerController>( true );

	/// <summary>The body and what is worn on it. Appearance, so everyone has it.</summary>
	[Sync( SyncFlags.FromHost )] public string Look { get; set; } = string.Empty;

	private string? _shownLook;
	private string? _shownHeld;
	private GameObject? _heldObject;

	/// <summary>Every client: makes the body look like its look, and hangs what is in hand on the hand.</summary>
	private void ApplyAppearance()
	{
		if ( Application.IsDedicatedServer || Controller?.Renderer is not { } renderer || !renderer.IsValid() ) return;
		if ( _shownLook != Look )
		{
			_shownLook = Look;
			Looks.Apply( renderer, Look );
		}
		var held = ItemDefinition.Find( HeldItemPath );
		if ( _shownHeld != HeldItemPath )
		{
			_shownHeld = HeldItemPath;
			_heldObject?.Destroy();
			_heldObject = null;
			if ( held is not null && renderer.GetBoneObject( "hold_R" ) is { } hand )
			{
				_heldObject = new GameObject( hand, true, "Held" ) { NetworkMode = NetworkMode.Never };
				Looks.Show( _heldObject, held, 0.08f );
			}
		}
		renderer.Set( "holdtype", (int)(held?.HoldType ?? Sandbox.Citizen.CitizenAnimationHelper.HoldTypes.None) );
	}

	/// <summary>
	/// A connection without a character is in the menu: no body in the world, no movement. Someone
	/// who has gone out of sight is not left standing, unseen and solid, where they were last seen.
	/// </summary>
	private void ApplyPresence()
	{
		if ( Controller is not { } controller ) return;
		var present = IsPresent;
		if ( controller.Renderer.IsValid() )
		{
			controller.Renderer.GameObject.Enabled = present;
			// Someone who is down lies down. It is crude, and it is visible from across the street.
			controller.Renderer.GameObject.LocalRotation = IsDown ? Rotation.From( -90, 0, 0 ) : Rotation.Identity;
		}
		// Another's body is moved by the network, not by physics, so it can lose its colliders safely.
		if ( IsProxy && controller.ColliderObject.IsValid() ) controller.ColliderObject.Enabled = present;
		if ( IsProxy ) return;
		// A driven test client acts as though nobody is at the keyboard, but still stands, falls and is pushed.
		if ( Dev.DevCommands.Driven ) Input.Suppressed = true;
		controller.UseInputControls = HasCharacter && !IsDown;
		controller.EnablePressing = !Dev.DevCommands.Driven;
		controller.UseLookControls = HasCharacter && !Dev.DevCommands.Driven;
		controller.UseCameraControls = HasCharacter && !Dev.DevCommands.CameraHeld;
	}

	/// <summary>
	/// Every host request starts here. Whoever sent it pays for it from their own budget, whichever
	/// pawn it names; it must name their own pawn, so one player cannot act as another; and most
	/// requests need a character in the city. Each refusal is journaled, as a run of them is how a
	/// cheat shows up, but a flood of them is written as a count.
	/// </summary>
	private bool Authorize( string request, out Connection caller, out GameManager game, bool needsCharacter = true )
	{
		caller = Rpc.Caller;
		game = GameManager.Instance!;
		if ( !Networking.IsHost || game?.Roster is null || caller is null ) return false;
		if ( !game.HostSpend( caller ) )
		{
			if ( game.HostRefuse( caller, JournalKinds.RequestLimited, null, ("request", request) ) ) Chat.Tell( caller, "You are doing that too fast." );
			return false;
		}
		if ( caller != Network.Owner )
		{
			game.HostRefuse( caller, JournalKinds.RequestForged, _character?.HolderLabel ?? $"Player:{GameObject.Id:N}", ("request", request) );
			return false;
		}
		if ( !needsCharacter || _character is not null ) return true;
		if ( game.HostRefuse( caller, JournalKinds.RequestInvalid, null, ("request", request), ("reason", "no character") ) ) Chat.Tell( caller, "Enter the city first." );
		return false;
	}

	/// <summary>Host: a request no honest client would make, such as for an item it does not have. Refused on the record.</summary>
	private void HostInvalid( string request, string reason, string? value = null )
	{
		if ( GameManager.Instance is not { } game || Network.Owner is not { } owner ) return;
		var data = value is null
			? new[] { ("request", request), ("reason", reason) }
			: new[] { ("request", request), ("reason", reason), ("value", Clip( value )) };
		game.HostRefuse( owner, JournalKinds.RequestInvalid, null, data );
	}

	/// <summary>Host: the stack, if this character has it. Asking after one it does not have is refused on the record.</summary>
	private ItemStack? HostItem( string request, Guid itemId )
	{
		var stack = _character?.Inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( stack is null ) HostInvalid( request, "item", itemId.ToString( "N" ) );
		return stack;
	}

	/// <summary>Whatever a client sent is kept short before it is written down.</summary>
	private static string Clip( string text ) => text.Length <= 64 ? text : text[..64] + "...";

	internal static long SteamIdOf( Connection connection ) => (long)connection.SteamId.ValueUnsigned;

	/// <summary>Host: who is behind a connection: its character in the city, or else the account.</summary>
	internal static Actor HostActorOf( Connection connection ) =>
		Game.ActiveScene?.GetAllComponents<Player>().FirstOrDefault( value => value.Network.Owner == connection && value._character is not null )?._character is { } character
			? Actor.Of( character )
			: new Actor( SteamIdOf( connection ), Name: connection.DisplayName );

	/// <summary>Host: the pawn this character is in the city in, on whichever connection.</summary>
	internal static Player? HostLoaded( Guid characterId ) =>
		Game.ActiveScene?.GetAllComponents<Player>().FirstOrDefault( value => value._character?.Id == characterId );

	/// <summary>Host: writes the loaded character, including where it stands.</summary>
	public void HostSave()
	{
		if ( !Networking.IsHost || _character is null || GameManager.Instance?.Roster is not { } roster ) return;
		var at = HostPosition;
		_character.Position = new[] { at.x, at.y, at.z };
		roster.Save( _character );
	}

	/// <summary>Host: the loaded character, for other host systems such as doors.</summary>
	public CharacterData? HostCharacter => Networking.IsHost ? _character : null;

	/// <summary>Host: issues an item to the loaded character from a named source and tells the owner. A key comes cut to its pattern.</summary>
	public Result HostIssue( string source, ItemDefinition definition, Actor by, int count = 1, string? pattern = null )
	{
		if ( _character is null || GameManager.Instance is not { Transfers: { } transfers, Roster: { } roster } )
			return Result.Fail( ErrorCode.NotFound, "That player has no character loaded." );
		var issued = definition.IssueTo( transfers, source, _character, by, count, pattern );
		if ( !issued.Ok ) return issued.ToResult();
		roster.Save( _character );
		SendPrivateState();
		return Result.Success();
	}

	/// <summary>Host: whether the loaded character is able to do something. Only what it holds grants anything; who it works for does not.</summary>
	public bool HostCan( string capability )
	{
		if ( !Networking.IsHost || _character is null ) return false;
		return Capabilities.Can( capability, _character.Inventory.Items.SelectMany( item => ItemDefinition.Find( item.Definition )?.Grants ?? Enumerable.Empty<string>() ) );
	}

	/// <summary>Host: whether the loaded character carries a key that turns a lock cut to this pattern.</summary>
	public bool HostHasKey( string pattern ) => Networking.IsHost && _character is not null && Keys.Opens( _character.Inventory, pattern );

	/// <summary>Host: writes what this character just did to the journal, with where and who was near enough to see.</summary>
	public void HostRecord( string kind, string? subject = null, bool ok = true, params (string Key, string Value)[] data )
	{
		if ( !Networking.IsHost ) return;
		var by = _character is not null
			? Actor.Of( _character )
			: Network.Owner is { } owner ? new Actor( SteamIdOf( owner ), Name: owner.DisplayName ) : Actor.Console;
		// Someone in the menu is nowhere, and nobody saw them.
		HostRecordAt( kind, by, _character is not null ? HostPosition : null, subject, ok, data );
	}

	/// <summary>Host: writes an act done at a place to the journal, with every character near enough to have seen it.</summary>
	internal static void HostRecordAt( string kind, Actor by, Vector3? at, string? subject = null, bool ok = true, params (string Key, string Value)[] data )
	{
		if ( !Networking.IsHost || GameManager.Instance?.Journal is not { } journal ) return;
		var witnesses = at is { } place ? HostWitnesses( place, by.Character ) : Array.Empty<Guid>();
		journal.Record( kind, by, subject, ok, at is { } where ? new[] { where.x, where.y, where.z } : null, witnesses, data );
	}

	private static Guid[] HostWitnesses( Vector3 at, Guid? except ) =>
		Game.ActiveScene.GetAllComponents<Player>()
			.Where( other => other._character is not null && other._character.Id != except && other.HostPosition.Distance( at ) <= WitnessRange )
			.Select( other => other._character!.Id ).ToArray();

	/// <summary>How near a character must be to count as having seen an act: the range of ordinary speech.</summary>
	public const float WitnessRange = 300f;

	private void SendCharacterList( Connection caller, GameManager game )
	{
		// The list is the menu, and a client takes it as leaving whoever it was. A player in the city
		// is sent it when they leave, whatever they asked of the menu meanwhile.
		if ( _character is null )
		{
			var summaries = game.Roster!.OwnedBy( SteamIdOf( caller ) )
				.Select( value => new CharacterSummary( value.Id, value.Name, FactionDefinition.Find( value.Faction )?.Title ?? value.Faction,
					value.FirstEntered is not null, value.DiedAt, value.ArrivesAt ) )
				.ToArray();
			using ( Rpc.FilterInclude( caller ) ) ReceiveCharacters( JsonSerializer.Serialize( summaries ) );
		}
		HostSendAccount();
	}

	private void SendPrivateState()
	{
		if ( _character is null || Network.Owner is not { } owner ) return;
		// Private state changes whenever the inventory does, which is also when what is in hand may have left it.
		HostSyncVitals();
		var doors = Scene.GetAllComponents<Door>().Where( value => value.HostOwner == _character.Id )
			.Select( value => new OwnedDoor( value.GameObject.Id, value.GameObject.Name ) ).ToArray();
		using ( Rpc.FilterInclude( owner ) )
			ReceivePrivateState( _character.Name, _character.Faction, JsonSerializer.Serialize( Keys.AsSeen( _character.Inventory ) ),
				JsonSerializer.Serialize( _character.Known ), _character.Health, JsonSerializer.Serialize( doors ) );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveCharacters( string json )
	{
		Characters = JsonSerializer.Deserialize<CharacterSummary[]>( json ) ?? Array.Empty<CharacterSummary>();
		// The list is only ever sent to a player in the menu, so whatever character they had is gone.
		CharacterName = string.Empty;
		FactionPath = string.Empty;
		Known = new Dictionary<Guid, string>();
		Inventory = null;
		OwnedDoors = Array.Empty<OwnedDoor>();
		OfferText = string.Empty;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceivePrivateState( string name, string factionPath, string inventoryJson, string knownJson, int health, string doorsJson )
	{
		Health = health;
		Known = JsonSerializer.Deserialize<Dictionary<Guid, string>>( knownJson ) ?? new Dictionary<Guid, string>();
		CharacterName = name;
		FactionPath = factionPath;
		Inventory = JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		OwnedDoors = JsonSerializer.Deserialize<OwnedDoor[]>( doorsJson ) ?? Array.Empty<OwnedDoor>();
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveTeleport( Vector3 position )
	{
		WorldPosition = position;
		if ( Controller?.Body is not { } body || !body.IsValid() ) return;
		body.Velocity = Vector3.Zero;
		// A body at rest is asleep, and one put in the air asleep would hang there.
		body.Sleeping = false;
	}
}
