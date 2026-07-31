#nullable enable

using System;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Composition;

public sealed record HostBootstrapOptions(
	string PersistenceRootOverride,
	string VerificationProbe );

/// <summary>
/// Resolves the effective host bootstrap options from launch overrides and scene-authored
/// values. The verification probe is a live production flag that durably mutates whatever
/// store it runs against and shuts the host down, so the probe/root coupling is enforced
/// here as a product invariant: a probe without an isolated persistence root fails the
/// bootstrap closed instead of polluting production data.
/// </summary>
public static class HostBootstrapResolution
{
	public const int MaximumProbeLength = 128;

	public static OperationResult<HostBootstrapOptions> Resolve(
		string? overrideRoot,
		string? sceneRoot,
		string? overrideProbe,
		string? sceneProbe,
		Func<string, string> normalizeRoot )
	{
		ArgumentNullException.ThrowIfNull( normalizeRoot );
		try
		{
			var requestedRoot = string.IsNullOrWhiteSpace( overrideRoot ) ? sceneRoot : overrideRoot;
			var root = string.IsNullOrWhiteSpace( requestedRoot )
				? string.Empty
				: normalizeRoot( requestedRoot );
			var requestedProbe = string.IsNullOrWhiteSpace( overrideProbe ) ? sceneProbe : overrideProbe;
			var probe = (requestedProbe ?? string.Empty).Trim();
			if ( probe.Length > MaximumProbeLength ) probe = probe[..MaximumProbeLength];
			if ( probe.Length > 0 && root.Length == 0 )
				return OperationResult<HostBootstrapOptions>.Failure(
					ErrorCode.ConfigurationInvalid,
					"A verification probe requires an isolated persistence root; set the data-root " +
					"override alongside the probe so verification can never mutate production data." );
			return OperationResult<HostBootstrapOptions>.Success( new HostBootstrapOptions( root, probe ) );
		}
		catch ( Exception exception )
		{
			return OperationResult<HostBootstrapOptions>.Failure( ErrorCode.ConfigurationInvalid, exception.Message );
		}
	}
}
