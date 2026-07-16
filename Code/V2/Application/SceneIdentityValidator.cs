#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

public enum SceneIdentityProvenance
{
	EditorAuthored = 0,
	RuntimeOrNetwork = 1
}

public sealed record SceneIdentityCandidate(
	string StablePath,
	SceneEntityId? Id,
	SceneIdentityProvenance Provenance = SceneIdentityProvenance.EditorAuthored );

public sealed record SceneIdentityResolution(
	string StablePath,
	SceneEntityId? OriginalId,
	SceneEntityId? EffectiveId,
	SceneIdentityProvenance Provenance,
	bool Enabled,
	bool Repaired,
	string? FatalDiagnostic );

/// <summary>
/// Classifies persistent identity components against the exact component IDs
/// present when the authored scene finished loading. Anything admitted later,
/// or moved below an active network root, is runtime state and fails closed.
/// </summary>
public sealed class SceneIdentityProvenanceClassifier
{
	private HashSet<Guid>? _authoredComponentIds;

	public bool HasAuthoredSnapshot => _authoredComponentIds is not null;
	public int AuthoredComponentCount => _authoredComponentIds?.Count ?? 0;

	public void CaptureAuthoredSnapshot( IEnumerable<Guid> componentIds )
	{
		ArgumentNullException.ThrowIfNull( componentIds );
		var captured = new HashSet<Guid>();
		foreach ( var componentId in componentIds )
		{
			if ( componentId == Guid.Empty )
				throw new ArgumentException(
					"Authored scene component IDs cannot be empty.", nameof(componentIds) );
			captured.Add( componentId );
		}
		_authoredComponentIds = captured;
	}

	public SceneIdentityProvenance Classify( Guid componentId, bool hasActiveNetworkRoot )
	{
		if ( componentId == Guid.Empty || hasActiveNetworkRoot ||
			_authoredComponentIds is null || !_authoredComponentIds.Contains( componentId ) )
			return SceneIdentityProvenance.RuntimeOrNetwork;
		return SceneIdentityProvenance.EditorAuthored;
	}
}

/// <summary>
/// Editor validation repairs missing/duplicate identities. Runtime validation is
/// deliberately non-mutating and disables every ambiguous entity.
/// </summary>
public static class SceneIdentityValidator
{
	public static IReadOnlyList<SceneIdentityResolution> RepairForEditor(
		IEnumerable<SceneIdentityCandidate> candidates,
		Func<SceneEntityId> createId )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		ArgumentNullException.ThrowIfNull( createId );
		var used = new HashSet<SceneEntityId>();
		var result = new List<SceneIdentityResolution>();
		foreach ( var candidate in candidates )
		{
			var effective = candidate.Id;
			var repaired = effective is null || !used.Add( effective.Value );
			if ( repaired )
			{
				do effective = createId(); while ( !used.Add( effective.Value ) );
			}

			result.Add( new SceneIdentityResolution(
				candidate.StablePath, candidate.Id, effective, candidate.Provenance,
				true, repaired, null ) );
		}

		return result;
	}

	public static IReadOnlyList<SceneIdentityResolution> ValidateRuntime(
		IEnumerable<SceneIdentityCandidate> candidates )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		var materialized = candidates.ToArray();
		var duplicates = materialized
			.Where( candidate => candidate.Provenance == SceneIdentityProvenance.EditorAuthored &&
				candidate.Id is not null )
			.GroupBy( candidate => candidate.Id!.Value )
			.Where( group => group.Count() > 1 )
			.Select( group => group.Key )
			.ToHashSet();

		return materialized.Select( candidate =>
		{
			if ( candidate.Provenance != SceneIdentityProvenance.EditorAuthored )
				return new SceneIdentityResolution(
					candidate.StablePath, candidate.Id, null, candidate.Provenance,
					false, false,
					$"Persistent scene entity '{candidate.StablePath}' originated from a runtime/network root; entity is disabled." );
			if ( candidate.Id is null )
				return new SceneIdentityResolution(
					candidate.StablePath, null, null, candidate.Provenance,
					false, false,
					$"Persistent scene entity '{candidate.StablePath}' has no editor-authored ID." );
			if ( duplicates.Contains( candidate.Id.Value ) )
				return new SceneIdentityResolution(
					candidate.StablePath, candidate.Id, candidate.Id, candidate.Provenance,
					false, false,
					$"Persistent scene entity ID '{candidate.Id}' is duplicated; entity is disabled." );
			return new SceneIdentityResolution(
				candidate.StablePath, candidate.Id, candidate.Id, candidate.Provenance,
				true, false, null );
		}).ToArray();
	}
}
