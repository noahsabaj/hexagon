#nullable enable

using System;
using System.IO;

namespace Hexagon.V2.Tests.Foundation;

/// <summary>
/// Locates the source roots from a test binary's location.
/// <para>
/// Tests that read the repository's own files — published documents pinned against the constants that
/// enforce them, package layout, forbidden-token sweeps — all need this walk. It lived in three
/// byte-identical private copies before, which is one copy per test that happened to need it and no
/// owner for the rule that the walk anchors on <c>hexagon.sbproj</c>.
/// </para>
/// </summary>
public sealed record RepositoryRoots( string Hexagon, string Hl2Rp )
{
	public static string FindHexagon()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hexagon.sbproj" ) ) )
			directory = directory.Parent;

		Assert.IsNotNull( directory, "Could not locate the Hexagon repository root." );
		return directory.FullName;
	}

	public static RepositoryRoots FindPair()
	{
		var hexagon = FindHexagon();
		var hl2rp = Path.GetFullPath( Path.Combine( hexagon, "..", "hl2rp-hexagon" ) );
		Assert.IsTrue( File.Exists( Path.Combine( hl2rp, "hl2rp.sbproj" ) ), "Could not locate the sibling HL2RP repository." );

		return new RepositoryRoots( hexagon, hl2rp );
	}
}
