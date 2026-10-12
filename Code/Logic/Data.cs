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
}

public sealed class DoorData
{
	public bool IsOpen { get; set; }
	public bool IsLocked { get; set; }
	/// <summary>The character who holds its title: the right to sell or rent it. Locking it takes its key, as for anyone.</summary>
	public Guid? Owner { get; set; }
}
