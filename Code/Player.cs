#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>What a player sees of one of their characters before entering the city.</summary>
public sealed record CharacterSummary( Guid Id, string Name, string Faction );

/// <summary>
/// One connection's presence in the city. Movement is the engine's <see cref="PlayerController"/>,
/// simulated by the owner. Only what another character could see is replicated to everyone through
/// <c>[Sync]</c>: that someone is there, and what they look like. A name or a faction is knowledge,
/// not appearance, so it reaches its owner alone by owner-only RPC and a client cannot list who is
/// in the city. A client changes nothing directly: it calls a <c>[Rpc.Host]</c> request, and the
/// host decides.
/// </summary>
[Title( "Hexagon Player" ), Category( "Hexagon" ), Icon( "person" )]
public sealed partial class Player : Component
{
	/// <summary>The pawn this client controls, or null before it has spawned.</summary>
	public static Player? Local { get; private set; }

	[Sync( SyncFlags.FromHost )] public bool HasCharacter { get; set; }
	[Sync( SyncFlags.FromHost )] public string CharacterDescription { get; set; } = string.Empty;

	// Owner-side copies of private state. Empty on every other client.
	public string CharacterName { get; private set; } = string.Empty;
	public string FactionPath { get; private set; } = string.Empty;
	public FactionDefinition? Faction => FactionDefinition.Find( FactionPath );
	public IReadOnlyList<CharacterSummary> Characters { get; private set; } = Array.Empty<CharacterSummary>();
	public InventoryData? Inventory { get; private set; }
	public long Tokens { get; private set; }

	/// <summary>Bumped whenever private state changes so panels can rebuild.</summary>
	public int PrivateVersion { get; private set; }

	// Host-side.
	private CharacterData? _character;
	private readonly RateLimiter _requests = new( capacity: 12, refillPerSecond: 4 );
	private readonly MovementAudit _movement = new();

	/// <summary>
	/// Host: where the host believes this character is. The owner simulates movement, so
	/// <c>WorldPosition</c> is only its claim; every host rule about distance reads this.
	/// </summary>
	public Vector3 HostPosition => _movement.Position;

	/// <summary>How much faster than a run a claim may be before it is disbelieved.</summary>
	public const float SpeedTolerance = 1.5f;

