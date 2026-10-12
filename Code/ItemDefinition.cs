#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;
namespace Hexagon;

/// <summary>An item type, authored as an asset. Inventories store the asset path, never a copy of this.</summary>
[AssetType( Name = "Hexagon Item", Extension = "item", Category = "Hexagon" )]
public sealed class ItemDefinition : GameResource
{
	[Property] public string Title { get; set; } = "Item";
	[Property, TextArea] public string Description { get; set; } = string.Empty;
	[Property, Range( 1, 4 )] public int Width { get; set; } = 1;
	[Property, Range( 1, 4 )] public int Height { get; set; } = 1;
	[Property] public Color Tint { get; set; } = Color.White;
	/// <summary>A picture of it for an inventory, or none to use what it is worn as, if it is clothing, or to show its name.</summary>
	[Property, ImageAssetPath] public string? Icon { get; set; }
	/// <summary>What it looks like lying in the world and held in a hand. None shows a tinted block.</summary>
	[Property] public Model? WorldModel { get; set; }
	/// <summary>How it is carried when in hand, which anyone can see from across the street.</summary>
	[Property] public Sandbox.Citizen.CitizenAnimationHelper.HoldTypes HoldType { get; set; }

	/// <summary>How many share one inventory slot. One means each is its own object, followed by id.</summary>
	[Property, Range( 1, 100 )] public int MaxStack { get; set; } = 1;

	/// <summary>Using it uses it up: food, drink, a bandage.</summary>
	[Property] public bool Consumable { get; set; }
	/// <summary>The button for using it, such as "Eat".</summary>
	[Property] public string UseTitle { get; set; } = "Use";
	/// <summary>What those nearby see when it is used, such as "eats a ration".</summary>
	[Property] public string UseEmote { get; set; } = string.Empty;

	/// <summary>Health restored by using it. Only consumables heal.</summary>
	[Property] public int Heals { get; set; }
	/// <summary>
	/// The wounds it treats, by kind: <c>arm</c>, <c>leg</c>. Each use closes one, on whoever uses it, or
	/// on someone else by whoever holds it in hand. See <see cref="Wounds"/>.
	/// </summary>
	[Property] public List<string> Treats { get; set; } = new();

	/// <summary>Harm done per attack while it is in hand. Zero means it is not a weapon. A weapon wounds where it lands.</summary>
	[Property] public int Damage { get; set; }
	/// <summary>Made to kill: a blow from it to the head is death, whether they stand or are already down.</summary>
	[Property] public bool Lethal { get; set; }
	[Property] public float Range { get; set; } = 1500f;
	/// <summary>Seconds between attacks.</summary>
	[Property] public float Cooldown { get; set; } = 0.5f;
	/// <summary>The item each attack uses up, or none for a weapon that needs no ammunition.</summary>
	[Property] public ItemDefinition? Ammo { get; set; }

	/// <summary>The fuel it holds, poured whole into a vehicle's tank from the hand, which uses it up. Zero means it is not fuel.</summary>
	[Property, Range( 0f, 500f )] public float Fuel { get; set; }

	/// <summary>
	/// It is clothing, worn as this engine clothing piece: put on, it is on the body rather than in
	/// the grid, and anyone can see it. Nothing is worn by itself: a uniform is handed over and put on.
	/// </summary>
	[Property] public Clothing? Wearable { get; set; }

	/// <summary>A new character may arrive wearing it: it is offered when a character is made.</summary>
	[Property] public bool Wardrobe { get; set; }

	/// <summary>It carries speech to every other radio tuned the same. See <see cref="Chat"/>.</summary>
	[Property] public bool IsRadio { get; set; }

	/// <summary>
	/// It is a key. Each one is cut to a pattern, which it carries, and fits every lock with that
	/// pattern: a door's or a container's. See <see cref="Keys"/>.
	/// </summary>
	[Property] public bool IsKey { get; set; }

	/// <summary>
	/// It is a lock, not yet fitted: cut to a pattern it carries, as a key is, it fits into a door or a
	/// locker in place of the lock there, which comes out as one of these. Whoever holds the title to the
	/// building fits it. See <see cref="Property"/>.
	/// </summary>
	[Property] public bool IsLock { get; set; }

