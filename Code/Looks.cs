#nullable enable

using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What can be seen of someone or something. A look is a body and what is worn on it, written as one
/// string so it can be synced and saved. It is appearance, so everyone gets it: a uniform is visible
/// even though the faction that issued it is never told to anyone.
/// </summary>
public static class Looks
{
	public const string DefaultBody = "models/citizen/citizen.vmdl";

	public static string Of( FactionDefinition? faction ) => Of( faction?.Body, faction?.Outfit );

	public static string Of( Model? body, IEnumerable<Clothing>? outfit )
	{
		var clothing = new ClothingContainer();
		foreach ( var item in outfit?.Where( value => value.IsValid() ) ?? Enumerable.Empty<Clothing>() ) clothing.Add( item );
		return $"{body?.ResourcePath}|{clothing.Serialize()}";
	}

	/// <summary>Every client dresses the body itself. A server with no screen has nothing to dress.</summary>
	public static void Apply( SkinnedModelRenderer renderer, string look )
	{
		if ( Application.IsDedicatedServer || !renderer.IsValid() ) return;
		var cut = look.IndexOf( '|' );
		var body = cut > 0 ? look[..cut] : string.Empty;
		var worn = cut >= 0 ? look[(cut + 1)..] : string.Empty;
		renderer.Model = Model.Load( body.Length > 0 ? body : DefaultBody );
		(worn.Length > 0 ? ClothingContainer.CreateFromJson( worn ) : new ClothingContainer()).Apply( renderer );
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
