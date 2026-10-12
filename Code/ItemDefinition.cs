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

	/// <summary>What holding this lets a character do, such as <c>person.restrain</c> for a zip tie.</summary>
	[Property] public List<string> Grants { get; set; } = new();

	/// <summary>The title as it reads inside a sentence: "the flat cap", but "the Civil Protection mask".</summary>
	public string InSentence => Title.Length > 1 && !Title[1..].Any( char.IsUpper ) ? char.ToLowerInvariant( Title[0] ) + Title[1..] : Title;

	/// <summary>Whether using it treats anything: a wound, or lost health.</summary>
	public bool IsTreatment => Consumable && (Heals > 0 || Treats.Count > 0);

	/// <summary>The picture to show for it, if there is one.</summary>
	public string? IconPath => !string.IsNullOrEmpty( Icon ) ? Icon : Wearable?.Icon.Path is { Length: > 0 } worn ? worn : null;

	/// <summary>
	/// Host: new ones enter the world from a named source, a key already cut to its pattern. All of
	/// them arrive or none do. Clothing can arrive <paramref name="worn"/>, one piece at a time.
	/// </summary>
	public Result<ItemStack> IssueTo( Transfers transfers, string source, IHolder to, Actor by, int count = 1, string? pattern = null, bool worn = false )
	{
		if ( pattern is not null && !IsKey ) return Result<ItemStack>.Fail( ErrorCode.Invalid, $"{Title} is not a key, so it is cut to nothing." );
		if ( worn && !Wearable.IsValid() ) return Result<ItemStack>.Fail( ErrorCode.Invalid, $"{Title} is not something to wear." );
		return transfers.Issue( source, to, ResourcePath, Width, Height, by, count, MaxStack, pattern, worn );
	}

	public static ItemDefinition? Find( string path ) =>
		ResourceLibrary.TryGet<ItemDefinition>( path, out var definition ) ? definition : null;
}
