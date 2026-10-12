#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

internal static class HostScene
{
	/// <summary>
	/// Host: true once the city is loaded and the session is up, having put the object on the network.
	/// The lobby opens after the scene loads, so a scene object cannot do this in OnStart: before
	/// then its synced state reaches nobody and requests about it have nothing to travel on.
	/// </summary>
	public static bool Join( Component component )
	{
		if ( !Networking.IsHost || !Networking.IsActive || HexagonServices.Current?.Roster is null ) return false;
		if ( !component.Network.Active ) component.GameObject.NetworkSpawn();
		return true;
	}
}

/// <summary>
/// Storage placed in the scene: a crate, a locker, a safe. What is inside is a holder saved under the
/// scene object's id. Only someone who has opened it is told what it holds. A scene's author can
/// stock it, once: what the world is built holding is placed the first time it is opened, and
/// nothing refills it. One with a lock opens only to a key cut to its pattern.
/// </summary>
[Title( "Hexagon Container" ), Category( "Hexagon" ), Icon( "inventory_2" )]
public sealed class Container : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Crate";
	[Property, Range( 1, 10 )] public int Width { get; set; } = 4;
	[Property, Range( 1, 10 )] public int Height { get; set; } = 4;
	/// <summary>What its lock is cut to: only a key cut the same opens it. Empty means it has no lock.</summary>
	[Property] public string LockPattern { get; set; } = string.Empty;
	/// <summary>What it holds as the world is built.</summary>
	[Property] public List<Stocked> Stock { get; set; } = new();

	/// <summary>Some of an item, as a scene's author places it.</summary>
	public sealed class Stocked
	{
		[Property] public ItemDefinition? Item { get; set; }
		[Property, Range( 1, 100 )] public int Count { get; set; } = 1;
		/// <summary>For a key, the pattern it is cut to.</summary>
		[Property] public string Pattern { get; set; } = string.Empty;
	}

	private bool _joined;

	public IReadOnlyList<Verb> Verbs { get; } = new[] { new Verb( VerbIds.ContainerOpen, "Open" ) };

	protected override void OnUpdate()
	{
		if ( !_joined ) _joined = HostScene.Join( this );
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, VerbIds.ContainerOpen );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Open {Title.ToLowerInvariant()}", "inventory_2", LockPattern.Length > 0 ? "It locks with a key." : string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( LockPattern.Length > 0 && !actor.HostHasKey( LockPattern ) ) return Result.Fail( ErrorCode.Denied, "It is locked, and you have no key that fits." );
		actor.HostOpen( this, Holder(), Title );
		return Result.Success();
	}

	/// <summary>Host: what it holds, stocked as its author placed it the first time it is asked for.</summary>
	private HolderData Holder()
	{
		var game = HexagonServices.Current!;
		if ( game.Holders!.Find( GameObject.Id ) is { } existing ) return existing;
		var holder = game.Holders.GetOrCreate( GameObject.Id, HolderKinds.Container, Title, Width, Height );
		foreach ( var stocked in Stock.Where( value => value.Item.IsValid() ) )
		{
			Result<string>? cut = stocked.Pattern.Length == 0 ? null : Keys.Pattern( stocked.Pattern );
			var placed = cut is { Ok: false } refused
				? Result<ItemStack>.Fail( refused.Code, refused.Message )
				: stocked.Item!.IssueTo( game.Transfers!, Sources.Scene, holder, Actor.Console, stocked.Count, cut?.Value );
			if ( !placed.Ok ) Log.Warning( $"[scene] {GameObject.Name} cannot hold the {stocked.Item!.Title} it is stocked with: {placed.Message}" );
		}
		game.Holders.Save( holder );
		return holder;
	}
}

/// <summary>
/// What a character carried, left where it fell, on its body lying limp (see <see cref="Bodies"/>). It
/// is a holder like a crate, put into the world at run time like a dropped item, and it goes once it
/// has been emptied, or once staff restore the character, who gets up here with whatever is still on
/// it. It is sent only to clients near enough to see it, and what it looks like only as
/// <see cref="Reveal"/> allows.
/// </summary>
[Title( "Hexagon Corpse" ), Category( "Hexagon" ), Icon( "airline_seat_flat" )]
public sealed class Corpse : Component, Component.IPressable, Component.INetworkVisible, IVerbTarget
{
	private static readonly Verb Search = new( VerbIds.CorpseSearch, "Search" );

	public Guid HolderId { get; private set; }
	public IReadOnlyList<Verb> Verbs { get; } = new[] { Search };

	// What it looks like, sent to all it is sent to once the reveal's hold is over.
	[Sync( SyncFlags.FromHost )] public bool IsShown { get; set; }
	[Sync( SyncFlags.FromHost )] public string ShownLook { get; set; } = string.Empty;
	// What it looks like, as this client was told at once because it could see the place.
	private string? _toldLook;
	private string? _dressedIn;
	private RealTimeSince _sinceSpawned;

