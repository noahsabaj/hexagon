#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Domain;

namespace Hexagon.V2.Application;

public sealed record SceneIdentityCandidate( string StablePath, SceneEntityId? Id );

public sealed record SceneIdentityResolution(
	string StablePath,
	SceneEntityId? OriginalId,
	SceneEntityId? EffectiveId,
	bool Enabled,
	bool Repaired,
	string? FatalDiagnostic );

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
		foreach ( var candidate in candidates.OrderBy( value => value.StablePath, StringComparer.Ordinal ) )
		{
			var effective = candidate.Id;
			var repaired = effective is null || !used.Add( effective.Value );
			if ( repaired )
			{
				do effective = createId(); while ( !used.Add( effective.Value ) );
			}

			result.Add( new SceneIdentityResolution(
				candidate.StablePath, candidate.Id, effective, true, repaired, null ) );
		}

		return result;
	}

	public static IReadOnlyList<SceneIdentityResolution> ValidateRuntime(
		IEnumerable<SceneIdentityCandidate> candidates )
	{
		ArgumentNullException.ThrowIfNull( candidates );
		var materialized = candidates.ToArray();
		var duplicates = materialized
			.Where( candidate => candidate.Id is not null )
			.GroupBy( candidate => candidate.Id!.Value )
			.Where( group => group.Count() > 1 )
			.Select( group => group.Key )
			.ToHashSet();

		return materialized.Select( candidate =>
		{
			if ( candidate.Id is null )
				return new SceneIdentityResolution(
					candidate.StablePath, null, null, false, false,
					$"Persistent scene entity '{candidate.StablePath}' has no editor-authored ID." );
			if ( duplicates.Contains( candidate.Id.Value ) )
				return new SceneIdentityResolution(
					candidate.StablePath, candidate.Id, candidate.Id, false, false,
					$"Persistent scene entity ID '{candidate.Id}' is duplicated; entity is disabled." );
			return new SceneIdentityResolution(
				candidate.StablePath, candidate.Id, candidate.Id, true, false, null );
		}).ToArray();
	}
}
