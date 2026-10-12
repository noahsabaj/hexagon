#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A character's body whenever its pawn does not carry it. Someone down lies limp where they fell, as
/// the engine's ragdoll (see <see cref="Bodies"/>), whether or not their player is still connected:
/// their pawn only looks out of it. Someone bound whose player went stands, bound, where they were
/// left. Leaving is not a way out of a scene: the character stays where it is, keeps bleeding, and its
/// <see cref="CharacterBody"/> can be helped up, searched, treated, released or finished like anyone
/// else in that state. It is the host's object, sent only to clients near enough to see it (see
/// <see cref="Reveal"/>), and its player picks up from here, on getting up or on coming back.
/// </summary>
[Title( "Hexagon Sleeper" ), Category( "Hexagon" ), Icon( "bedtime" )]
public sealed class Sleeper : Component, Component.INetworkVisible, IEmbodiment
{
	[Sync( SyncFlags.FromHost )] public Guid CharacterId { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsDown { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsRestrained { get; set; }
	/// <summary>Lying limp, as the engine's ragdoll, rather than standing.</summary>
	[Sync( SyncFlags.FromHost )] public bool IsLimp { get; set; }
	[Sync( SyncFlags.FromHost )] public bool IsBreathingHard { get; set; }
	// Who it looks like, sent to all it is sent to once the reveal's hold is over.
	[Sync( SyncFlags.FromHost )] public bool IsShown { get; set; }
	[Sync( SyncFlags.FromHost )] public string ShownDescription { get; set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string ShownLook { get; set; } = string.Empty;

	// Who it looks like, as this client was told at once because it could see the place.
	private string? _toldDescription;
	private string? _toldLook;

	/// <summary>Whether this client knows who is lying here, and shows them.</summary>
	public bool IsRevealed => IsShown || _toldLook is not null;
	public string CharacterDescription => IsShown ? ShownDescription : _toldDescription ?? string.Empty;
	public string Look => IsShown ? ShownLook : _toldLook ?? string.Empty;

	/// <summary>The body as it is drawn, or would be: it is turned off wherever it is not revealed.</summary>
	public SkinnedModelRenderer? Renderer => GetComponent<SkinnedModelRenderer>( includeDisabled: true );

	private RealTimeSince _sinceLeft;
	private string? _dressedIn;

	/// <summary>Host: the character lying here, or standing bound; none once its pawn carries it again.</summary>
	public CharacterData? HostCharacter =>
		Networking.IsHost && (IsLimp || Player.HostLoaded( CharacterId ) is null) ? HexagonServices.Current?.Roster?.Find( CharacterId ) : null;

	/// <summary>Host: where someone getting up from here stands.</summary>
	public Vector3 HostGroundPoint => IsLimp ? Bodies.GroundPoint( GameObject ) : WorldPosition;

	bool IEmbodiment.IsShownHere => IsRevealed;
	Vector3 IEmbodiment.HostPosition => WorldPosition;
	// It goes nowhere, so it shows no limp.
	bool IEmbodiment.IsLimping => false;

	/// <summary>
	/// Host: whether the character needs a body here, and which: limp while it is down, standing while
	/// it is bound and its player is away. Null while its pawn carries it, or once nothing should.
	/// </summary>
	private static bool? WantsLimp( CharacterData character ) =>
		HexagonServices.Current?.Roster?.Contains( character ) != true || character.IsDead ? null
		: character.IsDown ? true
		: character.IsRestrained && Player.HostLoaded( character.Id ) is null ? false
		: null;

	/// <summary>Host: someone goes down. Their body falls limp from the one that carried them, pushed by the blow that did it.</summary>
	public static Sleeper? HostFall( CharacterData character, SkinnedModelRenderer? from, Transform at, Impact? impact )
	{
		if ( !Networking.IsHost || WantsLimp( character ) != true ) return null;
		Find( character.Id )?.GameObject.Destroy();
		return Spawn( character, at, limp: true, from, impact );
	}

	/// <summary>Host: leaves a character where it is, if it needs a body here and has none: after its player went, or a restart.</summary>
	public static Sleeper? HostLeave( CharacterData character, Transform at )
	{
		if ( !Networking.IsHost || WantsLimp( character ) is not { } limp ) return null;
		if ( Find( character.Id ) is { } existing )
		{
			existing.HostReconcile( character );
			return existing;
		}
		return Spawn( character, at, limp, null, null );
	}

	/// <summary>The body of this character, if there is one.</summary>
	public static Sleeper? Find( Guid characterId ) =>
		Game.ActiveScene?.GetAllComponents<Sleeper>().FirstOrDefault( value => value.CharacterId == characterId && !value.GameObject.IsDestroyed );

	/// <summary>Host: brings a body in line with its character, and removes it once its pawn carries it again, or nothing should.</summary>
	public static void HostRefresh( CharacterData character )
	{
		if ( Networking.IsHost && Find( character.Id ) is { } sleeper ) sleeper.HostReconcile( character );
	}

	private void HostReconcile( CharacterData character )
	{
		var limp = WantsLimp( character );
		if ( limp is null )
		{
			GameObject.Destroy();
			return;
		}
		if ( limp != IsLimp )
		{
			// Helped up while its player is away, with its hands bound: it stands where it lay.
			var at = new Transform( HostGroundPoint, Rotation.FromYaw( WorldRotation.Yaw() ) );
			var from = Renderer;
			GameObject.Destroy();
			Spawn( character, at, limp.Value, from, null );
			return;
		}
		HostSync( character );
	}

	private static Sleeper Spawn( CharacterData character, Transform at, bool limp, SkinnedModelRenderer? from, Impact? impact )
	{
		var body = new GameObject( true, "Sleeper" );
		body.Tags.Add( "sleeper" );
		var look = LookOf( character );
		SkinnedModelRenderer renderer;
		if ( limp )
		{
			body.WorldTransform = from.IsValid() ? from.WorldTransform : Bodies.Lying( Looks.ModelOf( look ), at.Position, at.Rotation.Yaw() );
			renderer = Bodies.MakeLimp( body, look, from, impact );
		}
		else
		{
			body.WorldTransform = new Transform( at.Position, Rotation.FromYaw( at.Rotation.Yaw() ) );
			renderer = body.AddComponent<SkinnedModelRenderer>();
			renderer.Model = Looks.ModelOf( look );
			// Solid to whoever walks into them or reaches for them. A shot finds the body itself.
			var shape = new GameObject( body, true, "Shape" );
			shape.Tags.Add( "capsule" );
			var collider = shape.AddComponent<BoxCollider>();
			collider.Scale = new Vector3( 32, 32, 72 );
			collider.Center = new Vector3( 0, 0, 36 );
			collider.Static = true;
		}
		// Drawn only where it is revealed, which each machine decides for itself.
		renderer.Enabled = false;
		var sleeper = body.AddComponent<Sleeper>();
		body.AddComponent<CharacterBody>();
		body.AddComponent<BodyCues>();
		sleeper.CharacterId = character.Id;
		sleeper.IsLimp = limp;
		sleeper.HostSync( character );
		body.NetworkSpawn( Reveal.Options );
		if ( !limp ) Bodies.AddHitboxes( renderer, body );
		// Whoever can see the place sees who it is at once; everyone else learns it later, if they come near.
		var description = character.Description;
		sleeper.Tell( description, look );
		using ( Rpc.FilterInclude( Reveal.Witnesses( body.WorldPosition ) ) ) sleeper.TellWitnesses( description, look );
		return sleeper;
	}

	private void HostSync( CharacterData character )
	{
		IsDown = character.IsDown;
		IsRestrained = character.IsRestrained;
		IsBreathingHard = Vitals.BreathesHard( character );
		if ( !IsShown ) return;
		ShownDescription = character.Description;
		ShownLook = LookOf( character );
	}

	/// <summary>Host: keeps where a limp body lies with its character, so a restart finds it there.</summary>
	public void HostKeepPosition( CharacterData character )
	{
		if ( !IsLimp ) return;
		var at = HostGroundPoint;
		character.Position = new[] { at.x, at.y, at.z };
	}

	private void Tell( string description, string look )
	{
		_toldDescription = description;
		_toldLook = look;
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private void TellWitnesses( string description, string look ) => Tell( description, look );

	private static string LookOf( CharacterData character ) => CharacterService.LookOf( character );

	protected override void OnAwake() => _sinceLeft = 0;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || HexagonServices.Current?.Roster is not { } roster ) return;
		if ( roster.Find( CharacterId ) is not { } character )
		{
			GameObject.Destroy();
			return;
		}
		// Up, free, dead, or back in its own body in the city: this body is not theirs any more.
		if ( WantsLimp( character ) != IsLimp )
		{
			HostReconcile( character );
			return;
		}
		IsBreathingHard = Vitals.BreathesHard( character );
		if ( IsShown || _sinceLeft <= Reveal.HoldSeconds ) return;
		IsShown = true;
		HostSync( character );
	}

	protected override void OnUpdate()
	{
		if ( Renderer is not { } renderer ) return;
		renderer.Enabled = IsRevealed;
		// Its own player looks out of it from the ground, through the eyes in its head: they see its shadow.
		var own = IsLimp && Player.Local is { HasCharacter: true } local && local.CharacterId == CharacterId;
		renderer.RenderType = own ? ModelRenderer.ShadowRenderType.ShadowsOnly : ModelRenderer.ShadowRenderType.On;
		if ( !IsRevealed || _dressedIn == Look ) return;
		_dressedIn = Look;
		Looks.Apply( renderer, Look );
	}

	/// <summary>Host: a client is sent this only while its own character is near enough to see it.</summary>
	bool INetworkVisible.IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Reveal.IsVisibleTo( connection, WorldPosition );
}