	/// <summary>Whether this client knows what the body looks like, and shows it.</summary>
	public bool IsRevealed => IsShown || _toldLook is not null;
	public string Look => IsShown ? ShownLook : _toldLook ?? string.Empty;

	protected override void OnAwake() => _sinceSpawned = 0;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || IsShown || _sinceSpawned < Reveal.HoldSeconds ) return;
		IsShown = true;
		ShownLook = _toldLook ?? string.Empty;
	}

	protected override void OnUpdate()
	{
		// Turned off wherever it is not revealed, so it is looked for whether or not it is on.
		if ( GetComponent<SkinnedModelRenderer>( includeDisabled: true ) is not { } renderer ) return;
		renderer.Enabled = IsRevealed;
		if ( !IsRevealed || _dressedIn == Look ) return;
		_dressedIn = Look;
		Looks.Apply( renderer, Look );
	}

	/// <summary>
	/// Host: puts the body into the world, falling from <paramref name="from"/>, the body that carried
	/// the character as it died, or lying where it was left after a restart.
	/// </summary>
	public static Corpse? Spawn( HolderData holder, SkinnedModelRenderer? from = null, Impact? impact = null )
	{
		if ( holder.Position is not { Length: 3 } at ) return null;
		var body = new GameObject( true, "Body" );
		body.Tags.Add( "corpse" );
		var look = HexagonServices.Current?.Characters?.LookOf( holder ) ?? holder.Look ?? string.Empty;
		body.WorldTransform = from.IsValid() ? from.WorldTransform : Bodies.Lying( Looks.ModelOf( look ), new Vector3( at[0], at[1], at[2] ), 0f );
		Bodies.MakeLimp( body, look, from, impact ).Enabled = false;
		var corpse = body.AddComponent<Corpse>();
		corpse.HolderId = holder.Id;
		body.NetworkSpawn( Reveal.Options );
		corpse.Tell( look );
		using ( Rpc.FilterInclude( Reveal.Witnesses( body.WorldPosition ) ) ) corpse.TellWitnesses( look );
		return corpse;
	}

	/// <summary>
	/// Host: keeps where the body lies with its holder, so a restart, or staff restoring the character,
	/// finds it there. True when it had moved.
	/// </summary>
	public static bool HostKeepPosition( HolderData holder )
	{
		if ( Game.ActiveScene?.GetAllComponents<Corpse>().FirstOrDefault( value => value.HolderId == holder.Id ) is not { } corpse ) return false;
		var at = Bodies.GroundPoint( corpse.GameObject );
		if ( holder.Position is { Length: 3 } was && new Vector3( was[0], was[1], was[2] ).Distance( at ) < 4f ) return false;
		holder.Position = new[] { at.x, at.y, at.z };
		return true;
	}

	private void Tell( string look ) => _toldLook = look;

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private void TellWitnesses( string look ) => Tell( look );

	/// <summary>Host: something the body wore was taken off it, and whoever can see it sees the change.</summary>
	public static void HostRelook( HolderData holder )
	{
		if ( !Networking.IsHost || HexagonServices.Current?.Characters is not { } characters ) return;
		var look = characters.LookOf( holder );
		holder.Look = look;
		foreach ( var corpse in Game.ActiveScene.GetAllComponents<Corpse>().Where( value => value.HolderId == holder.Id ) )
		{
			corpse.Tell( look );
			if ( corpse.IsShown ) corpse.ShownLook = look;
			else using ( Rpc.FilterInclude( Reveal.Witnesses( corpse.WorldPosition ) ) ) corpse.TellWitnesses( look );
		}
	}

	bool INetworkVisible.IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Reveal.IsVisibleTo( connection, WorldPosition );

	/// <summary>Host: an emptied body is removed, along with its holder.</summary>
	public static void RemoveIfEmpty( IHolder holder )
	{
		if ( holder is not HolderData { Kind: HolderKinds.Corpse } data || HexagonServices.Current?.Holders is not { } holders ) return;
		if ( !holders.Delete( data.Id ).Ok ) return;
		foreach ( var corpse in Game.ActiveScene.GetAllComponents<Corpse>().Where( value => value.HolderId == data.Id ).ToArray() )
			corpse.GameObject.Destroy();
	}

	bool IPressable.CanPress( IPressable.Event e ) => IsRevealed;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, Search.Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		IsRevealed ? new IPressable.Tooltip( "Search the body", "airline_seat_flat", string.Empty ) : null;

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( HexagonServices.Current!.Holders!.Find( HolderId ) is not { } holder ) return Result.Fail( ErrorCode.NotFound, "There is nothing left." );
		actor.HostOpen( this, holder, holder.Title );
		return Result.Success();
	}
}

