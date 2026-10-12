#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;
using Sandbox.UI;

namespace Hexagon.UI;

/// <summary>What the hands do with a map leant over: draw on it, write a word on it, or wipe something off.</summary>
public enum MapTool
{
	Draw,
	Write,
	Wipe
}

/// <summary>Owner: how this client is marking the map it leans over. Counters are moved with any.</summary>
public static class MapTools
{
	public static MapTool Tool { get; set; }
	/// <summary>The word to write where the map is next clicked.</summary>
	public static string Word { get; set; } = string.Empty;

	public static void Reset()
	{
		Tool = MapTool.Draw;
		Word = string.Empty;
	}
}

/// <summary>
/// A map laid out on a table, as each machine draws it for itself: an engine world panel lying flat,
/// printed side up, with its far edge away from whoever laid it out, and the counters' box along its
/// near edge. It shows whoever can see the table what is on the map. Whoever leans over it reaches it
/// with the mouse, through the engine's <see cref="Sandbox.WorldInput"/> on the camera.
/// </summary>
public sealed class MapBoard : PanelComponent
{
	/// <summary>The depth of the box the counters wait in, along the map's near edge, in world units.</summary>
	public const float TrayDepth = 4f;
	/// <summary>How far above where it lies the paper is drawn, so the table does not show through.</summary>
	public const float Lift = 0.3f;
	/// <summary>Drawn at twice the engine's resolution for a world panel, so small words stay legible.</summary>
	private const float Detail = 0.5f;

	public WorldItem? Item { get; set; }
	public ItemDefinition? Definition { get; set; }
	public MapSheet? Sheet { get; private set; }

	/// <summary>How big the board is, in world units: as wide as the map, and as deep as the map and its counters' box.</summary>
	public static Vector2 Size( ItemDefinition map ) => new( map.MapSize.x, map.MapSize.y + TrayDepth );

	/// <summary>
	/// Every machine: gives a map lying in the world something to press and measure reach from, and on a
	/// machine with a screen, the board itself.
	/// </summary>
	public static void Build( GameObject shape, WorldItem item, ItemDefinition map )
	{
		var size = Size( map );
		// As big as the board, and as thin as paper. The item lies along its own forward, away from whoever laid it out.
		var collider = shape.AddComponent<BoxCollider>();
		collider.Center = new Vector3( 0f, 0f, Lift );
		collider.Scale = new Vector3( size.y, size.x, Lift * 2f );
		collider.Static = true;
		if ( Application.IsDedicatedServer ) return;
		var board = new GameObject( shape, true, "Board" ) { NetworkMode = NetworkMode.Never };
		board.LocalPosition = Vector3.Up * Lift;
		// A world panel faces along its forward with its top edge along its up: here, the sky, and the map's far edge.
		board.LocalRotation = Rotation.LookAt( Vector3.Up, Vector3.Forward );
		var panel = board.AddComponent<Sandbox.WorldPanel>();
		panel.RenderScale = Detail;
		panel.PanelSize = size / Sandbox.UI.WorldPanel.ScreenToWorldScale;
		// Unlit: lit, the panel took about four times the light of the table top beside it, in the sun's colour, under a roof.
		// Unlit, the plan shows its own printed colours, though it does not darken with the room.
		panel.Lighting = false;
		var component = board.AddComponent<MapBoard>();
		component.Item = item;
		component.Definition = map;
	}

	/// <summary>Owner: the view of someone leaning over a map: from straight above, its near edge at the foot of the screen, all of it in sight.</summary>
	public static void Frame( WorldItem item, ref CameraView view )
	{
		if ( item.Definition is not { IsMap: true } map ) return;
		var size = Size( map );
		var wide = MathF.Tan( view.FieldOfView.Clamp( 20f, 140f ).DegreeToRadian() / 2f );
		var high = wide / Screen.Aspect;
		var height = MathF.Max( size.x / 2f / wide, size.y / 2f / high ) * 1.1f;
		view.Position = item.WorldPosition + Vector3.Up * (Lift + height);
		view.Rotation = Rotation.LookAt( Vector3.Down, item.WorldRotation.Forward );
		view.ZNear = MathF.Min( view.ZNear, 1f );
	}

