using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hexagon.V2.Tests.Foundation;

[TestClass]
public sealed class PackageLayoutTests
{
	private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	[TestMethod]
	public void LibraryDoesNotOwnASceneOrStartupScene()
	{
		var hexagon = RepositoryRoots.FindHexagon();
		using var manifest = JsonDocument.Parse( File.ReadAllText( Path.Combine( hexagon, "hexagon.sbproj" ) ) );

		Assert.AreEqual( "library", manifest.RootElement.GetProperty( "Type" ).GetString() );
		Assert.IsFalse( manifest.RootElement.GetProperty( "IsStandaloneOnly" ).GetBoolean() );
		Assert.IsFalse(
			manifest.RootElement.TryGetProperty( "IsWhitelistDisabled", out _ ),
			"Game/library manifests must not rely on the obsolete top-level whitelist field." );
		Assert.IsTrue(
			manifest.RootElement.GetProperty( "Metadata" ).GetProperty( "Compiler" )
				.GetProperty( "Whitelist" ).GetBoolean(),
			"The reusable library must remain platform-whitelisted." );
		Assert.IsFalse(
			manifest.RootElement.GetProperty( "Metadata" ).TryGetProperty( "StartupScene", out _ ),
			"Library packages must not select a startup scene." );

		var scenes = Directory.Exists( Path.Combine( hexagon, "Assets" ) )
			? Directory.GetFiles( Path.Combine( hexagon, "Assets" ), "*.scene", SearchOption.AllDirectories )
			: [];
		Assert.HasCount( 0, scenes, "Library packages must not contribute game-owned scenes." );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void GameOwnsAResolvableStartupScene()
	{
		var roots = RepositoryRoots.FindPair();
		using var manifest = JsonDocument.Parse( File.ReadAllText( Path.Combine( roots.Hl2Rp, "hl2rp.sbproj" ) ) );

		Assert.AreEqual( "game", manifest.RootElement.GetProperty( "Type" ).GetString() );
		Assert.IsTrue( manifest.RootElement.GetProperty( "IsStandaloneOnly" ).GetBoolean() );
		Assert.IsFalse(
			manifest.RootElement.TryGetProperty( "IsWhitelistDisabled", out _ ),
			"Standalone game compilation is selected by IsStandaloneOnly and Metadata.Compiler.Whitelist." );
		Assert.IsFalse(
			manifest.RootElement.GetProperty( "Metadata" ).GetProperty( "Compiler" )
				.GetProperty( "Whitelist" ).GetBoolean(),
			"The game owns the production OS persistence adapter and must remain standalone-only." );
		var startupScene = manifest.RootElement.GetProperty( "Metadata" ).GetProperty( "StartupScene" ).GetString();
		var dedicatedScene = manifest.RootElement.GetProperty( "Metadata" ).GetProperty( "DedicatedServerStartupScene" ).GetString();
		Assert.IsFalse( string.IsNullOrWhiteSpace( startupScene ) );
		Assert.IsFalse( string.IsNullOrWhiteSpace( dedicatedScene ) );

		var scenePath = Path.Combine(
			roots.Hl2Rp,
			"Assets",
			startupScene!.Replace( '/', Path.DirectorySeparatorChar ) );
		Assert.IsTrue( File.Exists( scenePath ), $"Startup scene does not exist: {scenePath}" );
		var dedicatedPath = Path.Combine(
			roots.Hl2Rp,
			"Assets",
			dedicatedScene!.Replace( '/', Path.DirectorySeparatorChar ) );
		Assert.IsTrue( File.Exists( dedicatedPath ), $"Dedicated startup scene does not exist: {dedicatedPath}" );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void MountedAssetPathsDoNotCollide()
	{
		var roots = RepositoryRoots.FindPair();
		var libraryAssets = EnumerateAssets( roots.Hexagon );
		var gameAssets = EnumerateAssets( roots.Hl2Rp );
		var collisions = libraryAssets.Keys.Intersect( gameAssets.Keys, PathComparer ).Order().ToArray();

		Assert.HasCount(
			0,
			collisions,
			$"Mounted packages claim the same resource paths: {string.Join( ", ", collisions )}" );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void SceneAndObjectGuidsAreUnique()
	{
		var roots = RepositoryRoots.FindPair();
		var rootSceneIds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var scenePath in Directory.GetFiles( Path.Combine( roots.Hl2Rp, "Assets" ), "*.scene", SearchOption.AllDirectories ) )
		{
			using var scene = JsonDocument.Parse( File.ReadAllText( scenePath ) );
			var sceneId = scene.RootElement.GetProperty( "__guid" ).GetString();
			Assert.IsFalse( string.IsNullOrWhiteSpace( sceneId ), $"Scene has no root GUID: {scenePath}" );
			Assert.IsTrue( rootSceneIds.Add( sceneId! ), $"Duplicate scene GUID {sceneId}: {scenePath}" );

			var objectIds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			CollectGuids( scene.RootElement, objectIds, scenePath );
		}
	}

	[TestMethod]
	public void RemoteAcceptanceGateIsExplicitlyManualAndArtifactBound()
	{
		var hexagon = RepositoryRoots.FindHexagon();
		var verifier = File.ReadAllText( Path.Combine(
			hexagon, "tools", "verify-remote-acceptance.ps1" ) );
		var runbook = File.ReadAllText( Path.Combine(
			hexagon, "docs", "testing.md" ) );

		foreach ( var marker in new[]
		{
			"hexagon-v2-manual-remote-acceptance/2",
			"manual_operator_attestation",
			"hexagon_sha",
			"hl2rp_sha",
			"source_fingerprint",
			"ls-files --cached --others --exclude-standard",
			"artifact_ids",
			"operator.statement",
			"does not execute or independently prove"
		} )
			StringAssert.Contains( verifier, marker );

		Assert.IsFalse( verifier.Contains( "HEXAGON_REMOTE_ASSERT", StringComparison.Ordinal ),
			"A text sentinel must not be presented as executable remote acceptance." );
		StringAssert.Contains( runbook, "Manual dedicated-server two-client runbook" );
		StringAssert.Contains( runbook, "does **not** execute or independently prove" );
	}

	private static Dictionary<string, string> EnumerateAssets( string projectRoot )
	{
		var assetsRoot = Path.Combine( projectRoot, "Assets" );
		if ( !Directory.Exists( assetsRoot ) )
			return new Dictionary<string, string>( PathComparer );

		return Directory.GetFiles( assetsRoot, "*", SearchOption.AllDirectories )
			.ToDictionary(
				path => Path.GetRelativePath( assetsRoot, path ).Replace( '\\', '/' ),
				path => path,
				PathComparer );
	}

	private static void CollectGuids( JsonElement element, HashSet<string> ids, string scenePath )
	{
		if ( element.ValueKind == JsonValueKind.Object )
		{
			foreach ( var property in element.EnumerateObject() )
			{
				if ( property.NameEquals( "__guid" ) && property.Value.ValueKind == JsonValueKind.String )
				{
					var id = property.Value.GetString();
					Assert.IsFalse( string.IsNullOrWhiteSpace( id ), $"Empty GUID in {scenePath}" );
					Assert.IsTrue( ids.Add( id! ), $"Duplicate object/component GUID {id} in {scenePath}" );
				}

				CollectGuids( property.Value, ids, scenePath );
			}
		}
		else if ( element.ValueKind == JsonValueKind.Array )
		{
			foreach ( var child in element.EnumerateArray() )
				CollectGuids( child, ids, scenePath );
		}
	}

	private sealed record RepositoryRoots( string Hexagon, string Hl2Rp )
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
}
