#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// The single editor-authored identity shared by every persistent feature on a
/// scene GameObject. Missing and duplicate IDs are repaired only in edit mode;
/// runtime ambiguity disables the entire object and never invents authority.
/// </summary>
[Title( "Persistent Scene Entity" )]
[Category( "Hexagon" )]
[Icon( "fingerprint" )]
public sealed class PersistentSceneEntity : Component
{
	private static bool _repairingEditorIdentities;
	private bool _validatingRuntimeIdentity;

	[Property]
	public Guid PersistentId { get; set; }

	public SceneEntityId? Identity => PersistentId == Guid.Empty
		? null
		: new SceneEntityId( PersistentId );

	protected override void OnValidate()
	{
		base.OnValidate();
		if ( !Scene.IsEditor || Game.IsPlaying || _repairingEditorIdentities ) return;
		RepairEditorIdentities( Scene );
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( Game.IsPlaying ) ValidateRuntimeIdentity();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( Game.IsPlaying ) ValidateRuntimeIdentity();
	}

	private void ValidateRuntimeIdentity()
	{
		if ( _validatingRuntimeIdentity || !GameObject.Enabled ) return;
		_validatingRuntimeIdentity = true;
		try
		{
			var entries = Collect( Scene );
			var path = StablePath( this );
			var resolution = SceneIdentityValidator.ValidateRuntime(
				entries.Select( entry => entry.Candidate ) )
				.First( candidate => string.Equals( candidate.StablePath, path, StringComparison.Ordinal ) );
			if ( resolution.Enabled ) return;

			Log.Error( $"HEXAGON_FATAL_SCENE_IDENTITY {resolution.FatalDiagnostic}" );
			GameObject.Enabled = false;
		}
		finally
		{
			_validatingRuntimeIdentity = false;
		}
	}

	private static void RepairEditorIdentities( Scene scene )
	{
		_repairingEditorIdentities = true;
		try
		{
			var entries = Collect( scene );
			var byPath = entries.ToDictionary( entry => entry.Candidate.StablePath, StringComparer.Ordinal );
			var repaired = SceneIdentityValidator.RepairForEditor(
				entries.Select( entry => entry.Candidate ), SceneEntityId.New );
			foreach ( var resolution in repaired )
			{
				if ( !resolution.Repaired || resolution.EffectiveId is null ) continue;
				byPath[resolution.StablePath].Component.PersistentId = resolution.EffectiveId.Value.Value;
			}
		}
		finally
		{
			_repairingEditorIdentities = false;
		}
	}

	private static IReadOnlyList<IdentityEntry> Collect( Scene scene ) => scene
		.GetAllObjects( false )
		.SelectMany( gameObject => gameObject.GetComponents<PersistentSceneEntity>( true ) )
		.Select( component => new IdentityEntry(
			component,
			new SceneIdentityCandidate( StablePath( component ), component.Identity ) ) )
		.OrderBy( entry => entry.Candidate.StablePath, StringComparer.Ordinal )
		.ToArray();

	private static string StablePath( PersistentSceneEntity component )
	{
		var names = new Stack<string>();
		for ( var current = component.GameObject; current is not null && current is not Sandbox.Scene; current = current.Parent )
			names.Push( current.Name );
		return $"{string.Join( "/", names )}#{component.GameObject.Id:N}";
	}

	private sealed record IdentityEntry(
		PersistentSceneEntity Component,
		SceneIdentityCandidate Candidate );
}
