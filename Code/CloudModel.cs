#nullable enable

using System.Threading.Tasks;
using Sandbox;

namespace Hexagon;

/// <summary>
/// Puts a model from the cloud on the renderer beside it, mounting its package first wherever the
/// scene loads. A scene that names a cloud model only finds it where the editor has installed it, so a
/// dedicated server and its clients would draw the error model. Mounted on the host, the package
/// becomes one the engine has every client install too, as the engine's own dresser does for clothing.
/// </summary>
[Title( "Hexagon Cloud Model" ), Category( "Hexagon" ), Icon( "cloud_download" )]
public sealed class CloudModel : Component
{
	/// <summary>The package, as <c>org.ident#version</c>, whose primary asset is the model.</summary>
	[Property] public string Package { get; set; } = string.Empty;

	protected override async Task OnLoad()
	{
		if ( string.IsNullOrWhiteSpace( Package ) ) return;
		if ( GetComponent<ModelRenderer>() is not { } renderer ) return;
		if ( renderer.Model is { IsError: false } ) return;

		var model = await Cloud.Load<Model>( Package );
		if ( model is null || model.IsError )
		{
			Log.Warning( $"{GameObject.Name}: the model in {Package} could not be loaded." );
			return;
		}

		if ( renderer.IsValid() ) renderer.Model = model;
	}
}
