#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A train that brings goods into the city and takes away what is loaded for it. At each of its times
/// of day it rolls in along its track from the edge of the map to its station, where its wagons,
/// locked to a pattern, can be unloaded and loaded for as long as it waits there. One carries what it
/// brings, the same each time, with a printed manifest of it; another is for what the city sends out.
/// Then it rolls on, out of the map, taking everything still on board out of the world. A train missed
/// while the server was down never comes. Staff can send it, send it away, or hold it. See
/// <see cref="Deliveries"/>.
/// </summary>
[Title( "Hexagon Train" ), Category( "Hexagon" ), Icon( "train" )]
public sealed class Train : Component
{
	public enum Phases { Away, Arriving, Waiting, Departing }

	[Property] public string Title { get; set; } = "Train";
	/// <summary>The line it runs on, in world space: in from the edge of the map, through its station, and out again.</summary>
	[Property] public Spline Track { get; set; } = new();
	/// <summary>Which of the track's points is its station, where it stands and waits.</summary>
	[Property, Range( 0, 32 )] public int StationPoint { get; set; } = 1;
	/// <summary>How fast it runs, in units a second.</summary>
	[Property, Range( 50f, 2000f )] public float Speed { get; set; } = 400f;
	/// <summary>When it comes, as times of day on the server's clock, such as "06:00".</summary>
	[Property] public List<string> Times { get; set; } = new() { "06:00", "18:00" };
	/// <summary>How long it stands at its station before it leaves.</summary>
	[Property, Range( 1f, 240f )] public float WaitMinutes { get; set; } = 10f;
	/// <summary>What its wagons' locks are cut to. Empty means anyone at the station can open them.</summary>
	[Property] public string LockPattern { get; set; } = string.Empty;
	/// <summary>What it brings, each time.</summary>
	[Property] public List<Container.Stocked> Brings { get; set; } = new();
	/// <summary>The paper its manifest is written on, which comes with what it brings.</summary>
	[Property] public ItemDefinition? Manifest { get; set; }
	/// <summary>The stamp on its manifest, such as the railway's.</summary>
	[Property] public ItemDefinition? ManifestStamp { get; set; }

	[Sync( SyncFlags.FromHost )] public Phases Phase { get; set; }

	// Host-side.
	private bool _restored;
	private float _travelled;
	private DateTime? _next;

	/// <summary>Whether its wagons can be opened: it stands at its station.</summary>
	public bool IsWaiting => Phase == Phases.Waiting;

	/// <summary>Its wagons, the one that brings goods first.</summary>
	public IEnumerable<Wagon> Wagons => GameObject.Components.GetAll<Wagon>( FindMode.EverythingInSelfAndDescendants ).OrderBy( value => value.Outbound );

	private float StationDistance => Track.PointCount == 0 ? 0f : Track.GetDistanceAtPoint( Math.Clamp( StationPoint, 0, Track.PointCount - 1 ) );

