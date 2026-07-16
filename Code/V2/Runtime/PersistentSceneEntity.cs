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
	private Guid _persistentId;

	[Property]
	public Guid PersistentId
	{
		get => _persistentId;
		set
		{
			if ( _persistentId == value ) return;
			_persistentId = value;
			InvalidateRuntimeApproval();
			PersistentSceneIdentityIndexSystem.Current?.Invalidate();
		}
	}

	/// <summary>
	/// The last resolution published by the scene identity index. Consumers must
	/// require an enabled editor-authored resolution before using the identity.
	/// </summary>
	public SceneIdentityResolution? RuntimeResolution { get; private set; }

	public SceneEntityId? Identity => Game.IsPlaying && !_runtimeIdentityApproved
		? null
		: RawIdentity;

	internal SceneEntityId? RawIdentity => PersistentId == Guid.Empty
		? null
		: new SceneEntityId( PersistentId );

	protected override void OnValidate()
	{
		base.OnValidate();
		InvalidateRuntimeApproval();
		PersistentSceneIdentityIndexSystem.Current?.Invalidate();
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( !Game.IsPlaying ) return;
		InvalidateRuntimeApproval();
		PersistentSceneIdentityIndexSystem.Current?.Invalidate();
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Game.IsPlaying ) return;
		InvalidateRuntimeApproval();
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
			if ( _disabledForIdentity )
			{
				_disabledForIdentity = false;
				GameObject.Enabled = true;
			}
			RuntimeResolution = resolution;
			_runtimeIdentityApproved = true;
			return;
		}
		_runtimeIdentityApproved = false;
		RuntimeResolution = resolution;
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

	private void InvalidateRuntimeApproval()
	{
		if ( !Game.IsPlaying ) return;
		_runtimeIdentityApproved = false;
		RuntimeResolution = null;
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
	private readonly SceneIdentityProvenanceClassifier _provenance = new();
	private bool _dirty = true;
	private bool _building;

	public PersistentSceneIdentityIndexSystem( Scene scene ) : base( scene )
	{
		_scene = scene;
		Listen( Stage.SceneLoaded, int.MinValue, CaptureAuthoredSnapshot,
			"Hexagon authored persistent scene identity snapshot" );
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
		if ( !_provenance.HasAuthoredSnapshot )
			return OperationResult<PersistentSceneIdentityIndex>.Failure(
				ErrorCode.ConfigurationInvalid,
				"Persistent scene identities cannot be published before the authored scene snapshot is captured." );
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
		if ( _building || !_provenance.HasAuthoredSnapshot ) return;
		_building = true;
		try
		{
			var entries = Collect();
			Index = PersistentSceneIdentityIndex.Build( entries.Select( entry => entry.Candidate ) );
			for ( var index = 0; index < entries.Count; index++ )
				entries[index].Component.ApplyRuntimeResolution( Index.Resolutions[index] );
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
			_provenance.CaptureAuthoredSnapshot( entries.Select( entry => entry.Component.Id ) );
			var repaired = SceneIdentityValidator.RepairForEditor(
				entries.Select( entry => entry.Candidate ), SceneEntityId.New );
			for ( var index = 0; index < repaired.Count; index++ )
			{
				var resolution = repaired[index];
				if ( resolution.Repaired && resolution.EffectiveId is not null )
					entries[index].Component.PersistentId = resolution.EffectiveId.Value.Value;
			}
			Index = PersistentSceneIdentityIndex.Build( entries.Select( entry =>
				new SceneIdentityCandidate(
					entry.Candidate.StablePath, entry.Component.RawIdentity, entry.Candidate.Provenance ) ) );
			_dirty = false;
		}
		finally
		{
			_building = false;
		}
	}

	private void CaptureAuthoredSnapshot()
	{
		_provenance.CaptureAuthoredSnapshot( EnumerateComponents().Select( component => component.Id ) );
		Index = null;
		_dirty = true;
	}

	private IEnumerable<PersistentSceneEntity> EnumerateComponents() => _scene
		.GetAllObjects( true )
		.SelectMany( gameObject => gameObject.GetComponents<PersistentSceneEntity>( true ) );

	private IReadOnlyList<IdentityEntry> Collect() => EnumerateComponents()
		.Select( component => new IdentityEntry(
			component,
			new SceneIdentityCandidate(
				PersistentSceneEntity.StablePath( component ),
				component.RawIdentity,
				Game.IsPlaying
					? _provenance.Classify( component.Id, component.GameObject.RootNetwork.Active )
					: SceneIdentityProvenance.EditorAuthored ) ) )
		.ToArray();

	private sealed record IdentityEntry(
		PersistentSceneEntity Component,
		SceneIdentityCandidate Candidate );
}
