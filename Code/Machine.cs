#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What a machine or a tool makes, from what, and how long the work takes, as its author writes it.
/// See <see cref="Making"/>.
/// </summary>
public sealed class MakingRecipe
{
	/// <summary>What the work is called, such as "Strike tokens".</summary>
	[Property] public string Title { get; set; } = "Make";
	/// <summary>What it is made from, all used up in the making.</summary>
	[Property] public List<MakingInput> Inputs { get; set; } = new();
	[Property] public ItemDefinition? Output { get; set; }
	[Property, Range( 1, 100 )] public int Count { get; set; } = 1;
	/// <summary>How long the work takes, for as long as the worker keeps at it.</summary>
	[Property, Range( 0.5f, 120f )] public float Seconds { get; set; } = 5f;
	/// <summary>A machine prints a serial on each thing it makes, numbered on from the last, as on a banknote.</summary>
	[Property] public bool Serial { get; set; }
	/// <summary>What it is worked from without being used up, such as the key a copy is cut from.</summary>
	[Property] public List<MakingInput> Kept { get; set; } = new();
	/// <summary>What else the same work makes, such as the keys that come with a lock.</summary>
	[Property] public List<MakingInput> Also { get; set; } = new();
	/// <summary>What the keys and locks it makes are cut to: nothing, a new pattern, or the pattern of what it is worked from.</summary>
	[Property] public MakingCut Cut { get; set; }

	public bool IsValid => Output.IsValid() && Inputs.Count > 0 && Inputs.Concat( Kept ).Concat( Also ).All( input => input.Item.IsValid() );

	/// <summary>The recipe as the rules weigh it.</summary>
	public Recipe Rule => new( Title, Paths( Inputs ), Output!.ResourcePath, Count, Seconds, Serial, Paths( Kept ), Paths( Also ), Cut );

	/// <summary>What it makes from what, as a line: "Make a lock: Lock x1 and Key x2 from Scrap metal x2."</summary>
	public string Summary => $"{Title}: {Listed( new[] { new MakingInput { Item = Output, Count = Count } }.Concat( Also ) )} from {Listed( Inputs )}" +
		(Kept.Count > 0 ? $", working from {Listed( Kept )}." : ".");

	private static (string, int)[] Paths( IEnumerable<MakingInput> inputs ) => inputs.Select( input => (input.Item!.ResourcePath, input.Count) ).ToArray();

	private static string Listed( IEnumerable<MakingInput> inputs ) => string.Join( " and ", inputs.Select( input => $"{input.Item?.Title} x{input.Count}" ) );
}

/// <summary>Some of one thing a recipe uses up.</summary>
public sealed class MakingInput
{
	[Property] public ItemDefinition? Item { get; set; }
	[Property, Range( 1, 100 )] public int Count { get; set; } = 1;
}

/// <summary>
/// A machine placed in the world that makes things, such as a press striking tokens from blanks. It is
/// loaded like a crate, and makes from what it holds only while someone works it, holding Use until the
/// work is done; what it makes it keeps, for whoever opens it next. Everything it makes carries its
/// mark, which nobody can read, and one that numbers what it makes prints a serial on each. One with
/// a lock works and opens only for a key cut to its pattern.
/// </summary>
[Title( "Hexagon Machine" ), Category( "Hexagon" ), Icon( "precision_manufacturing" )]
public sealed class Machine : Component, Component.IPressable, IVerbTarget
{
	[Property] public string Title { get; set; } = "Press";
	[Property, Range( 1, 10 )] public int Width { get; set; } = 3;
	[Property, Range( 1, 10 )] public int Height { get; set; } = 2;
	/// <summary>What its lock is cut to: only a key cut the same works or opens it. Empty means it has no lock.</summary>
	[Property] public string LockPattern { get; set; } = string.Empty;
	/// <summary>What it makes. Worked, it makes the first it holds everything for.</summary>
	[Property] public List<MakingRecipe> Makes { get; set; } = new();
	/// <summary>The mark it leaves on what it makes, which only comparing shows. Empty is its own: no two machines alike.</summary>
	[Property] public string Mark { get; set; } = string.Empty;
	/// <summary>What the serials it prints begin with.</summary>
	[Property] public string SerialPrefix { get; set; } = "A";

	private bool _joined;

	public IReadOnlyList<Verb> Verbs { get; } = new[] { new Verb( VerbIds.MachineWork, "Work" ), new Verb( VerbIds.MachineOpen, "Open" ) };

	/// <summary>The mark on everything it makes.</summary>
	public string Maker => Mark.Length > 0 ? Mark : $"machine:{GameObject.Id:N}";

	protected override void OnUpdate()
	{
		if ( !_joined ) _joined = HostScene.Join( this );
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	// Use is held for as long as the work goes on, and letting go stops it.
	bool IPressable.Press( IPressable.Event e )
	{
		Player.Local?.RequestAct( this, VerbIds.MachineWork );
		return true;
	}

	void IPressable.Release( IPressable.Event e ) => Player.Local?.RequestStopWork();

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e ) =>
		new IPressable.Tooltip( $"Work the {Title.ToLowerInvariant()}", "precision_manufacturing", Described );

	private string Described =>
		string.Join( " ", Makes.Where( value => value.IsValid ).Select( value => value.Summary ).Append( "Hold to work it." ) ) + (LockPattern.Length > 0 ? " It locks with a key." : string.Empty);

	Result IVerbTarget.Perform( Player actor, Verb verb )
	{
		if ( LockPattern.Length > 0 && !actor.HostHasKey( LockPattern ) ) return Result.Fail( ErrorCode.Denied, "It is locked, and you have no key that fits." );
		var holder = Holder();
		if ( verb.Id != VerbIds.MachineWork )
		{
			actor.HostOpen( this, holder, Title );
			return Result.Success();
		}
		return actor.HostStartMaking( this, holder, holder, Makes, Maker, this );
	}

	/// <summary>Host: what it holds.</summary>
	internal HolderData Holder()
	{
		var holders = HexagonServices.Current!.Holders!;
		return holders.Find( GameObject.Id ) ?? holders.GetOrCreate( GameObject.Id, HolderKinds.Machine, Title, Width, Height );
	}

	/// <summary>Host: what it remembers, kept with the world.</summary>
	internal MachineData Remembered()
	{
		var world = HexagonServices.Current!.World;
		if ( !world.Machines.TryGetValue( GameObject.Id, out var data ) ) world.Machines[GameObject.Id] = data = new MachineData();
		return data;
	}
}
