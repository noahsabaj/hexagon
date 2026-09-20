#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hexagon.Logic;

// Plain documents. One JSON file each; see docs/decisions.md for why there is no database.

public sealed class CharacterData : IHolder
{
	public Guid Id { get; set; }
	public long SteamId { get; set; }
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string Faction { get; set; } = string.Empty;
	public long Tokens { get; set; }
	public InventoryData Inventory { get; set; } = new();
	public DateTimeOffset CreatedAt { get; set; }
	/// <summary>Where the character last stood, or null to use a spawn point.</summary>
	public float[]? Position { get; set; }
	public int Health { get; set; } = 100;
	public bool IsDown { get; set; }
	public bool IsRestrained { get; set; }
	/// <summary>The item in hand, if any. Anyone can see what it is.</summary>
	public Guid? Equipped { get; set; }
	/// <summary>The characters who have introduced themselves to this one, and the name each gave.</summary>
	public Dictionary<Guid, string> Known { get; set; } = new();

	[JsonIgnore] public Guid HolderId => Id;
	[JsonIgnore] public string HolderLabel => $"character:{Id:N}";
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
}

public sealed class AccountData
{
	public long SteamId { get; set; }
	/// <summary>Faction ids this account may create characters in, beyond the open ones.</summary>
	public List<string> Whitelists { get; set; } = new();
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
	/// <summary>The character who bought it, who may lock it without any other authority.</summary>
	public Guid? Owner { get; set; }
}
