#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Hexagon.V2.Tests.Architecture;

/// <summary>
/// Inclusion-completeness guard for the engine-facing layers: every file under
/// Code/V2/Runtime and Code/V2/Infrastructure is either compiled into this test project
/// or sits on the reviewed exclusion manifest with a reason — never silently untested.
/// </summary>
[TestClass]
public sealed class RuntimeInclusionTests
{
	[TestMethod]
	public void EveryEngineFacingFileIsCompiledOrOnTheReviewedExclusionManifest()
	{
		var compiled = CompiledEngineFacingFiles();
		var manifest = ReadExclusionManifest();
		var disk = EngineFacingFilesOnDisk();

		var missing = disk
			.Where( file => !compiled.Contains( file ) && !manifest.ContainsKey( file ) )
			.OrderBy( file => file, StringComparer.Ordinal )
			.ToArray();
		Assert.IsEmpty( missing,
			"Engine-facing files must be compiled into the test project or reviewed onto " +
			$"tests/CompileExclusions.txt with a reason: {string.Join( ", ", missing )}" );

		var overlapping = manifest.Keys
			.Where( compiled.Contains )
			.OrderBy( file => file, StringComparer.Ordinal )
			.ToArray();
		Assert.IsEmpty( overlapping,
			$"Manifest entries are stale; these files are compiled now: {string.Join( ", ", overlapping )}" );

		var vanished = manifest.Keys
			.Where( file => !disk.Contains( file ) )
			.OrderBy( file => file, StringComparer.Ordinal )
			.ToArray();
		Assert.IsEmpty( vanished,
			$"Manifest entries name files that no longer exist: {string.Join( ", ", vanished )}" );

		foreach ( var entry in manifest )
			Assert.IsFalse( string.IsNullOrWhiteSpace( entry.Value ),
				$"Manifest entry '{entry.Key}' must carry a non-empty reason." );
	}

	private static HashSet<string> CompiledEngineFacingFiles()
	{
		var project = XDocument.Load( TestProjectPath() );
		return project.Descendants( "Compile" )
			.Select( item => item.Attribute( "Include" )?.Value )
			.Where( include => include is not null && include.StartsWith( "../Code/V2/", StringComparison.Ordinal ) )
			.Select( include => include!["../Code/V2/".Length..].Replace( '\\', '/' ) )
			.Where( IsEngineFacing )
			.ToHashSet( StringComparer.Ordinal );
	}

	private static SortedDictionary<string, string> ReadExclusionManifest()
	{
		var manifest = new SortedDictionary<string, string>( StringComparer.Ordinal );
		foreach ( var line in File.ReadAllLines( ManifestPath() ) )
		{
			var trimmed = line.Trim();
			if ( trimmed.Length == 0 || trimmed.StartsWith( "#", StringComparison.Ordinal ) ) continue;
			var separator = trimmed.IndexOf( '|', StringComparison.Ordinal );
			Assert.IsGreaterThan( 0, separator, $"Manifest line must be '<path> | <reason>': {trimmed}" );
			var path = trimmed[..separator].Trim().Replace( '\\', '/' );
			var reason = trimmed[(separator + 1)..].Trim();
			Assert.IsTrue( IsEngineFacing( path ),
				$"Manifest may list only Runtime/Infrastructure files: {path}" );
			Assert.IsTrue( manifest.TryAdd( path, reason ), $"Manifest lists '{path}' twice." );
		}
		Assert.IsNotEmpty( manifest, "The exclusion manifest parsed to zero entries." );
		return manifest;
	}

	private static HashSet<string> EngineFacingFilesOnDisk()
	{
		var root = V2Root();
		return new[] { "Runtime", "Infrastructure" }
			.Select( layer => Path.Combine( root, layer ) )
			.Where( Directory.Exists )
			.SelectMany( layer => Directory.EnumerateFiles( layer, "*.cs", SearchOption.AllDirectories ) )
			.Select( file => Path.GetRelativePath( root, file ).Replace( '\\', '/' ) )
			.ToHashSet( StringComparer.Ordinal );
	}

	private static bool IsEngineFacing( string relativePath ) =>
		relativePath.StartsWith( "Runtime/", StringComparison.Ordinal ) ||
		relativePath.StartsWith( "Infrastructure/", StringComparison.Ordinal );

	private static string TestProjectPath() =>
		Path.Combine( TestsRoot(), "Hexagon.V2.Tests.csproj" );

	private static string ManifestPath() =>
		Path.Combine( TestsRoot(), "CompileExclusions.txt" );

	private static string V2Root() => Path.Combine( ProductRoot(), "Code", "V2" );
	private static string TestsRoot() => Path.Combine( ProductRoot(), "tests" );

	private static string ProductRoot()
	{
		var current = new DirectoryInfo( AppContext.BaseDirectory );
		while ( current is not null )
		{
			if ( Directory.Exists( Path.Combine( current.FullName, "Code", "V2" ) ) )
				return current.FullName;
			current = current.Parent;
		}

		throw new DirectoryNotFoundException( "Could not locate the Hexagon product root from the test output directory." );
	}
}
