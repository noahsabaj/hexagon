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
		if ( !Networking.IsHost || !Networking.IsActive || GameManager.Instance?.Roster is null ) return false;
		if ( !component.Network.Active ) component.GameObject.NetworkSpawn();
		return true;
	}
}

/// <summary>
/// Storage placed in the scene: a crate, a locker, a desk drawer. What is inside is a holder saved
/// under the scene object's id. Only someone who has opened it is told what it holds.
/// </summary>
[Title( "Hexagon Container" ), Category( "Hexagon" ), Icon( "inventory_2" )]
public sealed class Container : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Crate";
	[Property, Range( 1, 10 )] public int Width { get; set; } = 4;
	[Property, Range( 1, 10 )] public int Height { get; set; } = 4;
	/// <summary>A capability needed to open it, or empty for anyone.</summary>
	[Property] public string Requires { get; set; } = string.Empty;

	private bool _joined;

	public IReadOnlyList<Verb> Verbs => new[] { new Verb( "container.open", "Open", Requires.Length == 0 ? null : Requires ) };

	protected override void OnUpdate()
	{
		if ( !_joined ) _joined = HostScene.Join( this );
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, "container.open" );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new IPressable.Tooltip( $"Open {Title.ToLowerInvariant()}", "inventory_2", string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		var holder = GameManager.Instance!.Holders!.GetOrCreate( GameObject.Id, HolderKinds.Container, Title, Width, Height );
		actor.HostOpen( this, holder, Title );
		return Result.Success();
	}
}

/// <summary>
/// What a character carried, left where it fell. It is a holder like a crate, put into the world at
/// run time like a dropped item, and it goes once it has been emptied.
/// </summary>
[Title( "Hexagon Corpse" ), Category( "Hexagon" ), Icon( "airline_seat_flat" )]
public sealed class Corpse : Component, Component.IPressable, IVerbTarget
{
	private static readonly Verb Search = new( "corpse.search", "Search" );

	public Guid HolderId { get; private set; }
	public IReadOnlyList<Verb> Verbs { get; } = new[] { Search };

	[Sync( SyncFlags.FromHost )] public string Look { get; set; } = string.Empty;
	private string? _shownLook;

	protected override void OnUpdate()
	{
		if ( _shownLook == Look || GetComponentInChildren<SkinnedModelRenderer>() is not { } renderer ) return;
		_shownLook = Look;
		Looks.Apply( renderer, Look );
	}

	public static Corpse? Spawn( HolderData holder )
	{
		if ( holder.Position is not { Length: 3 } at ) return null;
		var body = new GameObject( true, "Body" );
		body.WorldPosition = new Vector3( at[0], at[1], at[2] + 8f );
		body.Tags.Add( "corpse" );
		// It lies as the downed lie: the same figure, in the same clothes, on its back.
		var figure = new GameObject( body, true, "Figure" );
		figure.LocalRotation = Rotation.From( -90, 0, 0 );
		var renderer = figure.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Model.Load( Looks.DefaultBody );
		var collider = body.AddComponent<BoxCollider>();
		collider.Scale = new Vector3( 76, 28, 20 );
		collider.Center = new Vector3( -36, 0, 2 );
		collider.Static = true;
		var corpse = body.AddComponent<Corpse>();
		corpse.HolderId = holder.Id;
		corpse.Look = holder.Look ?? string.Empty;
		body.NetworkSpawn();
		return corpse;
	}

	/// <summary>Host: an emptied body is removed, along with its holder.</summary>
	public static void RemoveIfEmpty( IHolder holder )
	{
		if ( holder is not HolderData { Kind: HolderKinds.Corpse } data || GameManager.Instance?.Holders is not { } holders ) return;
		if ( !holders.Delete( data.Id ).Ok ) return;
		foreach ( var corpse in Game.ActiveScene.GetAllComponents<Corpse>().Where( value => value.HolderId == data.Id ).ToArray() )
			corpse.GameObject.Destroy();
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, Search.Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) => new IPressable.Tooltip( "Search the body", "airline_seat_flat", string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( GameManager.Instance!.Holders!.Find( HolderId ) is not { } holder ) return Result.Fail( ErrorCode.NotFound, "There is nothing left." );
		actor.HostOpen( this, holder, holder.Title );
		return Result.Success();
	}
}

/// <summary>
/// An item lying in the world. It is a holder with one thing in it, so dropping and picking up are
/// ordinary transfers and the item is the same object, with the same id, before and after. Anyone
/// can see what it is; that is appearance.
/// </summary>
[Title( "Hexagon World Item" ), Category( "Hexagon" ), Icon( "category" )]
public sealed class WorldItem : Component, Component.IPressable, IVerbTarget
{
	private static readonly Verb Take = new( "item.take", "Pick up" );

	[Sync( SyncFlags.FromHost )] public string DefinitionPath { get; set; } = string.Empty;

	/// <summary>Host: the holder this object stands for.</summary>
	public Guid HolderId { get; private set; }

	public ItemDefinition? Definition => ItemDefinition.Find( DefinitionPath );
	public IReadOnlyList<Verb> Verbs { get; } = new[] { Take };

	/// <summary>Host: puts a ground holder into the world, when it is dropped and again after a restart.</summary>
	public static WorldItem? Spawn( HolderData holder )
	{
		if ( holder.Inventory.Items.FirstOrDefault() is not { } stack || holder.Position is not { Length: 3 } at ) return null;
		var definition = ItemDefinition.Find( stack.Definition );
		var item = new GameObject( true, $"Item - {definition?.Title ?? "Unknown"}" );
		item.WorldPosition = new Vector3( at[0], at[1], at[2] );
		item.Tags.Add( "item" );
		var renderer = Looks.Show( item, definition, 0.2f );
		var collider = item.AddComponent<BoxCollider>();
		// Big enough to look at and press, whatever the model.
		var bounds = renderer.Model.Bounds;
		collider.Center = bounds.Center;
		collider.Scale = definition?.WorldModel is null ? new Vector3( 50, 50, 50 ) : Vector3.Max( bounds.Size, new Vector3( 12, 12, 12 ) );
		collider.Static = true;
		var component = item.AddComponent<WorldItem>();
		component.HolderId = holder.Id;
		component.DefinitionPath = stack.Definition;
		item.NetworkSpawn();
		return component;
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, Take.Id );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Pick up {Definition?.Title ?? "item"}", "category", Definition?.Description ?? string.Empty );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		var game = GameManager.Instance!;
		if ( game.Holders!.Find( HolderId ) is not { } holder || holder.Inventory.Items.FirstOrDefault() is not { } stack )
			return Result.Fail( ErrorCode.NotFound, "It is gone." );
		var taken = actor.HostTakeFrom( holder, stack.Id );
		if ( !taken.Ok ) return taken;
		game.Holders.Delete( holder.Id );
		GameObject.Destroy();
		return Result.Success();
	}
}