	protected override void OnUpdate()
	{
		if ( !_restored ) HostRestore();
		// Away, it is nowhere to be seen or bumped into.
		var shown = Phase != Phases.Away;
		foreach ( var renderer in GameObject.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) ) renderer.Enabled = shown;
		foreach ( var collider in GameObject.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) ) collider.Enabled = shown;
	}

	protected override void OnFixedUpdate()
	{
		if ( !_restored || !Networking.IsHost || HexagonServices.Current?.Roster is null ) return;
		// A time that comes while it is still out on the line passes: nothing stacks up.
		if ( _next is { } next && DateTime.Now >= next )
		{
			_next = Deliveries.Next( TimesOfDay(), DateTime.Now );
			if ( Phase == Phases.Away && !Remembered().IsHeld ) HostSend();
		}
		switch ( Phase )
		{
			case Phases.Arriving:
				_travelled = Math.Min( StationDistance, _travelled + Speed * Time.Delta );
				Place( _travelled );
				if ( _travelled >= StationDistance ) HostArrive();
				break;
			case Phases.Waiting:
				if ( Remembered().DepartsAt is { } departs && DateTime.UtcNow >= departs ) HostDepart();
				break;
			case Phases.Departing:
				_travelled += Speed * Time.Delta;
				Place( _travelled );
				if ( _travelled >= Track.Length ) Phase = Phases.Away;
				break;
		}
	}

	/// <summary>
	/// Host: join the session, and pick up where it was. Standing at its station when the server
	/// stopped, it stands there still until its time to leave; if that time has passed, what was on
	/// board has gone with it. Its next time is the next one from now: one missed never comes.
	/// </summary>
	private void HostRestore()
	{
		if ( !HostScene.Join( this ) ) return;
		_restored = true;
		var data = Remembered();
		if ( data.DepartsAt is { } departs )
		{
			if ( DateTime.UtcNow < departs )
			{
				_travelled = StationDistance;
				Place( _travelled );
				Phase = Phases.Waiting;
			}
			else HostTakeAway();
		}
		if ( Phase == Phases.Away ) Place( 0f );
		_next = Deliveries.Next( TimesOfDay(), DateTime.Now );
		foreach ( var time in Times.Where( value => !Deliveries.TimeOfDay( value ).Ok ) )
			Log.Warning( $"[scene] {GameObject.Name} has a time it cannot come at: {Deliveries.TimeOfDay( time ).Message}" );
	}

	private IEnumerable<TimeSpan> TimesOfDay() => Times.Select( Deliveries.TimeOfDay ).Where( value => value.Ok ).Select( value => value.Value );

	/// <summary>Host: it sets off for its station, unless it is already on the line.</summary>
	public Result HostSend()
	{
		if ( Phase != Phases.Away ) return Result.Fail( ErrorCode.Conflict, $"{Title} is already on the line." );
		_travelled = 0f;
		Place( _travelled );
		Phase = Phases.Arriving;
		return Result.Success();
	}

	/// <summary>Host: it leaves its station now.</summary>
	public Result HostSendAway()
	{
		if ( Phase != Phases.Waiting ) return Result.Fail( ErrorCode.Conflict, $"{Title} is not standing at its station." );
		HostDepart();
		return Result.Success();
	}

	/// <summary>Host: whether it comes at its times. Held, it comes only when staff send it.</summary>
	public void HostHold( bool held )
	{
		Remembered().IsHeld = held;
		HexagonServices.Current!.SaveWorld();
	}

	/// <summary>Host: what staff are told of it.</summary>
	public string HostDescribe() =>
		$"{GameObject.Name}: {Phase.ToString().ToLowerInvariant()}{(Remembered().IsHeld ? ", held" : string.Empty)}" +
		(Remembered().DepartsAt is { } departs ? $", leaves at {departs.ToLocalTime():HH:mm}" : _next is { } next ? $", next at {next:HH:mm}" : string.Empty) + ".";

	/// <summary>Host: it stands at its station, its wagon loaded with what it brings and the manifest of it.</summary>
	private void HostArrive()
	{
		var game = HexagonServices.Current!;
		Phase = Phases.Waiting;
		var data = Remembered();
		data.DepartsAt = DateTime.UtcNow.AddMinutes( WaitMinutes );
		var holders = Wagons.Select( value => value.Holder() ).ToList();
		var goods = new List<(string Title, int Count)>();
		if ( holders.FirstOrDefault() is { } cargo )
		{
			foreach ( var brought in Brings.Where( value => value.Item.IsValid() ) )
			{
				Result<string>? cut = brought.Pattern.Length == 0 ? null : Keys.Pattern( brought.Pattern );
				var placed = cut is { Ok: false } refused
					? Result<ItemStack>.Fail( refused.Code, refused.Message )
					: brought.Item!.IssueTo( game.Transfers!, Sources.Delivery, cargo, Actor.Console, brought.Count, cut?.Value );
				if ( placed.Ok ) goods.Add( (brought.Item!.Title, brought.Count) );
				else Log.Warning( $"[scene] {GameObject.Name} cannot carry the {brought.Item!.Title} it brings: {placed.Message}" );
			}
			HostWriteManifest( game, cargo, goods );
		}
		game.Journal!.Record( JournalKinds.TrainArrive, Actor.Console, $"Train:{GameObject.Id:N}",
			data: new[] { ("train", Title), ("brings", Deliveries.Summary( goods )) } );
		game.SaveTogether( holders.Cast<object>().Append( game.World ).ToArray() );
		foreach ( var holder in holders ) Player.HostRefresh( holder );
	}

	/// <summary>Host: the manifest comes with what the train brings: the train, when, what, and the railway's stamp.</summary>
	private void HostWriteManifest( HexagonServices game, IHolder cargo, IReadOnlyList<(string Title, int Count)> goods )
	{
		if ( Manifest is not { IsPaper: true } form ) return;
		var issued = form.IssueTo( game.Transfers!, Sources.Delivery, cargo, Actor.Console );
		if ( !issued.Ok )
		{
			Log.Warning( $"[scene] {GameObject.Name} cannot carry its manifest: {issued.Message}" );
			return;
		}
		foreach ( var line in Deliveries.Lines( goods ).Prepend( $"{Title}, {DateTime.Now.ToString( "d MMMM, HH:mm", CultureInfo.InvariantCulture )}" ) )
			if ( !Papers.Write( issued.Value, form.Lines, line ).Ok ) break;
		if ( ManifestStamp is { IsStamp: true } stamp ) Papers.Stamp( issued.Value, stamp.ResourcePath );
	}

	/// <summary>Host: it rolls on out of the city, taking everything still on board with it.</summary>
	private void HostDepart()
	{
		HostTakeAway();
		Phase = Phases.Departing;
	}

	/// <summary>Host: whatever is on board leaves the world, on the record, and the train is no longer at its station.</summary>
	private void HostTakeAway()
	{
		var game = HexagonServices.Current!;
		var holders = Wagons.Select( value => value.Holder() ).ToList();
		var taken = holders.SelectMany( value => value.Inventory.Items )
			.Select( value => (ItemDefinition.Find( value.Definition )?.Title ?? value.Definition, value.Count) ).ToList();
		foreach ( var holder in holders ) game.Transfers!.DestroyAll( Sinks.Departed, holder, Actor.Console );
		Remembered().DepartsAt = null;
		game.Journal!.Record( JournalKinds.TrainDepart, Actor.Console, $"Train:{GameObject.Id:N}",
			data: new[] { ("train", Title), ("took", Deliveries.Summary( taken )) } );
		game.SaveTogether( holders.Cast<object>().Append( game.World ).ToArray() );
		foreach ( var holder in holders ) Player.HostRefresh( holder );
	}

	/// <summary>Puts it where it is along its track, facing the way it runs.</summary>
	private void Place( float distance )
	{
		if ( Track.PointCount < 2 ) return;
		var sample = Track.SampleAtDistance( Math.Clamp( distance, 0f, Track.Length ) );
		WorldPosition = sample.Position;
		if ( sample.Tangent.WithZ( 0 ).Length > 0.01f ) WorldRotation = Rotation.LookAt( sample.Tangent.WithZ( 0 ).Normal );
	}

	/// <summary>Host: what it remembers, kept with the world.</summary>
	private TrainData Remembered()
	{
		var world = HexagonServices.Current!.World;
		if ( !world.Trains.TryGetValue( GameObject.Id, out var data ) ) world.Trains[GameObject.Id] = data = new TrainData();
		return data;
	}

	protected override void DrawGizmos()
	{
		if ( Track.PointCount < 2 ) return;
		// The track is in world space, and gizmos draw in the object's own.
		Gizmo.Transform = global::Transform.Zero;
		Gizmo.Draw.Color = Color.Yellow;
		var line = Track.ConvertToPolyline();
		for ( var index = 1; index < line.Count; index++ ) Gizmo.Draw.Line( line[index - 1], line[index] );
		Gizmo.Draw.SolidSphere( Track.SampleAtDistance( StationDistance ).Position, 12f );
	}
}

