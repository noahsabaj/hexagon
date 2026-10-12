#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hexagon.Logic;

// Plain documents. One JSON file each; see docs/decisions.md for why there is no database.

public sealed class CharacterData : IHolder
{
	public Guid Id { get; set; }
	public long SteamId { get; set; }
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	/// <summary>The faction it belongs to, or empty: everyone is made belonging to none, and joins one in the world.</summary>
	public string Faction { get; set; } = string.Empty;
	/// <summary>Its rank in its faction, by title, given by a superior in person; empty while it belongs to none. See <see cref="Ranks"/>.</summary>
	public string Rank { get; set; } = string.Empty;
	/// <summary>
	/// The body it was made with, for life: the parts of the person that are not clothes, such as a
	/// skin and hair, as the engine's clothing assets name them. What it wears is in its inventory.
	/// </summary>
	public List<string> Body { get; set; } = new();
	public InventoryData Inventory { get; set; } = new();
	public DateTimeOffset CreatedAt { get; set; }
	/// <summary>The earliest it may enter the city: a while after the death that came before it. Null when none did.</summary>
	public DateTimeOffset? ArrivesAt { get; set; }
	/// <summary>When it first entered the city. One that never has can be deleted, because it never lived.</summary>
	public DateTimeOffset? FirstEntered { get; set; }
	/// <summary>When it died, or was retired. The dead are kept, so staff can restore one killed against the rules.</summary>
	public DateTimeOffset? DiedAt { get; set; }
	/// <summary>Where the character last stood, or null to use a spawn point.</summary>
	public float[]? Position { get; set; }
	public int Health { get; set; } = 100;
	public bool IsDown { get; set; }
	/// <summary>How long it has been down while the city was running. Kept here so leaving and returning does not restart it.</summary>
	public double SecondsDown { get; set; }
	public bool IsRestrained { get; set; }
	/// <summary>Where it is wounded, by place, oldest first, until someone treats it. Nothing heals by itself. See <see cref="Hexagon.Logic.Wounds"/>.</summary>
	public List<string> Wounds { get; set; } = new();
	/// <summary>The item in hand, if any. Anyone can see what it is.</summary>
	public Guid? Equipped { get; set; }
	/// <summary>The characters who have introduced themselves to this one, and the name each gave.</summary>
	public Dictionary<Guid, string> Known { get; set; } = new();

	[JsonIgnore] public Guid HolderId => Id;
	[JsonIgnore] public string HolderLabel => $"character:{Id:N}";
	[JsonIgnore] public bool IsDead => DiedAt is not null;
}

public sealed class InventoryData
{
	public int Width { get; set; } = 6;
	public int Height { get; set; } = 4;
	public List<ItemStack> Items { get; set; } = new();
}

public sealed class ItemStack
{
	public Guid Id { get; set; }
	/// <summary>Resource path of the item definition asset.</summary>
	public string Definition { get; set; } = string.Empty;
	public int X { get; set; }
	public int Y { get; set; }
	public int Width { get; set; } = 1;
	public int Height { get; set; } = 1;
	/// <summary>How many identical things are in this slot, and how many it can take.</summary>
	public int Count { get; set; } = 1;
	public int Max { get; set; } = 1;
	/// <summary>What a radio is tuned to. It belongs to the radio, so it goes wherever the radio goes.</summary>
	public string? Frequency { get; set; }
	/// <summary>The pattern a key is cut to. It belongs to the key, so it goes wherever the key goes. See <see cref="Keys"/>.</summary>
	public string? Pattern { get; set; }
	/// <summary>Worn rather than carried: it takes no room in the grid, and anyone can see it. See <see cref="Wearing"/>.</summary>
	public bool Worn { get; set; }
	/// <summary>What is written on it, if it is paper or a printed form, and the stamps on it. It goes wherever the sheet goes. See <see cref="Papers"/>.</summary>
	public PaperData? Paper { get; set; }
	/// <summary>What is drawn on it, if it is a planning map, and where its counters stand. It goes wherever the map goes. See <see cref="Maps"/>.</summary>
	public MapData? Map { get; set; }
	/// <summary>
	/// For things made with a mark, such as tokens off a press or banknotes: one mark for each in the
	/// pile, the top last, saying what made it and the serial printed on it, if any. Each goes wherever
	/// the thing it is on goes. See <see cref="Making"/>.
	/// </summary>
	public List<MadeMark>? Made { get; set; }
}

/// <summary>What made one thing, by the mark it left, and the serial printed on it, if it has one.</summary>
public sealed class MadeMark
{
	/// <summary>The mark of the press or die that made it. Never shown: only compared, by a trained eye.</summary>
	public string By { get; set; } = string.Empty;
	/// <summary>The serial printed on it, which anyone can read, or empty.</summary>
	public string Serial { get; set; } = string.Empty;

