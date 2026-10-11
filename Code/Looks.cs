#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What can be seen of someone or something. A look is the body a character was made with and what
/// it wears, written as one string of the engine's clothing assets so it can be synced and saved.
/// It is appearance, so everyone gets it: a uniform is visible, and so is a uniform worn by someone
/// who took it off a prisoner. Nothing about it says who anyone is or what they belong to.
/// </summary>
public static class Looks
{
	public const string DefaultBody = "models/citizen/citizen.vmdl";

	/// <summary>A character's look: its body, and what it wears over it.</summary>
	public static string Of( CharacterData character ) => Of( character.Body, character.Inventory );

	/// <summary>
	/// A body and what is worn on it, as a body left lying still has it on. What is worn goes over
	/// the body: hair under a hat is hidden by it, as the engine says which pieces cannot be worn together.
	/// </summary>
	public static string Of( IEnumerable<string> body, InventoryData worn )
	{
		var clothes = Wearing.Worn( worn ).Select( value => ItemDefinition.Find( value.Definition )?.Wearable )
			.Where( value => value.IsValid() ).Select( value => value! ).ToList();
		var parts = body.Select( value => ResourceLibrary.Get<Clothing>( value ) ).Where( value => value.IsValid() )
			.Where( part => clothes.All( piece => part.CanBeWornWith( piece ) ) );
		return string.Join( ';', parts.Concat( clothes ).Select( value => value.ResourcePath ) );
	}

	/// <summary>The engine clothing pieces a look is made of, by name, as anyone could describe them.</summary>
	public static IEnumerable<string> Pieces( string look ) =>
		Clothing( look ).Select( value => value.ResourceName );

	private static IEnumerable<Clothing> Clothing( string look ) =>
		look.Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
			.Select( value => ResourceLibrary.Get<Clothing>( value ) ).Where( value => value.IsValid() );

	/// <summary>
	/// Every client dresses the body itself. A human skin carries its own model; without one the body
	/// is the engine's citizen. A server with no screen has nothing to dress.
	/// </summary>
	public static void Apply( SkinnedModelRenderer renderer, string look )
	{
		if ( Application.IsDedicatedServer || !renderer.IsValid() ) return;
		var pieces = Clothing( look ).ToList();
		var skin = pieces.FirstOrDefault( value => value.HasHumanSkin && !string.IsNullOrEmpty( value.HumanSkinModel ) );
		renderer.Model = Model.Load( skin?.HumanSkinModel ?? DefaultBody );
		// Built by hand rather than with Add, which silently drops whatever a new piece cannot be worn with:
		// the host has already decided what is worn.
		var outfit = new ClothingContainer();
		foreach ( var piece in pieces ) outfit.Clothing.Add( new ClothingContainer.ClothingEntry( piece ) );
		var dresser = Dresser.GetOrCreate( renderer );
		dresser.UpdateAppearance( outfit );
		dresser.Apply( outfit );
	}

	/// <summary>Gives an object the item's own model, or a small tinted block when its asset names none.</summary>
	public static ModelRenderer Show( GameObject on, ItemDefinition? definition, float blockSize )
	{
		var renderer = on.AddComponent<ModelRenderer>();
		if ( definition?.WorldModel is { } model )
		{
			renderer.Model = model;
			return renderer;
		}
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = definition?.Tint ?? Color.White;
		on.LocalScale = new Vector3( blockSize * (definition?.Width ?? 1), blockSize * (definition?.Height ?? 1), blockSize * 0.6f );
		return renderer;
	}
}