/// <summary>
/// One of a train's wagons, a holder opened while the train stands at its station, by a key cut to the
/// train's lock if it has one. Whoever has it open when the train leaves is shut out of it.
/// </summary>
[Title( "Hexagon Wagon" ), Category( "Hexagon" ), Icon( "rv_hookup" )]
public sealed class Wagon : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Wagon";
	[Property, Range( 1, 10 )] public int Width { get; set; } = 6;
	[Property, Range( 1, 10 )] public int Height { get; set; } = 4;
	/// <summary>For what the city sends out, rather than what the train brings.</summary>
	[Property] public bool Outbound { get; set; }

	public IReadOnlyList<Verb> Verbs { get; } = new[] { new Verb( VerbIds.WagonOpen, "Open" ) };

	private Train? Train => GameObject.Components.GetInAncestorsOrSelf<Train>();

	bool IPressable.CanPress( IPressable.Event e ) => Train is { IsWaiting: true };

	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, VerbIds.WagonOpen );
		return true;
	}

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Open {Title.ToLowerInvariant()}", "rv_hookup",
			(Outbound ? "What is loaded leaves with the train." : "What the train brings.") + (Train is { LockPattern.Length: > 0 } ? " It locks with a key." : string.Empty) );

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( Train is not { IsWaiting: true } train ) return Result.Fail( ErrorCode.Conflict, "The train is not standing at its station." );
		if ( train.LockPattern.Length > 0 && !actor.HostHasKey( train.LockPattern ) ) return Result.Fail( ErrorCode.Denied, "It is locked, and you have no key that fits." );
		actor.HostOpen( this, Holder(), Title, () => train.IsWaiting );
		return Result.Success();
	}

	/// <summary>Host: what it carries.</summary>
	internal HolderData Holder()
	{
		var holders = HexagonServices.Current!.Holders!;
		return holders.Find( GameObject.Id ) ?? holders.GetOrCreate( GameObject.Id, HolderKinds.Wagon, Title, Width, Height );
	}
}
