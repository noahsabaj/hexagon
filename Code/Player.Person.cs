#nullable enable

namespace Hexagon;

// A character is also someone other characters act on, through the CharacterBody on its pawn, which
// is the same whether its player is here or left it lying where it was.
public sealed partial class Player
{
	/// <summary>How near someone must stand to do anything to a person, or hand them something.</summary>
	public const float PersonReach = 120f;
}