	/// <summary>Builds the pawn on the host, before it is network-spawned, so clients receive it whole.</summary>
	public static void Compose( GameObject pawn )
	{
		pawn.Tags.Add( "player" );
		// The animated model sits on a child: PlayerController moves its renderer locally for the
		// duck bob, which on the root would drag the whole pawn to the origin.
		var body = new GameObject( pawn, true, "Body" );
		var renderer = body.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );

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
		}
		ApplyPresence();
		if ( !IsProxy ) RequestCharacters();
	}

	protected override void OnDestroy()
	{
		if ( Local == this ) Local = null;
	}

	protected override void OnUpdate()
	{
		ApplyPresence();
		if ( IsProxy || !HasCharacter ) return;
		// "Use" reaches a target's first verb through IPressable. "Reload" is its second.
		if ( Input.Pressed( "Reload" ) && Controller?.Hovered?.GetComponent<IVerbTarget>() is Component target &&
			((IVerbTarget)target).Verbs.ElementAtOrDefault( 1 ) is { } second )
			RequestAct( target, second.Id );
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || _character is null || Controller is not { } controller ) return;
		var limit = MathF.Max( controller.RunSpeed, controller.WalkSpeed ) * SpeedTolerance;
		if ( _movement.Observe( WorldPosition, Time.Now, limit ) ) return;
		var claimed = WorldPosition;
		HostRecord( "movement.implausible", ok: false, data: ("claimed", $"{claimed.x:0},{claimed.y:0},{claimed.z:0}") );
		HostTeleport( HostPosition );
	}

	/// <summary>Host: puts the character somewhere. The owner simulates movement, so it is told to move itself.</summary>
	public void HostTeleport( Vector3 position )
	{
		if ( !Networking.IsHost || Network.Owner is not { } owner ) return;
		_movement.Reset( position, Time.Now );
		using ( Rpc.FilterInclude( owner ) ) ReceiveTeleport( position );
	}

	private PlayerController? Controller => GetComponent<PlayerController>( true );

	/// <summary>A connection without a character is in the menu: no body in the world, no movement.</summary>
	private void ApplyPresence()
	{
		if ( Controller is not { } controller ) return;
		if ( controller.Renderer.IsValid() ) controller.Renderer.GameObject.Enabled = HasCharacter;
		if ( IsProxy ) return;
		controller.UseInputControls = HasCharacter;
		controller.UseLookControls = HasCharacter;
		controller.UseCameraControls = HasCharacter;
	}

	/// <summary>
	/// Every host request starts here. The caller must own this pawn, so one player cannot act as
	/// another, and must be within its request budget.
	/// </summary>
	private bool Authorize( out Connection caller, out GameManager game )
	{
		caller = Rpc.Caller;
		game = GameManager.Instance!;
		if ( !Networking.IsHost || game?.Roster is null || caller is null || caller != Network.Owner ) return false;
		if ( _requests.TryTake( RealTime.Now ) ) return true;
		Chat.Tell( caller, "You are doing that too fast." );
		return false;
	}

	private static long SteamIdOf( Connection connection ) => (long)connection.SteamId.ValueUnsigned;

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

	/// <summary>Host: issues an item to the loaded character from a named source and tells the owner.</summary>
	public Result HostIssue( string source, ItemDefinition definition, Actor by )
	{
		if ( _character is null || GameManager.Instance is not { Transfers: { } transfers, Roster: { } roster } )
			return Result.Fail( ErrorCode.NotFound, "That player has no character loaded." );
		var issued = transfers.Issue( source, _character, definition.ResourcePath, definition.Width, definition.Height, by );
		if ( !issued.Ok ) return issued.ToResult();
		roster.Save( _character );
		SendPrivateState();
		return Result.Success();
	}

	/// <summary>Host: whether the loaded character is able to do something, from its faction and what it holds.</summary>
	public bool HostCan( string capability )
	{
		if ( !Networking.IsHost || _character is null ) return false;
		var granted = (FactionDefinition.Find( _character.Faction )?.Capabilities ?? Enumerable.Empty<string>())
			.Concat( _character.Inventory.Items.SelectMany( item => ItemDefinition.Find( item.Definition )?.Grants ?? Enumerable.Empty<string>() ) );
		return Capabilities.Can( capability, granted );
	}

	/// <summary>Host: writes what this character just did to the journal, with where and who was near enough to see.</summary>
	public void HostRecord( string kind, string? subject = null, bool ok = true, params (string Key, string Value)[] data )
	{
		if ( !Networking.IsHost || GameManager.Instance?.Journal is not { } journal ) return;
		var by = _character is not null ? Actor.Of( _character ) : new Actor( Network.Owner is { } owner ? SteamIdOf( owner ) : 0 );
		var witnesses = _character is null
			? Array.Empty<Guid>()
			: Scene.GetAllComponents<Player>()
				.Where( other => other != this && other._character is not null && other.HostPosition.Distance( HostPosition ) <= WitnessRange )
				.Select( other => other._character!.Id ).ToArray();
		var at = HostPosition;
		journal.Record( kind, by, subject, ok, new[] { at.x, at.y, at.z }, witnesses, data );
	}

	/// <summary>How near a character must be to count as having seen an act: the range of ordinary speech.</summary>
	public const float WitnessRange = 300f;

	private void SendCharacterList( Connection caller, GameManager game )
	{
		var summaries = game.Roster!.OwnedBy( SteamIdOf( caller ) )
			.Select( value => new CharacterSummary( value.Id, value.Name, FactionDefinition.Find( value.Faction )?.Title ?? value.Faction ) )
			.ToArray();
		using ( Rpc.FilterInclude( caller ) ) ReceiveCharacters( JsonSerializer.Serialize( summaries ) );
	}

	private void SendPrivateState()
	{
		if ( _character is null || Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) )
			ReceivePrivateState( _character.Name, _character.Faction, JsonSerializer.Serialize( _character.Inventory ), _character.Tokens );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveCharacters( string json )
	{
		Characters = JsonSerializer.Deserialize<CharacterSummary[]>( json ) ?? Array.Empty<CharacterSummary>();
		// The list is only ever sent to a player in the menu, so whatever character they had is gone.
		CharacterName = string.Empty;
		FactionPath = string.Empty;
		Inventory = null;
		Tokens = 0;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceivePrivateState( string name, string factionPath, string inventoryJson, long tokens )
	{
		CharacterName = name;
		FactionPath = factionPath;
		Inventory = JsonSerializer.Deserialize<InventoryData>( inventoryJson );
		Tokens = tokens;
		PrivateVersion++;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceiveTeleport( Vector3 position )
	{
		WorldPosition = position;
		if ( Controller?.Body is { } body && body.IsValid() ) body.Velocity = Vector3.Zero;
	}
}