	protected override void OnStart()
	{
		base.OnStart();
		Panel.Style.Width = Length.Percent( 100 );
		Panel.Style.Height = Length.Percent( 100 );
		Sheet = new MapSheet { Board = this };
		Panel.AddChild( Sheet );
	}
}

/// <summary>
/// The paper of a map laid out: the printed map, the grease pencil on it and the counters, drawn with
/// the engine's painter every frame, and the mouse of whoever leans over it.
/// </summary>
public sealed class MapSheet : Panel
{
	public MapBoard? Board { get; set; }

	private static readonly Color PaperColor = new( 0.93f, 0.89f, 0.78f );
	private static readonly Color TrayColor = new( 0.24f, 0.17f, 0.11f );
	private static readonly Color EdgeColor = new( 0.15f, 0.13f, 0.1f );

	// Owner: the counter being moved, and where it is held; the line being drawn, in panel units; and
	// the line last sent, kept on the paper until the host's copy of the map comes back with it.
	private int? _moving;
	private Vector2 _heldAt;
	private readonly List<Vector2> _line = new();
	private List<Vector2> _sent = new();
	private string? _sentOver;
	private RealTimeSince _sinceSent;
	// Where each word was last drawn, to find the one a hand wipes.
	private readonly Dictionary<int, Rect> _words = new();
	private Texture? _image;
	private string? _imageFor;

	public MapSheet()
	{
		Style.Width = Length.Percent( 100 );
		Style.Height = Length.Percent( 100 );
		Style.PointerEvents = PointerEvents.All;
	}

	private WorldItem? Item => Board?.Item;
	private ItemDefinition? Definition => Board?.Definition;
	private MapData? Map => Item?.Map;
	private bool Leaning => Item.IsValid() && Player.Local?.LeaningOver == Item;

	private Rect MapRect( Vector2 size )
	{
		var depth = Definition is { } map ? map.MapSize.y / MapBoard.Size( map ).y : 1f;
		return new Rect( 0f, 0f, size.x, size.y * depth );
	}

	private Rect TrayRect( Vector2 size )
	{
		var map = MapRect( size );
		return new Rect( 0f, map.Bottom, size.x, size.y - map.Height );
	}

	/// <summary>A place on the map, in thousandths, as a point on the paper.</summary>
	public Vector2 FromMap( int x, int y ) => FromMap( x, y, Box.Rect.Size );

	private Vector2 FromMap( int x, int y, Vector2 size )
	{
		var map = MapRect( size );
		return new Vector2( map.Left + map.Width * x / Maps.Scale, map.Top + map.Height * y / Maps.Scale );
	}

	private (int X, int Y) ToMap( Vector2 at, Vector2 size )
	{
		var map = MapRect( size );
		return ((int)MathF.Round( ((at.x - map.Left) / map.Width).Clamp( 0f, 1f ) * Maps.Scale ),
			(int)MathF.Round( ((at.y - map.Top) / map.Height).Clamp( 0f, 1f ) * Maps.Scale ));
	}

	private static float CounterRadius( Rect map ) => map.Width * 0.016f;

	/// <summary>Where each of the map's counters is: standing on it, waiting in its box, or in the hand moving it.</summary>
	private Vector2 CounterAt( int number, Vector2 size )
	{
		if ( number == _moving ) return _heldAt;
		if ( Map?.Counters.FirstOrDefault( value => value.Number == number ) is { } standing ) return FromMap( standing.X, standing.Y, size );
		var tray = TrayRect( size );
		var count = Definition?.Counters.Count ?? 0;
		return new Vector2( tray.Left + tray.Width * (number + 1) / (count + 1), tray.Top + tray.Height / 2f );
	}

