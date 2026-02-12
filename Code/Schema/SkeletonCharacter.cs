namespace Hexagon.Schema;

/// <summary>
/// Example character data for the skeleton schema.
/// This is what schema devs would define in their own project.
/// </summary>
public class SkeletonCharacter : HexCharacterData
{
	[CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
	public string Name { get; set; }

	[CharVar( Default = "A mysterious stranger.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
	public string Description { get; set; }

	[CharVar( Order = 3, ShowInCreation = true )]
	public string Model { get; set; }

	[CharVar( Local = true, Default = 0 )]
	public int Money { get; set; }
}
