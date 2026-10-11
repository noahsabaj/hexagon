#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hexagon.Logic;
using Sandbox;
using Sandbox.Network;

namespace Hexagon;

/// <summary>
/// What runs the city: it opens the lobby, gives each connection a pawn, and owns the host's saved
/// state. It is one of the engine's scene systems, so every scene has it without anything placed in
/// it, and a game sets its rules under Project Settings, Systems, for every scene or for one. The
/// services exist on the host only, from the moment the engine says the host's scene is loaded.
/// </summary>
[Title( "Hexagon" ), Category( "Hexagon" ), Icon( "location_city" )]
public sealed class HexagonServices : GameObjectSystem<HexagonServices>, ISceneStartup, Component.INetworkListener
{
	/// <summary>Where the world's own document is kept.</summary>
	public const string WorldPath = "world.json";

	public HexagonServices( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishFixedUpdate, 0, Tick, "Hexagon" );
	}

	/// <summary>Host-only. Null on clients, and until the host's scene is loaded.</summary>
	public CharacterRoster? Roster { get; private set; }
	public DocumentStore? Store { get; private set; }
	public Journal? Journal { get; private set; }
	public Transfers? Transfers { get; private set; }
	public HolderStore? Holders { get; private set; }
	public WorldData World { get; private set; } = new();

	[Property] public float AutosaveSeconds { get; set; } = 60f;
	/// <summary>How far a character sees, in world units. A client is sent nobody farther from its character than this.</summary>
	[Property] public float SightRange { get; set; } = 4000f;

	/// <summary>What death means in this game. Hexagon supplies the pipeline; this is the policy, and death is final unless a game says otherwise.</summary>
	[Property, Group( "Death" )] public DeathPolicy Death { get; set; } = DeathPolicy.Permanent;
	/// <summary>Whether a dying character leaves a body holding what it carried.</summary>
	[Property, Group( "Death" )] public bool DeathDropsBelongings { get; set; } = true;
	/// <summary>How long a character stays down before it bleeds out. Zero means until someone acts.</summary>
	[Property, Group( "Death" )] public float DownedSeconds { get; set; } = 180f;
	/// <summary>Whether bleeding out is death. Otherwise the character gets back up, barely.</summary>
	[Property, Group( "Death" )] public bool BleedOutKills { get; set; }
	/// <summary>A capability needed to finish someone who is down, or empty for anyone.</summary>
	[Property, Group( "Death" )] public string FinishRequires { get; set; } = string.Empty;
	/// <summary>How many characters an account may have alive at once.</summary>
	[Property, Group( "Death" ), Range( 1, 10 )] public int LivingCharacters { get; set; } = 1;
	/// <summary>How long after a death the account's next character may enter the city.</summary>
	[Property, Group( "Death" )] public float NewLifeHours { get; set; } = 24f;

	/// <summary>The limits on lives that the roster applies.</summary>
	public LifeRules Lives => new( Math.Max( 1, LivingCharacters ), TimeSpan.FromHours( Math.Max( 0f, NewLifeHours ) ) );

	/// <summary>Whether the host's services are running.</summary>
	public bool IsRunning => Roster is not null;

	private readonly Dictionary<long, AccountData> _accounts = new();
	// What each connection may still ask this second, whichever pawn its requests name.
	private readonly Dictionary<Guid, RateLimiter> _budgets = new();
	private readonly RefusalThrottle _refusals = new();
	// Where each connection's character is, as the host believes, for deciding what its client is sent.
	private readonly Dictionary<Guid, Vector3> _eyes = new();
	private RealTimeSince _sinceAutosave;
	private bool _worldRestored;
	private RealTimeSince _sinceRefusals;

	public override void Dispose()
	{
		// The city closing is the last chance to keep what changed since the last autosave. Its
		// pawns have kept where they stood as they went; the scene empties before its systems go.
		if ( Roster is not null ) SaveAll();
		base.Dispose();
	}

	void ISceneStartup.OnHostInitialize()
	{
		if ( Scene.IsEditor ) return;
		Open();
		if ( !Networking.IsActive ) _ = OpenLobby();
	}

	private async Task OpenLobby()
	{
		LoadingScreen.Title = "Opening the city";
		await GameTask.DelayRealtimeSeconds( 0.1f );
		if ( !Networking.IsActive ) Networking.CreateLobby( new LobbyConfig() );
	}

	/// <summary>Host: loads what the city keeps, and starts its services.</summary>
	private void Open()
	{
		if ( Roster is not null ) return;
		var files = new SandboxFileStore();
		Store = new DocumentStore( files, message => Log.Warning( $"[store] {message}" ) );
		Journal = new Journal( files, warn: message => Log.Warning( $"[journal] {message}" ) );
		Transfers = new Transfers( Journal );
		Holders = new HolderStore( Store );
		Roster = new CharacterRoster( Store );
		// An entry names who it was done to and who saw it, as they were called at the time.
		Journal.NameOf = id => Roster?.Find( id )?.Name;
		World = Store.Load<WorldData>( WorldPath ) ?? new WorldData();
		// Counted from when the city opened, not from when the program started.
		_sinceAutosave = 0;
		Log.Info( $"Hexagon host ready: {Roster.Count} characters, {World.Doors.Count} saved doors." );
		// The engine's own text chat reaches everyone in the session, by account name, from anywhere.
		// Speech here reaches only those in range, in character, so a game that leaves it on has a way
		// around both.
		if ( Sandbox.Platform.Chat.Enabled )
			Log.Warning( "The engine's platform chat is on for this game. It reaches every player by account name, whatever the distance; turn it off in the project's settings." );
	}

	private void Tick()
	{
		if ( Scene.IsEditor || !Networking.IsHost || Roster is null ) return;
		if ( !_worldRestored && Networking.IsActive ) RestoreWorld();
		See();
		if ( _sinceRefusals > 1f ) ReportRefusals();
		if ( _sinceAutosave < AutosaveSeconds ) return;
		_sinceAutosave = 0;
		SaveAll();
	}

	/// <summary>Host: what was in the city when the server stopped is still there.</summary>
	private void RestoreWorld()
	{
		_worldRestored = true;
		// A holder emptied before a crash could remove it has nothing left to find.
		foreach ( var ground in Holders!.OfKind( HolderKinds.Ground ) )
			if ( ground.Inventory.Items.Count == 0 ) Holders.Delete( ground.Id );
			else WorldItem.Spawn( ground );
		foreach ( var body in Holders.OfKind( HolderKinds.Corpse ) )
			if ( body.Inventory.Items.Count == 0 ) Holders.Delete( body.Id );
			else Corpse.Spawn( body );
		// Whoever was down or bound when it stopped is still lying, or standing, where they were.
		foreach ( var character in Roster!.All.Where( value => value.IsDown || value.IsRestrained ).ToArray() )
			Sleeper.HostLeave( character, character.Position is { Length: 3 } at ? new Vector3( at[0], at[1], at[2] ) : FindSpawn().Position );
	}

	/// <summary>Host: writes what changes without being saved at once: where everyone stands, and how long the down have been down.</summary>
	public void SaveAll()
	{
		if ( Roster is null || !Scene.IsValid() ) return;
		// Walked rather than looked up: a closing scene empties its index before it destroys anything,
		// and this manager is destroyed before the pawns, which are added after it.
		foreach ( var player in Scene.Components.GetAll<Player>( FindMode.EverythingInSelfAndDescendants ) ) player.HostSave();
		foreach ( var character in Roster.All.Where( value => value.IsDown ).ToArray() ) Roster.Save( character );
	}

	void Component.INetworkListener.OnActive( Connection connection )
	{
		// The project settings already deny these; repeat it per connection so a bad setting fails closed.
		if ( !connection.IsHost )
		{
			connection.CanSpawnObjects = false;
			connection.CanRefreshObjects = false;
			connection.CanDestroyObjects = false;
		}

		Journal?.Record( JournalKinds.ConnectionJoin, new Actor( (long)connection.SteamId.ValueUnsigned, Name: connection.DisplayName ) );
		var spawn = FindSpawn();
		// Every client is sent every object's name, so a player's own name never goes in one.
		var pawn = new GameObject( true, "Player" );
		pawn.WorldTransform = spawn;
		Player.Compose( pawn );
		// Only the host knows where everyone is, so it alone decides which clients are sent this pawn.
		var options = NetworkSpawnOptions.Default;
		options.Owner = connection;
		options.AlwaysTransmit = false;
		if ( !pawn.NetworkSpawn( options ) )
		{
			pawn.Destroy();
			connection.Kick( "The server could not create your player." );
		}
	}

	void Component.INetworkListener.OnDisconnected( Connection connection )
	{
		_budgets.Remove( connection.Id );
		Journal?.Record( JournalKinds.ConnectionLeave, new Actor( (long)connection.SteamId.ValueUnsigned, Name: connection.DisplayName ) );
		foreach ( var player in Scene.GetAllComponents<Player>().Where( value => value.Network.Owner == connection ).ToArray() )
		{
			// Leaving is not a way out of being down or bound: the character stays where it is.
			player.HostDisconnected();
			player.GameObject.Destroy();
		}
	}

	void Component.INetworkListener.OnBecameHost( Connection previousHost )
	{
		// A new host has none of the saved state, so it would run an empty city. Stop instead.
		Log.Warning( "Hexagon does not support host migration; leaving the session." );
		Networking.Disconnect();
	}

	/// <summary>Host: notes where each connection's character is, for <see cref="Sees"/>.</summary>
	private void See()
	{
		_eyes.Clear();
		foreach ( var player in Scene.GetAllComponents<Player>() )
			if ( player.HostCharacter is not null && player.Network.Owner is { } owner ) _eyes[owner.Id] = player.HostPosition;
	}

	/// <summary>Host: whether the connection has a character in the city near enough to see this place.</summary>
	public bool Sees( Connection connection, Vector3 at ) =>
		_eyes.TryGetValue( connection.Id, out var eye ) && eye.Distance( at ) <= SightRange;

	/// <summary>
	/// Host: takes one request from the connection's budget. Whoever sends a request pays for it,
	/// whichever pawn it names, so asking in someone else's name is no way to flood the host.
	/// </summary>
	public bool HostSpend( Connection caller )
	{
		if ( !_budgets.TryGetValue( caller.Id, out var budget ) ) _budgets[caller.Id] = budget = new RateLimiter( capacity: 12, refillPerSecond: 4 );
		return budget.TryTake( RealTime.Now );
	}

	/// <summary>
	/// Host: journals a refusal that costs the sender nothing to cause, such as a forged request or
	/// one over budget. Each kind is written at most once a second per account, and the rest are
	/// counted into the next. False when this one was only counted.
	/// </summary>
	public bool HostRefuse( Connection caller, string kind, string? subject, params (string Key, string Value)[] data )
	{
		if ( Journal is null || !_refusals.Admit( Player.SteamIdOf( caller ), kind, RealTime.Now, out var held ) ) return false;
		Journal.Record( kind, Player.HostActorOf( caller ), subject, ok: false, data: held > 0 ? data.Append( ("more", held.ToString()) ).ToArray() : data );
		return true;
	}

	/// <summary>Host: what a flood held back is written once the flood has stopped.</summary>
	private void ReportRefusals()
	{
		_sinceRefusals = 0;
		foreach ( var (account, kind, held) in _refusals.Due( RealTime.Now ) )
			Journal?.Record( kind, new Actor( account ), ok: false, data: ("more", held.ToString()) );
	}

	public Transform FindSpawn()
	{
		// A game's own spawn points are where it wants people. A map's are for when it placed none.
		var points = Scene.GetAllComponents<SpawnPoint>().ToArray();
		var placed = points.Where( point => point.GameObject.Components.GetInAncestors<MapInstance>() is null ).ToArray();
		if ( placed.Length > 0 ) points = placed;
		return points.Length == 0 ? global::Transform.Zero : Game.Random.FromArray( points )!.WorldTransform;
	}

	public void SaveWorld() => Store?.Save( WorldPath, World );

	/// <summary>
	/// Host: saves documents that changed together as one change, so a crash keeps all of it or none:
	/// both sides of a hand-over, a body and what it took from the dead. A character or holder deleted
	/// since it changed is left out, as saving it alone would leave it out.
	/// </summary>
	public void SaveTogether( params object[] documents )
	{
		if ( Store is null ) return;
		var change = new List<(string Path, object Document)>();
		foreach ( var document in documents.Distinct() )
		{
			if ( document is CharacterData character && Roster!.Contains( character ) ) change.Add( (CharacterRoster.PathOf( character.Id ), character) );
			else if ( document is HolderData holder && Holders!.Contains( holder ) ) change.Add( (HolderStore.PathOf( holder.Id ), holder) );
			else if ( document is WorldData world ) change.Add( (WorldPath, world) );
		}
		if ( change.Count > 0 ) Store.SaveTogether( change.ToArray() );
	}

	public AccountData Account( long steamId )
	{
		if ( _accounts.TryGetValue( steamId, out var account ) ) return account;
		account = Store?.Load<AccountData>( $"accounts/{steamId}.json" ) ?? new AccountData { SteamId = steamId };
		_accounts[steamId] = account;
		return account;
	}

	public void SaveAccount( AccountData account ) => Store?.Save( $"accounts/{account.SteamId}.json", account );
}