	private int? CounterUnder( Vector2 at, Vector2 size )
	{
		var reach = CounterRadius( MapRect( size ) ) * 1.4f;
		for ( var number = (Definition?.Counters.Count ?? 0) - 1; number >= 0; number-- )
			if ( CounterAt( number, size ).Distance( at ) <= reach ) return number;
		return null;
	}

	private MapMark? MarkUnder( Vector2 at, Vector2 size )
	{
		if ( Map is not { } map ) return null;
		var reach = CounterRadius( MapRect( size ) );
		MapMark? nearest = null;
		var best = float.MaxValue;
		foreach ( var mark in map.Marks )
		{
			float distance;
			if ( mark.Text.Length > 0 ) distance = _words.TryGetValue( mark.Id, out var rect ) && rect.Grow( reach ).IsInside( at ) ? 0f : float.MaxValue;
			else
			{
				distance = float.MaxValue;
				var points = Points( mark, size );
				for ( var i = 1; i < points.Length; i++ ) distance = MathF.Min( distance, Distance( points[i - 1], points[i], at ) );
			}
			if ( distance > reach || distance >= best ) continue;
			best = distance;
			nearest = mark;
		}
		return nearest;
	}

	/// <summary>How far a point is from the nearest part of a stretch of line.</summary>
	private static float Distance( Vector2 from, Vector2 to, Vector2 point )
	{
		var along = to - from;
		var part = along.LengthSquared < 0.0001f ? 0f : (Vector2.Dot( point - from, along ) / along.LengthSquared).Clamp( 0f, 1f );
		return (from + along * part).Distance( point );
	}

	private Vector2[] Points( MapMark mark, Vector2 size )
	{
		var points = new Vector2[mark.Points.Count / 2];
		for ( var i = 0; i < points.Length; i++ ) points[i] = FromMap( mark.Points[i * 2], mark.Points[i * 2 + 1], size );
		return points;
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		if ( e.MouseButton == MouseButtons.Left ) Press( e.LocalPosition );
	}

	protected override void OnMouseMove( MousePanelEvent e ) => Drag( e.LocalPosition );

	protected override void OnMouseUp( MousePanelEvent e )
	{
		if ( e.MouseButton == MouseButtons.Left ) Release( e.LocalPosition );
	}

	/// <summary>A hand put down on the paper: on a counter it takes hold of it; on the map it draws, writes or wipes.</summary>
	public void Press( Vector2 at )
	{
		if ( !Leaning || Player.Local is not { } player || Item is not { } item ) return;
		var size = Box.Rect.Size;
		if ( CounterUnder( at, size ) is { } counter )
		{
			_moving = counter;
			_heldAt = at;
			return;
		}
		if ( !MapRect( size ).IsInside( at ) ) return;
		var place = ToMap( at, size );
		switch ( MapTools.Tool )
		{
			case MapTool.Draw:
				_line.Clear();
				_line.Add( at );
				break;
			case MapTool.Write when MapTools.Word.Length > 0:
				player.RequestMapWrite( item, place.X, place.Y, MapTools.Word );
				MapTools.Word = string.Empty;
				break;
			case MapTool.Wipe when MarkUnder( at, size ) is { } mark:
				player.RequestMapWipe( item, mark.Id );
				break;
		}
	}

	/// <summary>The hand moving on the paper: a counter held goes with it, and a line being drawn follows it.</summary>
	public void Drag( Vector2 at )
	{
		if ( _moving is not null )
		{
			_heldAt = at;
			return;
		}
		if ( _line.Count == 0 || _line.Count >= Maps.MaximumPoints ) return;
		// A point at least every so far along, so a slow hand does not spend the line's points in one place.
		var map = MapRect( Box.Rect.Size );
		if ( at.Distance( _line[^1] ) < map.Width / 120f ) return;
		_line.Add( new Vector2( at.x.Clamp( map.Left, map.Right ), at.y.Clamp( map.Top, map.Bottom ) ) );
	}