	/// <summary>
	/// It is paper: how many lines of writing it takes, one below another. Ink stays, so a written line
	/// is there for good. Zero is nothing to write on. See <see cref="Papers"/>.
	/// </summary>
	[Property, Range( 0, 40 )] public int Lines { get; set; }
	/// <summary>It is a printed form: the blanks printed on it, by the label beside each, each filled in by hand once.</summary>
	[Property] public List<string> Fields { get; set; } = new();
	/// <summary>The blank that a form issued to someone arrives filled in with: their name.</summary>
	[Property] public string BearerField { get; set; } = string.Empty;
	/// <summary>It is a stamp, and this is the mark it leaves, such as an office's name. Every stamp of the kind leaves the same mark.</summary>
	[Property] public string StampMark { get; set; } = string.Empty;
	/// <summary>The colour of the ink a stamp leaves.</summary>
	[Property] public Color StampInk { get; set; } = new Color( 0.2f, 0.25f, 0.6f );

	/// <summary>
	/// It is a planning map, printed with this picture. Laid out on a table, it is marked up by whoever
	/// leans over it. See <see cref="Maps"/>.
	/// </summary>
	[Property, ImageAssetPath] public string? MapImage { get; set; }
	/// <summary>How big a map is laid out, in world units: its width, and its depth from the near edge to the far.</summary>
	[Property] public Vector2 MapSize { get; set; } = new Vector2( 36, 24 );
	/// <summary>The counters that come with a map, in a box beside it, one of each colour listed.</summary>
	[Property] public List<Color> Counters { get; set; } = new();
	/// <summary>The colour something to write with marks a map in: a pen's ink, a pencil's lead.</summary>
	[Property] public Color MapInk { get; set; } = new Color( 0.12f, 0.16f, 0.43f );

	/// <summary>What holding this lets a character do, such as <c>person.restrain</c> for a zip tie.</summary>
	[Property] public List<string> Grants { get; set; } = new();

	/// <summary>
	/// It is a tool that makes something in hand, such as a die striking a coin from a blank. Whatever it
	/// makes carries its mark, its own and no other's. See <see cref="Making"/>.
	/// </summary>
	[Property] public List<MakingRecipe> Makes { get; set; } = new();

	/// <summary>The title as it reads inside a sentence: "the flat cap", but "the Civil Protection mask".</summary>
	public string InSentence => Title.Length > 1 && !Title[1..].Any( char.IsUpper ) ? char.ToLowerInvariant( Title[0] ) + Title[1..] : Title;

	/// <summary>Whether it is something to write on or fill in, and read.</summary>
	public bool IsPaper => Lines > 0 || Fields.Count > 0;

	/// <summary>Whether it is cut to a pattern: a key, or a lock not yet fitted.</summary>
	public bool IsCut => IsKey || IsLock;

	/// <summary>Whether it is a stamp.</summary>
	public bool IsStamp => StampMark.Length > 0;

	/// <summary>Whether it is a tool that makes something in hand.</summary>
	public bool IsTool => Makes.Any( value => value.IsValid );

	/// <summary>Whether it is a planning map.</summary>
	public bool IsMap => !string.IsNullOrEmpty( MapImage );

	/// <summary>Whether using it treats anything: a wound, or lost health.</summary>
	public bool IsTreatment => Consumable && (Heals > 0 || Treats.Count > 0);

	/// <summary>The picture to show for it, if there is one.</summary>
	public string? IconPath => !string.IsNullOrEmpty( Icon ) ? Icon : Wearable?.Icon.Path is { Length: > 0 } worn ? worn : null;

	/// <summary>
	/// Host: new ones enter the world from a named source, a key or a lock already cut to its pattern.
	/// All of them arrive or none do. Clothing can arrive <paramref name="worn"/>, one piece at a time.
	/// </summary>
	public Result<ItemStack> IssueTo( Transfers transfers, string source, IHolder to, Actor by, int count = 1, string? pattern = null, bool worn = false )
	{
		if ( pattern is not null && !IsCut ) return Result<ItemStack>.Fail( ErrorCode.Invalid, $"{Title} is neither a key nor a lock, so it is cut to nothing." );
		if ( worn && !Wearable.IsValid() ) return Result<ItemStack>.Fail( ErrorCode.Invalid, $"{Title} is not something to wear." );
		return transfers.Issue( source, to, ResourcePath, Width, Height, by, count, MaxStack, pattern, worn );
	}

	public static ItemDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<ItemDefinition>( path, out var definition ) ? definition : null;
}
