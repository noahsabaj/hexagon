#nullable enable

using System;
using System.Collections.Generic;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

/// <summary>
/// Immutable O(N) scene identity index. Missing IDs, duplicate IDs, and
/// duplicate stable paths are excluded from lookup and remain fail-closed.
/// </summary>
public sealed class PersistentSceneIdentityIndex
{
	private readonly IReadOnlyList<SceneIdentityResolution> _resolutions;
	private readonly Dictionary<string, SceneIdentityResolution> _byPath;
	private readonly Dictionary<SceneEntityId, SceneIdentityResolution> _byId;

	private PersistentSceneIdentityIndex(
		IReadOnlyList<SceneIdentityResolution> resolutions,
		Dictionary<string, SceneIdentityResolution> byPath,
		Dictionary<SceneEntityId, SceneIdentityResolution> byId )
	{
		_resolutions = resolutions;
		_byPath = byPath;
		_byId = byId;
	}

	public IReadOnlyList<SceneIdentityResolution> Resolutions => _resolutions;
	public int Count => _resolutions.Count;

	public bool TryResolvePath( string stablePath, out SceneIdentityResolution resolution ) =>
		_byPath.TryGetValue( stablePath, out resolution! );

	public bool TryResolveId( SceneEntityId id, out SceneIdentityResolution resolution ) =>
		_byId.TryGetValue( id, out resolution! );

	public static PersistentSceneIdentityIndex Build( IEnumerable<SceneIdentityCandidate> candidates )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		var materialized = new List<SceneIdentityCandidate>();
		var idCounts = new Dictionary<SceneEntityId, int>();
		var pathCounts = new Dictionary<string, int>( StringComparer.Ordinal );
		foreach ( var candidate in candidates )
		{
			if ( candidate is null ) throw new ArgumentException( "Scene identity candidates cannot contain null.", nameof(candidates) );
			if ( string.IsNullOrWhiteSpace( candidate.StablePath ) )
				throw new ArgumentException( "Scene identity stable paths cannot be blank.", nameof(candidates) );
			materialized.Add( candidate );
			pathCounts[candidate.StablePath] = pathCounts.GetValueOrDefault( candidate.StablePath ) + 1;
			if ( candidate.Id is not null ) idCounts[candidate.Id.Value] = idCounts.GetValueOrDefault( candidate.Id.Value ) + 1;
		}

		var resolutions = new SceneIdentityResolution[materialized.Count];
		var byPath = new Dictionary<string, SceneIdentityResolution>( StringComparer.Ordinal );
		var byId = new Dictionary<SceneEntityId, SceneIdentityResolution>();
		for ( var index = 0; index < materialized.Count; index++ )
		{
			var candidate = materialized[index];
			string? diagnostic = null;
			if ( pathCounts[candidate.StablePath] > 1 )
				diagnostic = $"Persistent scene path '{candidate.StablePath}' is duplicated; entity is disabled.";
			else if ( candidate.Id is null )
				diagnostic = $"Persistent scene entity '{candidate.StablePath}' has no editor-authored ID.";
			else if ( idCounts[candidate.Id.Value] > 1 )
				diagnostic = $"Persistent scene entity ID '{candidate.Id}' is duplicated; entity is disabled.";

			var resolution = new SceneIdentityResolution(
				candidate.StablePath,
				candidate.Id,
				candidate.Id,
				diagnostic is null,
				false,
				diagnostic );
			resolutions[index] = resolution;
			if ( diagnostic is not null ) continue;
			byPath.Add( candidate.StablePath, resolution );
			byId.Add( candidate.Id!.Value, resolution );
		}

		return new PersistentSceneIdentityIndex( resolutions, byPath, byId );
	}
}
