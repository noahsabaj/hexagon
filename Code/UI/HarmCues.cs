#nullable enable

using System;
using Hexagon.Logic;
using Sandbox;
using Sandbox.Volumes;

namespace Hexagon.UI;

/// <summary>
/// How harm shows on this client's own screen, drawn by the engine's post-processing rather than by a
/// panel. As the character is hurt, the edges of sight darken and the world pales and softens;
/// while it is down, sight narrows, dims and blurs, but the world can still be made out, since
/// whoever comes near is part of the scene. Each is a global <see cref="PostProcessVolume"/>
/// whose weight follows the character, so the engine blends it over whatever the game's camera already
/// does. Nothing here is networked: it is how one client's character feels.
/// </summary>
public sealed class HarmCues : Component
{
	/// <summary>How far below full health the hurt cue is at its strongest.</summary>
	public const float Worst = 75f;
	/// <summary>How fast a cue follows the character, in weight a second, so a blow arrives rather than snaps.</summary>
	private const float Rate = 3f;

	private PostProcessVolume? _hurt;
	private PostProcessVolume? _down;

	protected override void OnStart()
	{
		_hurt = Volume( "Hurt" );
		Add<Vignette>( _hurt, value =>
		{
			value.Color = new Color( 0.25f, 0.02f, 0.02f );
			value.Intensity = 0.85f;
			value.Smoothness = 0.8f;
			value.Roundness = 0.6f;
		} );
		Add<ColorAdjustments>( _hurt, value => value.Saturation = 0.35f );
		Add<Blur>( _hurt, value => value.Size = 0.2f );

		_down = Volume( "Down" );
		Add<Vignette>( _down, value =>
		{
			value.Color = Color.Black;
			value.Intensity = 0.8f;
			value.Smoothness = 0.7f;
			value.Roundness = 0.6f;
		} );
		Add<ColorAdjustments>( _down, value =>
		{
			value.Saturation = 0.2f;
			value.Brightness = 0.65f;
		} );
		Add<Blur>( _down, value => value.Size = 0.4f );
	}

	protected override void OnUpdate()
	{
		var character = Player.Local is { HasCharacter: true } local ? local : null;
		var down = character is { IsDown: true };
		Follow( _hurt, character is null || down ? 0f : Math.Clamp( (Vitals.Full - character.Health) / Worst, 0f, 1f ) );
		Follow( _down, down ? 1f : 0f );
	}

	/// <summary>A volume everywhere, on an object of its own: a volume applies the effects on its object and below.</summary>
	private PostProcessVolume Volume( string name )
	{
		var holder = new GameObject( GameObject, false, name ) { NetworkMode = NetworkMode.Never };
		var volume = holder.AddComponent<PostProcessVolume>();
		volume.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Infinite };
		volume.BlendWeight = 0f;
		return volume;
	}

	private static void Add<T>( PostProcessVolume volume, Action<T> set ) where T : Component, new() =>
		set( volume.GameObject.AddComponent<T>() );

	/// <summary>Moves a cue's weight toward how the character is, and stops drawing it once it has faded out.</summary>
	private static void Follow( PostProcessVolume? volume, float target )
	{
		if ( volume is null ) return;
		volume.BlendWeight = volume.BlendWeight.Approach( target, Time.Delta * Rate );
		volume.GameObject.Enabled = volume.BlendWeight > 0f;
	}
}