/// <summary>
/// An item lying in the world. It is a holder with one thing in it, so dropping and picking up are
/// ordinary transfers and the item is the same object, with the same id, before and after. Whoever
/// can see it can see what it is; that is appearance. It is sent only to clients near enough to see
/// it, and what it is only as <see cref="Reveal"/> allows, so each machine gives it its model and its
/// shape itself, once it knows.
/// </summary>
[Title( "Hexagon World Item" ), Category( "Hexagon" ), Icon( "category" )]
public sealed class WorldItem : Component, Component.IPressable, Component.INetworkVisible, IVerbTarget
{
	private static readonly Verb Take = new( VerbIds.ItemTake, "Pick up" );

	// What it is, sent to all it is sent to once the reveal's hold is over.
	[Sync( SyncFlags.FromHost )] public bool IsShown { get; set; }
	[Sync( SyncFlags.FromHost )] public string ShownDefinition { get; set; } = string.Empty;
	// What it is, as this client was told at once because it could see the place.
	private string? _toldDefinition;
	private string? _builtFor;
	private RealTimeSince _sinceSpawned;

	/// <summary>Host: the holder this object stands for.</summary>
	public Guid HolderId { get; private set; }

	/// <summary>Whether this client knows what is lying here, and shows it.</summary>
	public bool IsRevealed => IsShown || _toldDefinition is not null;
	public string DefinitionPath => IsShown ? ShownDefinition : _toldDefinition ?? string.Empty;
	public ItemDefinition? Definition => ItemDefinition.Find( DefinitionPath );
	public IReadOnlyList<Verb> Verbs { get; } = new[] { Take };

	/// <summary>Host: puts a ground holder into the world, when it is dropped and again after a restart.</summary>
	public static WorldItem? Spawn( HolderData holder )
	{
		if ( holder.Inventory.Items.FirstOrDefault() is not { } stack || holder.Position is not { Length: 3 } at ) return null;
		// Named for nothing it holds: every client is sent every object's name.
		var item = new GameObject( true, "Item" );
		item.WorldPosition = new Vector3( at[0], at[1], at[2] );
		item.Tags.Add( "item" );
		var component = item.AddComponent<WorldItem>();
		component.HolderId = holder.Id;
		item.NetworkSpawn( Reveal.Options );
		component.Tell( stack.Definition );
		using ( Rpc.FilterInclude( Reveal.Witnesses( item.WorldPosition ) ) ) component.TellWitnesses( stack.Definition );
		return component;
	}

	private void Tell( string definition )
	{
		_toldDefinition = definition;
		Build();
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private void TellWitnesses( string definition ) => Tell( definition );

	protected override void OnAwake() => _sinceSpawned = 0;

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || IsShown || _sinceSpawned < Reveal.HoldSeconds ) return;
		IsShown = true;
		ShownDefinition = _toldDefinition ?? string.Empty;
	}

	protected override void OnUpdate() => Build();

	/// <summary>
	/// Gives the item, on this machine alone, its model and something to look at and press, once this
	/// machine knows what it is. Big enough to see and press, whatever the model.
	/// </summary>
	private void Build()
	{
		if ( !IsRevealed || _builtFor == DefinitionPath ) return;
		_builtFor = DefinitionPath;
		var shape = new GameObject( GameObject, true, "Shape" ) { NetworkMode = NetworkMode.Never };
		var definition = Definition;
		var renderer = Looks.Show( shape, definition, 0.2f );
		var collider = shape.AddComponent<BoxCollider>();
		var bounds = renderer.Model.Bounds;
		collider.Center = bounds.Center;
		collider.Scale = definition?.WorldModel is null ? new Vector3( 50, 50, 50 ) : Vector3.Max( bounds.Size, new Vector3( 12, 12, 12 ) );
		collider.Static = true;
	}

	bool INetworkVisible.IsVisibleToConnection( Connection connection, in BBox worldBounds ) => Reveal.IsVisibleTo( connection, WorldPosition );

	bool IPressable.CanPress( IPressable.Event e ) => IsRevealed;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, Take.Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		IsRevealed ? new IPressable.Tooltip( $"Pick up {Definition?.Title ?? "item"}", "category", Definition?.Description ?? string.Empty ) : null;

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		var game = HexagonServices.Current!;
		if ( game.Holders!.Find( HolderId ) is not { } holder || holder.Inventory.Items.FirstOrDefault() is not { } stack )
			return Result.Fail( ErrorCode.NotFound, "It is gone." );
		var taken = actor.HostTakeFrom( holder, stack.Id );
		if ( !taken.Ok ) return taken;
		game.Holders.Delete( holder.Id );
		GameObject.Destroy();
		return Result.Success();
	}
}
