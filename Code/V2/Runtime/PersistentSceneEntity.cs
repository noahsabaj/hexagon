#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// The single editor-authored identity shared by every persistent feature on a
/// scene GameObject. Validation is delegated to one scene index so startup is
/// O(N), and editor changes are coalesced to one FinishUpdate rebuild.
/// </summary>
[Title( "Persistent Scene Entity" )]
[Category( "Hexagon" )]
[Icon( "fingerprint" )]
public sealed class PersistentSceneEntity : Component
{
	private bool _validatingRuntimeIdentity;
	private bool _runtimeIdentityApproved;
	private bool _disabledForIdentity;

	[Property]
	public Guid PersistentId { get; set; }

	public SceneEntityId? Identity => Game.IsPlaying && !_runtimeIdentityApproved
		? null
		: RawIdentity;

	internal SceneEntityId? RawIdentity => PersistentId == Guid.Empty
		? null
		: new SceneEntityId( PersistentId );

	protected override void OnValidate()
	{
		base.OnValidate();
		PersistentSceneIdentityIndexSystem.Current?.Invalidate();
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( Game.IsPlaying ) PersistentSceneIdentityIndexSystem.Current?.Invalidate();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Game.IsPlaying ) return;
		_runtimeIdentityApproved = false;
		PersistentSceneIdentityIndexSystem.Current?.Invalidate();
	}

	protected override void OnDestroy()
	{
		PersistentSceneIdentityIndexSystem.Current?.Invalidate();
		base.OnDestroy();
	}

	internal void ApplyRuntimeResolution( SceneIdentityResolution resolution )
	{
		if ( resolution.Enabled )
		{
			_runtimeIdentityApproved = true;
			if ( _disabledForIdentity )
			{
				_disabledForIdentity = false;
				GameObject.Enabled = true;
			}
			return;
		}
		_runtimeIdentityApproved = false;
		if ( _validatingRuntimeIdentity || !GameObject.Enabled ) return;
		_validatingRuntimeIdentity = true;
		try
		{
			Log.Error( $"HEXAGON_FATAL_SCENE_IDENTITY {resolution.FatalDiagnostic}" );
			_disabledForIdentity = true;
			GameObject.Enabled = false;
		}
		finally
		{
			_validatingRuntimeIdentity = false;
		}
	}

	internal static string StablePath( PersistentSceneEntity component )
		// The editor-authored GameObject ID is already scene-stable and unique.
		// Including the local name keeps diagnostics readable without repeatedly
		// walking ancestors (which is quadratic for a deep hierarchy).
		=> $"{component.GameObject.Name}#{component.GameObject.Id:N}";
}

/// <summary>Scene-owned identity cache and coalesced rebuild coordinator.</summary>
public sealed class PersistentSceneIdentityIndexSystem : GameObjectSystem<PersistentSceneIdentityIndexSystem>
{
	private readonly Scene _scene;
	private bool _dirty = true;
	private bool _building;

	public PersistentSceneIdentityIndexSystem( Scene scene ) : base( scene )
	{
		_scene = scene;
		Listen( Stage.FinishUpdate, 90, ProcessDirty, "Hexagon persistent scene identity index" );
	}

	public PersistentSceneIdentityIndex? Index { get; private set; }

	public void Invalidate() => _dirty = true;

	/// <summary>
	/// Synchronously publishes the runtime identity index for this scene. Host
	/// composition calls this before any application is allowed to consume a
	/// <see cref="PersistentSceneEntity.Identity"/>; the FinishUpdate listener
	/// remains the coalesced path for later scene changes.
	/// </summary>
	public OperationResult<PersistentSceneIdentityIndex> EnsureRuntimeReady()
	{
		if ( !Game.IsPlaying )
			return OperationResult<PersistentSceneIdentityIndex>.Failure(
				ErrorCode.ConfigurationInvalid,
				"Persistent scene identities can only be published while the game is playing." );
		if ( _building )
			return OperationResult<PersistentSceneIdentityIndex>.Failure(
				ErrorCode.Conflict,
				"Persistent scene identity index is already being rebuilt." );
		if ( _dirty || Index is null ) RebuildRuntime();
		return Index is not null
			? OperationResult<PersistentSceneIdentityIndex>.Success( Index )
			: OperationResult<PersistentSceneIdentityIndex>.Failure(
				ErrorCode.ConfigurationInvalid,
				"Persistent scene identity index was not published." );
	}

	private void ProcessDirty()
	{
		if ( !_dirty || _building ) return;
		if ( _scene.IsEditor && !Game.IsPlaying ) RepairEditor();
		else if ( Game.IsPlaying ) RebuildRuntime();
	}

	private void RebuildRuntime()
	{
		if ( _building ) return;
		_building = true;
		try
		{
			var entries = Collect();
			Index = PersistentSceneIdentityIndex.Build( entries.Select( entry => entry.Candidate ) );
			var byPath = new Dictionary<string, SceneIdentityResolution>( StringComparer.Ordinal );
			foreach ( var resolution in Index.Resolutions ) byPath[resolution.StablePath] = resolution;
			foreach ( var entry in entries ) entry.Component.ApplyRuntimeResolution( byPath[entry.Candidate.StablePath] );
			_dirty = false;
		}
		finally
		{
			_building = false;
		}
	}

	private void RepairEditor()
	{
		if ( _building ) return;
		_building = true;
		try
		{
			var entries = Collect();
			var byPath = entries.ToDictionary( entry => entry.Candidate.StablePath, StringComparer.Ordinal );
			var repaired = SceneIdentityValidator.RepairForEditor(
				entries.Select( entry => entry.Candidate ), SceneEntityId.New );
			foreach ( var resolution in repaired )
			{
				if ( resolution.Repaired && resolution.EffectiveId is not null )
					byPath[resolution.StablePath].Component.PersistentId = resolution.EffectiveId.Value.Value;
			}
			Index = PersistentSceneIdentityIndex.Build( entries.Select( entry =>
				new SceneIdentityCandidate( entry.Candidate.StablePath, entry.Component.RawIdentity ) ) );
			_dirty = false;
		}
		finally
		{
			_building = false;
		}
	}

	private IReadOnlyList<IdentityEntry> Collect() => _scene
		.GetAllObjects( true )
		.SelectMany( gameObject => gameObject.GetComponents<PersistentSceneEntity>( true ) )
		.Select( component => new IdentityEntry(
			component,
			new SceneIdentityCandidate( PersistentSceneEntity.StablePath( component ), component.RawIdentity ) ) )
		.ToArray();

	private sealed record IdentityEntry(
		PersistentSceneEntity Component,
		SceneIdentityCandidate Candidate );
}
