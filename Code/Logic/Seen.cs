#nullable enable

using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// What a player is shown of what someone holds: what each thing is, where it lies and how many there
/// are, and nothing a glance would not tell. A key shows its cut, never the pattern that says which
/// locks it opens. A sheet shows that it is paper, not what is on it: that is read a sheet at a time,
/// by whoever holds it or has it held up for them to read. A map shows what is on it only laid out,
/// to whoever can see it lying there.
/// </summary>
public static class Seen
{
	public static InventoryData Inventory( InventoryData inventory ) => new()
	{
		Width = inventory.Width,
		Height = inventory.Height,
		Items = inventory.Items.Select( Item ).ToList()
	};

	private static ItemStack Item( ItemStack item ) => item.Pattern is null && item.Paper is null && item.Map is null ? item : new ItemStack
	{
		Id = item.Id, Definition = item.Definition, X = item.X, Y = item.Y, Width = item.Width, Height = item.Height,
		Count = item.Count, Max = item.Max, Frequency = item.Frequency, Pattern = item.Pattern is { } pattern ? Keys.Cut( pattern ) : null, Worn = item.Worn
	};
}