	/// <summary>The hand lifted: a counter is put down where it is, on the map or back in its box, and a line drawn is sent.</summary>
	public void Release( Vector2 at )
	{
		if ( Player.Local is not { } player || Item is not { } item ) return;
		var size = Box.Rect.Size;
		if ( _moving is { } counter )
		{
			_moving = null;
			var place = ToMap( at, size );
			if ( Leaning ) player.RequestMapMove( item, counter, place.X, place.Y, MapRect( size ).IsInside( at ) );
			return;
		}
		if ( _line.Count == 0 ) return;
		Drag( at );
		if ( _line.Count >= 2 && Leaning )
		{
			player.RequestMapDraw( item, string.Join( ";", _line.Select( value => ToMap( value, size ) ).Select( value => $"{value.X},{value.Y}" ) ) );
			_sent = _line.ToList();
			_sentOver = item.ShownMap + item.Map?.NextMark;
			_sinceSent = 0;
		}
		_line.Clear();
	}

	public override void Tick()
	{
		base.Tick();
		// Standing up lets go of whatever the hand held.
		if ( !Leaning )
		{
			_moving = null;
			_line.Clear();
		}
		// The host's copy has come back, with the line or without it.
		if ( _sent.Count > 0 && (_sinceSent > 2f || _sentOver != Item?.ShownMap + Item?.Map?.NextMark) ) _sent.Clear();
	}

	public override void OnDraw( Painter painter )
	{
		var size = painter.Bounds.Size;
		var map = MapRect( size );
		var tray = TrayRect( size );
		painter.Stroke = Stroke.None;
		painter.Fill = PaperColor;
		painter.Rect( map );
		painter.Fill = TrayColor;
		painter.Rect( tray );
		if ( Definition?.MapImage is { Length: > 0 } path )
		{
			if ( _imageFor != path )
			{
				_imageFor = path;
				_image = Texture.Load( path );
			}
			// Printed on paper, the picture takes the paper's colour.
			if ( _image is not null ) painter.Texture( _image, map, PaperColor );
		}
		painter.Fill = Fill.None;
		painter.Stroke = Stroke.Solid( EdgeColor, map.Width * 0.003f );
		painter.Outline( map );

		var width = map.Width * 0.004f;
		var font = map.Height * 0.045f;
		_words.Clear();
		foreach ( var mark in Map?.Marks ?? Enumerable.Empty<MapMark>() )
		{
			var ink = Color.Parse( mark.Ink ) ?? Player.PenInk;
			if ( mark.Text.Length > 0 && mark.Points.Count >= 2 )
			{
				var start = FromMap( mark.Points[0], mark.Points[1], size );
				// A font's size is in the panel's own units, which the painter scales to its pixels.
				painter.TextStyle = TextStyle.Default with { FontName = "Roboto", FontSize = font / ScaleToScreen, FontWeight = 700, Italic = true, Color = ink, Alignment = TextFlag.LeftCenter };
				var measured = painter.MeasureText( mark.Text );
				var rect = new Rect( start.x, start.y - measured.y / 2f, measured.x, measured.y );
				painter.Text( mark.Text, rect.Grow( 2f ) );
				_words[mark.Id] = rect;
				continue;
			}
			DrawLine( painter, Points( mark, size ), ink, width );
		}
		var own = Player.Local?.Ink ?? Player.PenInk;
		if ( _sent.Count > 1 ) DrawLine( painter, _sent.ToArray(), own, width );
		if ( _line.Count > 1 ) DrawLine( painter, _line.ToArray(), own, width );

		var radius = CounterRadius( map );
		var colours = Definition?.Counters ?? new List<Color>();
		for ( var number = 0; number < colours.Count; number++ )
		{
			painter.Fill = colours[number];
			painter.Stroke = Stroke.Solid( EdgeColor, radius * 0.25f );
			painter.Circle( CounterAt( number, size ), radius );
		}
	}

	private static void DrawLine( Painter painter, Vector2[] points, Color ink, float width )
	{
		painter.Fill = Fill.None;
		painter.Stroke = new Stroke( ink, width, Stroke.LineCap.Round ) with { Join = Stroke.LineJoin.Round };
		painter.LineSmooth( points );
	}
}
