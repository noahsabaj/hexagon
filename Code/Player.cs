#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hexagon.Logic;
using Sandbox;
using Sandbox.Movement;

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

/// <summary>
/// The title to a building or a flat that a character can offer on, as it is shown it: one it holds,
/// or, where <paramref name="Office"/> names one, one that office holds, whose stamp it carries.
/// </summary>
public sealed record HeldTitle( Guid Id, string Name, string Office );

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
public sealed partial class Player : Component, Component.INetworkVisible, IEmbodiment, ICameraModifier
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
	/// <summary>Its rank in its faction, by title, or empty.</summary>
	public string RankTitle { get; private set; } = string.Empty;
	/// <summary>The names this character has been told, by face.</summary>
	public IReadOnlyDictionary<Guid, string> Known { get; private set; } = new Dictionary<Guid, string>();

	/// <summary>Owner: how this client's character would refer to someone it can see.</summary>
	public string LabelFor( Player other ) =>
		other == this ? CharacterName : Known.TryGetValue( other.CharacterId, out var name ) ? name : Recognition.Stranger( other.CharacterDescription );

	/// <summary>Owner: how this client's character would refer to someone, standing here or left lying where they were.</summary>
	public string LabelFor( CharacterBody other ) =>
		other.GameObject == GameObject ? CharacterName : Known.TryGetValue( other.CharacterId, out var name ) ? name : Recognition.Stranger( other.CharacterDescription );

	public IReadOnlyList<CharacterSummary> Characters { get; private set; } = Array.Empty<CharacterSummary>();
	/// <summary>What this character carries. A key shows its cut, never what it opens.</summary>
	public InventoryData? Inventory { get; private set; }
	/// <summary>The titles this character can offer: those it holds, and those of any office whose stamp it carries.</summary>
	public IReadOnlyList<HeldTitle> HeldTitles { get; private set; } = Array.Empty<HeldTitle>();
	/// <summary>Where this character is wounded, oldest first. How hurt someone else is cannot be read off them.</summary>
	public IReadOnlyList<string> Wounded { get; private set; } = Array.Empty<string>();

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

	/// <summary>Seconds the owner has to slow to a limp before a faster claim is disbelieved.</summary>
	public const float LimpGrace = 1.5f;

	/// <summary>How fast someone with a wounded leg walks, which is as fast as they go.</summary>
	public static float LimpSpeed => HexagonServices.Current?.LimpSpeed ?? 60f;

	// The speeds the controller was made with, before any limp: walking, running, ducked, and jumping.
	private (float Walk, float Run, float Ducked, float Jump) _speeds = (110f, 320f, 70f, 300f);
	// Host: since the character started limping.
	private RealTimeSince _sinceLimping;

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

	// Another client's person, while they are in sight and on their feet: someone down is their body on the ground.
	bool IEmbodiment.IsShownHere => IsProxy && IsPresent && !IsDown;

	// Someone down is carried by their body on the ground, a Sleeper: that is what is acted on and harmed.
	CharacterData? IEmbodiment.HostCharacter => IsDown ? null : HostCharacter;
	SkinnedModelRenderer? IEmbodiment.Renderer => Controller?.Renderer;
	bool IEmbodiment.IsLimp => false;

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
		// Left null for controllers created at runtime, yet its generated colliders need a tag set. The
		// capsule is only for walking into: a shot passes it by for the body's own hitboxes.
		controller.BodyCollisionTags = new TagSet();
		controller.BodyCollisionTags.Add( "player" );
		controller.BodyCollisionTags.Add( "capsule" );
		// The controller made its colliders as it was added, before it had the set, so they are told it.
		controller.ColliderObject?.Tags.SetFrom( controller.BodyCollisionTags );
		// Linked explicitly: the automatic link runs when the controller enables, before the child exists.
		controller.Renderer = renderer;
		// The controller only adds walking for itself. Sitting is chosen whenever the pawn is put in a seat.
		pawn.AddComponent<SitMoveMode>();
		pawn.AddComponent<Player>();
		pawn.AddComponent<CharacterBody>();
		pawn.AddComponent<BodyCues>();
	}

	protected override void OnStart()
	{
		if ( !IsProxy )
		{
			Local = this;
			// Statics outlive a session in the editor, so a new one starts with a clean log.
			Chat.Clear();
		}
		if ( Controller is { } controller )
		{
			_speeds = (controller.WalkSpeed, controller.RunSpeed, controller.DuckedSpeed, controller.JumpSpeed);
			if ( controller.Renderer.IsValid() ) Bodies.AddHitboxes( controller.Renderer, GameObject );
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

	protected override void OnUpdate()
	{
		if ( Pulse != _seenPulse )
		{
			_seenPulse = Pulse;
			_sincePulse = 0;
		}
		ApplyPresence();
		ApplyAppearance();
		if ( IsProxy || !HasCharacter ) return;
		DriveTick();
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
		if ( _character is null || Controller is not { } controller || HexagonServices.Current is not { } game ) return;
		if ( _open is not null && !HostOpenIsValid() ) HostClose();
		HostOfferTick();
		HostPaperTick();
		HostLeanTick();
		HostWorkTick();
		// Sitting, it goes where its seat goes, and its owner has no say in it.
		if ( _seat is not null ) HostSeatTick();
		else HostAuditMovement( controller );
	}

	/// <summary>
	/// Host: the owner says where it is, and the host decides whether to believe it. A claim must be
	/// reachable at the character's speed, and the way to it must be open: a claim on the far side of
	/// a wall, a shut door or a floor is a path no body could take, at any speed.
	/// </summary>
	private void HostAuditMovement( PlayerController controller )
	{
		// The speeds the character was made with. Limping, it is held to a walk once its owner has had time to slow.
		var limping = IsLimping && _sinceLimping > LimpGrace;
		var limit = (limping ? LimpSpeed : MathF.Max( _speeds.Run, _speeds.Walk )) * SpeedTolerance;
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
		// Put somewhere, it is not sitting anywhere.
		HostLeaveSeat();
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
		_character is not null && HexagonServices.Current?.Sees( connection, HostPosition ) == true;

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

	/// <summary>
	/// Every machine: makes the body look like its look, and hangs what is in hand on the hand. A server
	/// with no screen still gives the body its model, which where a shot lands and how it falls are of.
	/// </summary>
	private void ApplyAppearance()
	{
		if ( Controller?.Renderer is not { } renderer || !renderer.IsValid() ) return;
		if ( _shownLook != Look )
		{
			_shownLook = Look;
			Looks.Apply( renderer, Look );
		}
		if ( Application.IsDedicatedServer ) return;
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
	/// Someone down is their body on the ground, which the host's Sleeper carries: the pawn holds still,
	/// unseen and not solid, and its player looks out of the body's eyes.
	/// </summary>
	private void ApplyPresence()
	{
		if ( Controller is not { } controller ) return;
		var standing = IsPresent && !IsDown;
		var seated = IsSeated;
		if ( controller.Renderer.IsValid() ) controller.Renderer.GameObject.Enabled = standing;
		// Another's body is moved by the network, not by physics, so it can lose its colliders safely.
		// The owner's own holds still while they are down, so it can lose them too. Sitting, a body is
		// carried by its seat, and would only push against what carries it.
		if ( controller.ColliderObject.IsValid() ) controller.ColliderObject.Enabled = !seated && (IsProxy ? standing : !IsDown);
		if ( IsProxy ) return;
		// Sitting, Use gets out, and nothing in reach is pressed.
		controller.EnablePressing = !seated;
		if ( controller.Body.IsValid() ) controller.Body.MotionEnabled = !IsDown;
		// Leaning over a map, the hands and eyes are on the map.
		var leaning = LeaningOver.IsValid();
		controller.UseInputControls = HasCharacter && !IsDown && !leaning;
		controller.UseLookControls = HasCharacter && !leaning;
		controller.UseCameraControls = HasCharacter;
		// The mouse reaches a map through the camera, and only while leaning over one.
		if ( Scene.Camera is { } camera && camera.GetComponent<WorldInput>() is var input && leaning != input.IsValid() )
		{
			if ( leaning ) camera.AddComponent<WorldInput>();
			else input!.Destroy();
		}
		// A wounded leg walks, slowly, and neither runs nor jumps.
		controller.WalkSpeed = IsLimping ? MathF.Min( LimpSpeed, _speeds.Walk ) : _speeds.Walk;
		controller.RunSpeed = IsLimping ? controller.WalkSpeed : _speeds.Run;
		controller.DuckedSpeed = IsLimping ? MathF.Min( controller.WalkSpeed, _speeds.Ducked ) : _speeds.Ducked;
		controller.JumpSpeed = IsLimping || seated ? 0f : _speeds.Jump;
	}

	// After the controller's own view, before anything that holds the camera for a test.
	int ICameraModifier.CameraOrder => 10;

	/// <summary>
	/// Owner: someone leaning over a map looks straight down at it. Someone down sees from their body's
	/// eyes, on the ground, and can still look about.
	/// </summary>
	void ICameraModifier.ModifyCamera( CameraComponent camera, ref CameraView view )
	{
		if ( IsProxy || camera != Scene.Camera ) return;
		if ( !IsDown && LeaningOver is { } map && map.IsValid() )
		{
			UI.MapBoard.Frame( map, ref view );
			return;
		}
		if ( !IsDown || Controller is not { } controller ) return;
		if ( Sleeper.Find( CharacterId ) is not { IsLimp: true, Renderer: { } body } || body.GetAttachment( "eyes" ) is not { } eyes ) return;
		view.Position = eyes.Position;
		view.Rotation = controller.EyeAngles.ToRotation();
		// Close enough to the ground not to see through it.
		view.ZNear = MathF.Min( view.ZNear, 1f );
	}

	/// <summary>
	/// Every host request starts here. Whoever sent it pays for it from their own budget, whichever
	/// pawn it names; it must name their own pawn, so one player cannot act as another; and most
	/// requests need a character in the city. Each refusal is journaled, as a run of them is how a
	/// cheat shows up, but a flood of them is written as a count.
	/// </summary>
	private bool Authorize( string request, out Connection caller, out HexagonServices game, bool needsCharacter = true )
	{
		caller = Rpc.Caller;
		game = HexagonServices.Current!;
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
		if ( HexagonServices.Current is not { } game || Network.Owner is not { } owner ) return;
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
		// Also as the city closes, when the scene may no longer be the active one.
		if ( !Networking.IsHost || _character is null || HexagonServices.Get( Scene )?.Roster is not { } roster ) return;
		// Someone down is where their body lies, which may have been knocked from where they fell.
		if ( _character.IsDown && Sleeper.Find( _character.Id ) is { IsLimp: true } body ) body.HostKeepPosition( _character );
		else
		{
			// Someone sitting in a vehicle is kept beside it: a restart does not keep their seat.
			var at = _seat.IsValid() ? _seat!.HostExitPoint() : HostPosition;
			_character.Position = new[] { at.x, at.y, at.z };
		}
		roster.Save( _character );
	}

	/// <summary>Host: the loaded character, for other host systems such as doors.</summary>
	public CharacterData? HostCharacter => Networking.IsHost ? _character : null;

	/// <summary>Host: issues an item to the loaded character from a named source and tells the owner. A key comes cut to its pattern.</summary>
	public Result HostIssue( string source, ItemDefinition definition, Actor by, int count = 1, string? pattern = null )
	{
		if ( _character is null || HexagonServices.Current is not { Transfers: { } transfers, Roster: { } roster } )
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
		if ( !Networking.IsHost || HexagonServices.Current?.Journal is not { } journal ) return;
		var witnesses = at is { } place ? HostWitnesses( place, by.Character ) : Array.Empty<Guid>();
		journal.Record( kind, by, subject, ok, at is { } where ? new[] { where.x, where.y, where.z } : null, witnesses, data );
	}

	private static Guid[] HostWitnesses( Vector3 at, Guid? except ) =>
		Game.ActiveScene.GetAllComponents<Player>()
			.Where( other => other._character is not null && other._character.Id != except && other.HostPosition.Distance( at ) <= WitnessRange )
			.Select( other => other._character!.Id ).ToArray();

	/// <summary>How near a character must be to count as having seen an act: the range of ordinary speech.</summary>
	public const float WitnessRange = 300f;

	private void SendCharacterList( Connection caller, HexagonServices game )
	{
		// The list is the menu, and a client takes it as leaving whoever it was. A player in the city
		// is sent it when they leave, whatever they asked of the menu meanwhile.
		if ( _character is null )
		{
			var summaries = game.Roster!.OwnedBy( SteamIdOf( caller ) )
				.Select( value => new CharacterSummary( value.Id, value.Name,
					(FactionDefinition.Find( value.Faction )?.Title ?? (value.Faction.Length == 0 ? "Unaffiliated" : value.Faction)) + (value.Rank.Length > 0 ? $", {value.Rank}" : string.Empty),
					value.FirstEntered is not null, value.DiedAt, value.ArrivesAt ) )
				.ToArray();
			using ( Rpc.FilterInclude( caller ) ) ReceiveCharacters( JsonSerializer.Serialize( summaries ) );
		}
		HostSendAccount();
	}

	internal void SendPrivateState()
	{
		if ( _character is null || Network.Owner is not { } owner ) return;
		// Private state changes whenever the inventory does, which is also when what is in hand may have left it.
		HostSyncVitals();
		var marks = StampMarks( _character ).ToArray();
		var titles = Scene.GetAllComponents<Property>().Where( value => Titles.ActsFor( value.HostOwner, value.Office, _character.Id, marks ) )
			.Select( value => new HeldTitle( value.GameObject.Id, value.Title, value.HostOwner is null ? value.Office : string.Empty ) ).ToArray();
		using ( Rpc.FilterInclude( owner ) )
			ReceivePrivateState( _character.Name, _character.Faction, _character.Rank, JsonSerializer.Serialize( Seen.Inventory( _character.Inventory ) ),
				JsonSerializer.Serialize( _character.Known ), _character.Health, JsonSerializer.Serialize( titles ), string.Join( ';', _character.Wounds ) );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveCharacters( string json )
	{
		Characters = JsonSerializer.Deserialize<CharacterSummary[]>( json ) ?? Array.Empty<CharacterSummary>();
		// The list is only ever sent to a player in the menu, so whatever character they had is gone.
		CharacterName = string.Empty;
		FactionPath = string.Empty;
		RankTitle = string.Empty;
		Known = new Dictionary<Guid, string>();
		Inventory = null;
		HeldTitles = Array.Empty<HeldTitle>();
		OfferText = string.Empty;
		OfferOffice = string.Empty;
		Wounded = Array.Empty<string>();
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceivePrivateState( string name, string factionPath, string rank, string inventoryJson, string knownJson, int health, string titlesJson, string wounds )
	{
		Health = health;
		Wounded = wounds.Split( ';', StringSplitOptions.RemoveEmptyEntries );
		Known = JsonSerializer.Deserialize<Dictionary<Guid, string>>( knownJson ) ?? new Dictionary<Guid, string>();
		CharacterName = name;
		FactionPath = factionPath;
		RankTitle = rank;
		Inventory = JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		HeldTitles = JsonSerializer.Deserialize<HeldTitle[]>( titlesJson ) ?? Array.Empty<HeldTitle>();
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveTeleport( Vector3 position ) => Arrive( position );

	/// <summary>Owner: the pawn is where the host put it, at rest.</summary>
	private void Arrive( Vector3 position )
	{
		WorldPosition = position;
		if ( Controller is not { } controller || controller.Body is not { } body || !body.IsValid() ) return;
		// The controller turns gravity off on the ground it stood on, which was at the old place. Forgetting
		// it turns gravity back on until the controller finds ground here.
		controller.PreventGrounding( 0f );
		body.Velocity = Vector3.Zero;
		// A body at rest is asleep, and one put in the air asleep would hang there.
		body.Sleeping = false;
	}
}