	public MadeMark Copy() => new() { By = By, Serial = Serial };
}

/// <summary>
/// What is on a planning map: grease pencil, which wipes off, and the counters that came with it. A place
/// on a map is in thousandths of its width and height, from its top left corner as printed.
/// </summary>
public sealed class MapData
{
	/// <summary>The lines and words on it, in the order they were put down.</summary>
	public List<MapMark> Marks { get; set; } = new();
	/// <summary>The counters standing on it. One that is not here is beside the map, in its box.</summary>
	public List<MapCounter> Counters { get; set; } = new();
	/// <summary>The number the next mark is given, so a mark wiped off is never mistaken for one put down later.</summary>
	public int NextMark { get; set; } = 1;

	public MapData Copy() => new()
	{
		Marks = Marks.Select( value => new MapMark { Id = value.Id, Points = new List<int>( value.Points ), Text = value.Text, Ink = value.Ink } ).ToList(),
		Counters = Counters.Select( value => new MapCounter { Number = value.Number, X = value.X, Y = value.Y } ).ToList(),
		NextMark = NextMark
	};
}

/// <summary>A line drawn on a map, or a word written on it, in the colour of what drew it.</summary>
public sealed class MapMark
{
	public int Id { get; set; }
	/// <summary>A line's points, x then y, one after another; a word's one point is where it starts.</summary>
	public List<int> Points { get; set; } = new();
	/// <summary>The word, or empty for a line.</summary>
	public string Text { get; set; } = string.Empty;
	/// <summary>The colour it was drawn in, as <c>#rrggbb</c>.</summary>
	public string Ink { get; set; } = string.Empty;
}

/// <summary>A counter standing on a map: which of the map's counters, by its number, and where.</summary>
public sealed class MapCounter
{
	public int Number { get; set; }
	public int X { get; set; }
	public int Y { get; set; }
}

/// <summary>
/// What is on a sheet of paper or a printed form. Ink stays: lines are only ever added below, each
/// blank on a form is filled in once, and a stamp goes on top of whatever is written by then.
/// </summary>
public sealed class PaperData
{
	/// <summary>What has been written, line by line, in the order it was put down.</summary>
	public List<string> Lines { get; set; } = new();
	/// <summary>The blanks of a printed form that have been filled in, by the label printed beside each.</summary>
	public Dictionary<string, string> Fields { get; set; } = new();
	/// <summary>The stamps on it, in the order they went on.</summary>
	public List<StampMark> Stamps { get; set; } = new();

	public PaperData Copy() => new()
	{
		Lines = new List<string>( Lines ),
		Fields = new Dictionary<string, string>( Fields ),
		Stamps = Stamps.Select( value => new StampMark { Stamp = value.Stamp, AfterLine = value.AfterLine } ).ToList()
	};
}

/// <summary>
/// A stamp's mark on a sheet: which kind of stamp made it, by its item, and how many lines were written
/// when it went on. A line below a stamp was written after it.
/// </summary>
public sealed class StampMark
{
	public string Stamp { get; set; } = string.Empty;
	public int AfterLine { get; set; }
}

public sealed class AccountData
{
	public long SteamId { get; set; }
	/// <summary>May use the operator commands in game. Granted only from the server console.</summary>
	public bool IsStaff { get; set; }
}

public sealed class WorldData
{
	public Dictionary<Guid, DoorData> Doors { get; set; } = new();
	public Dictionary<Guid, MachineData> Machines { get; set; } = new();
	public Dictionary<Guid, TrainData> Trains { get; set; } = new();
	public Dictionary<Guid, SiteData> Sites { get; set; } = new();
}

/// <summary>What a train remembers: whether staff are holding it, and when it leaves the station it stands at.</summary>
public sealed class TrainData
{
	/// <summary>Held by staff: it does not come at its times until it is let go, though staff can still send it.</summary>
	public bool IsHeld { get; set; }
	/// <summary>When it leaves the station, in UTC, while it stands there; null while it is away or on the move.</summary>
	public DateTime? DepartsAt { get; set; }
}

/// <summary>What a site holds, as of when it was last looked at. See <see cref="Sites"/>.</summary>
public sealed class SiteData
{
	public double Held { get; set; }
	/// <summary>When it last held <see cref="Held"/>, in UTC; it has grown back since.</summary>
	public DateTime Since { get; set; }
}

/// <summary>What a machine remembers between one piece of work and the next.</summary>
public sealed class MachineData
{
	/// <summary>The number of the next serial it prints. No two things it numbers share one.</summary>
	public long NextSerial { get; set; } = 1;
}

public sealed class DoorData
{
	public bool IsOpen { get; set; }
	public bool IsLocked { get; set; }
	/// <summary>The character who holds its title: the right to sell or rent it. Locking it takes its key, as for anyone.</summary>
	public Guid? Owner { get; set; }
}
