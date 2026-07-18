using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hexagon.V2.Tests.Foundation;

[TestClass]
public sealed class PackageLayoutTests
{
	private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
	private static readonly Regex NonCodeSource = new(
		"\"{3,}.*?\"{3,}|//[^\\r\\n]*|/\\*.*?\\*/|@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'",
		RegexOptions.Singleline | RegexOptions.CultureInvariant );
	private static readonly (string Name, string Pattern)[] ClientArchiveForbiddenSignatures =
	[
		("raw File static API", @"\bFile\s*\."),
		("raw Directory static API", @"\bDirectory\s*\."),
		("FileStream", @"\bFileStream\b"),
		("SafeFileHandle", @"\bSafeFileHandle\b"),
		("Environment.ProcessId", @"\bEnvironment\s*\.\s*ProcessId\b"),
		("Volatile API", @"\bVolatile\s*\."),
		("volatile field modifier", @"\bvolatile\s+")
	];

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
	public void TrackedUserProjectFileStaysAnEmptyInjectionPoint()
	{
		// MSBuild auto-imports Code/*.csproj.user into the generated project at the
		// targets stage, where its properties override the generated ones. The file is
		// deliberately tracked (untracking would hide it while still imported); this
		// gate makes any build-affecting content a loud failure instead of a dormant
		// build-behavior injection point.
		AssertEmptyUserProjectFile( Path.Combine(
			RepositoryRoots.FindHexagon(), "Code", "hexagon.csproj.user" ) );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void GameHasNoTrackedUserProjectFilesOrOnlyEmptyOnes()
	{
		var codeRoot = Path.Combine( RepositoryRoots.FindPair().Hl2Rp, "Code" );
		foreach ( var userFile in Directory.GetFiles( codeRoot, "*.csproj.user", SearchOption.AllDirectories ) )
			AssertEmptyUserProjectFile( userFile );
	}

	private static void AssertEmptyUserProjectFile( string path )
	{
		Assert.IsTrue( File.Exists( path ), $"Expected tracked user project file: {path}" );
		var document = System.Xml.Linq.XDocument.Load( path );
		var ns = document.Root!.Name.Namespace;
		Assert.IsEmpty( document.Root.Elements( ns + "Import" ).ToArray(),
			$"{path} must not import anything." );
		Assert.IsEmpty( document.Root.Elements( ns + "Target" ).ToArray(),
			$"{path} must not declare targets." );
		foreach ( var propertyGroup in document.Root.Elements( ns + "PropertyGroup" ) )
			Assert.IsEmpty( propertyGroup.Elements().ToArray(),
				$"{path} must not set MSBuild properties." );
		var unexpected = document.Root.Elements()
			.Where( element => element.Name.LocalName is not "PropertyGroup" )
			.Select( element => element.Name.LocalName )
			.ToArray();
		Assert.IsEmpty( unexpected,
			$"{path} contains unexpected elements: {string.Join( ", ", unexpected )}" );
	}

	[TestMethod]
	public void DependencySourcesArePinnedWithAuditSources()
	{
		AssertPinnedNuGetConfiguration( Path.Combine( RepositoryRoots.FindHexagon(), "NuGet.config" ) );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void GameDependencySourcesArePinnedWithAuditSources()
	{
		AssertPinnedNuGetConfiguration( Path.Combine( RepositoryRoots.FindPair().Hl2Rp, "NuGet.config" ) );
	}

	private static void AssertPinnedNuGetConfiguration( string path )
	{
		// The <auditSources> declaration is what makes the NuGet vulnerability gates
		// fail closed: without it, a reachable source lacking vulnerability data makes
		// both the audited restore and the package-list gate silently vacuous.
		Assert.IsTrue( File.Exists( path ), $"Pinned NuGet.config is missing: {path}" );
		var document = System.Xml.Linq.XDocument.Load( path );
		foreach ( var section in new[] { "packageSources", "auditSources" } )
		{
			var element = document.Root!.Element( section );
			Assert.IsNotNull( element, $"NuGet.config must declare <{section}>." );
			Assert.IsNotNull( element!.Element( "clear" ), $"<{section}> must clear inherited sources." );
			var sources = element.Elements( "add" ).ToArray();
			Assert.HasCount( 1, sources, $"<{section}> must pin exactly one source." );
			Assert.AreEqual(
				"https://api.nuget.org/v3/index.json",
				sources[0].Attribute( "value" )?.Value,
				$"<{section}> must pin api.nuget.org." );
		}
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void GameOwnsAResolvableStartupScene()
	{
		var roots = RepositoryRoots.FindPair();
		using var manifest = JsonDocument.Parse( File.ReadAllText( Path.Combine( roots.Hl2Rp, "hl2rp.sbproj" ) ) );

		Assert.AreEqual( "game", manifest.RootElement.GetProperty( "Type" ).GetString() );
		Assert.IsFalse(
			manifest.RootElement.GetProperty( "IsStandaloneOnly" ).GetBoolean(),
			"Publication to sbox.game requires non-standalone compilation; the engine silently skips Publish() for standalone-only projects." );
		Assert.IsFalse(
			manifest.RootElement.TryGetProperty( "IsWhitelistDisabled", out _ ),
			"Whitelist compilation is selected by Metadata.Compiler.Whitelist." );
		Assert.IsTrue(
			manifest.RootElement.GetProperty( "Metadata" ).GetProperty( "Compiler" )
				.GetProperty( "Whitelist" ).GetBoolean(),
			"Every streamed assembly is access-controlled on every host, so the compile-time whitelist gate must stay on." );
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
	public void GameUsesOneWhitelistSafeStoragePathForEveryHost()
	{
		var roots = RepositoryRoots.FindPair();
		var runtime = Path.Combine( roots.Hl2Rp, "Code", "Runtime" );
		var corePath = Path.Combine( runtime, "HL2RPDurableStorageCore.cs" );
		var factoryPath = Path.Combine( runtime, "HL2RPPersistenceStorageFactory.cs" );

		foreach ( var retired in new[]
		{
			"HL2RPPhysicalPersistenceStorage.cs",
			"HL2RPPhysicalPersistenceStorage.Server.cs",
			"HL2RPPhysicalPersistenceStorageCore.Server.cs",
			"HL2RPSandboxPersistenceStorage.cs"
		} )
			Assert.IsFalse(
				File.Exists( Path.Combine( runtime, retired ) ),
				$"Raw operating-system persistence was retired for whitelist-safe storage; '{retired}' must not return." );

		Assert.IsTrue(
			File.Exists( corePath ),
			"The durable storage protocol must live in the engine-neutral, test-compiled core." );
		Assert.IsTrue(
			File.Exists( factoryPath ),
			"Storage binding to the engine filesystem must remain explicit in the factory." );

		var core = File.ReadAllText( corePath );
		StringAssert.Contains( core, "class HL2RPDurableStorageCore" );
		StringAssert.Contains( core, "interface IHL2RPStorageFileSystem" );
		StringAssert.Contains( core, ": IPersistenceStorage" );

		// The single-writer evidence is anchored to the lease-acquisition block: the
		// held OpenOrCreate stream is what excludes a second writer, so a whole-file
		// scan would keep passing if only the lease site were weakened.
		var leaseAcquire = core.IndexOf(
			"public ValueTask<IPersistenceLease> AcquireExclusiveLeaseAsync(", StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, leaseAcquire );
		var leaseBlockEnd = core.IndexOf(
			"public ValueTask<bool> ExistsAsync(", leaseAcquire, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, leaseBlockEnd );
		var leaseBlock = core[leaseAcquire..leaseBlockEnd];
		StringAssert.Contains( leaseBlock, "FileMode.OpenOrCreate" );
		StringAssert.Contains( leaseBlock, "HL2RPRetryablePersistenceLease<Stream>" );
		StringAssert.Contains( leaseBlock, "PersistenceLeaseUnavailableException" );

		// Immutable publication evidence is anchored the same way: stage, verify,
		// then CreateNew-copy to the final name.
		var publish = core.IndexOf(
			"public ValueTask<bool> TryWriteImmutableAsync(", StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, publish );
		var publishEnd = core.IndexOf( "private byte[] ReadAllBytes(", publish, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, publishEnd );
		var publishBlock = core[publish..publishEnd];
		StringAssert.Contains( publishBlock, ".staging" );
		StringAssert.Contains( publishBlock, "SequenceEqual" );
		StringAssert.Contains( publishBlock, "FileMode.CreateNew" );

		var factory = File.ReadAllText( factoryPath );
		StringAssert.Contains( factory, "new HL2RPDurableStorageCore( new SandboxStorageFileSystem( fileSystem ) )" );
		StringAssert.Contains( factory, ": IHL2RPStorageFileSystem" );
		Assert.IsFalse(
			factory.Contains( "#if", StringComparison.Ordinal ),
			"Storage no longer forks on a compilation boundary; every host uses the whitelist-safe path." );

		var schemaSource = File.ReadAllText( Path.Combine( runtime, "HL2RPSchemaSourceSystem.cs" ) );
		StringAssert.Contains( schemaSource, "HL2RPPersistenceStorageFactory.Create" );
		Assert.IsFalse(
			schemaSource.Contains( "new HL2RPDurableStorageCore", StringComparison.Ordinal ),
			"The schema source must bind storage through the factory, not construct the core directly." );
	}

	[TestMethod]
	[TestCategory( "CrossRepository" )]
	public void ClientVisibleGameSourcesAvoidConfirmedAssemblyAccessControlViolations()
	{
		var roots = RepositoryRoots.FindPair();
		var codeRoot = Path.Combine( roots.Hl2Rp, "Code" );
		var violations = new List<string>();

		foreach ( var path in Directory.GetFiles( codeRoot, "*.cs", SearchOption.AllDirectories )
			.Where( IsClientVisibleSource ) )
		{
			var source = MaskNonCodeSource( File.ReadAllText( path ) );
			var relativePath = Path.GetRelativePath( roots.Hl2Rp, path );
			foreach ( var signature in ClientArchiveForbiddenSignatures )
			{
				foreach ( Match match in Regex.Matches(
					source,
					signature.Pattern,
					RegexOptions.CultureInvariant ) )
				{
					violations.Add(
						$"{relativePath}:{GetLineNumber( source, match.Index )} contains {signature.Name}" );
				}
			}

			foreach ( var index in FindAwaitInFinallyBlocks( source ) )
				violations.Add(
					$"{relativePath}:{GetLineNumber( source, index )} awaits in a finally block" );
		}

		Assert.HasCount(
			0,
			violations,
			"Client-visible HL2RP source contains constructs that make s&box reject the streamed assembly:" +
			Environment.NewLine + string.Join( Environment.NewLine, violations ) );
	}

	[TestMethod]
	public void FullVerifierExecutesTheClientEquivalentAssemblyAccessGate()
	{
		var hexagon = RepositoryRoots.FindHexagon();
		var verifier = File.ReadAllText( Path.Combine( hexagon, "tools", "verify.ps1" ) );
		var clientAccess = File.ReadAllText( Path.Combine(
			hexagon, "tools", "verify-client-assembly-access.ps1" ) );

		StringAssert.Contains( verifier, "verify-client-assembly-access.ps1" );
		StringAssert.Contains( verifier, "PowerShell 7 (pwsh) is required" );
		foreach ( var marker in new[]
		{
			"DefaultItemExcludesInProjectFolder=**/*.Server.cs",
			"GenerateAssemblyInfo=false",
			"GenerateTargetFrameworkAttribute=false",
			"Sandbox.AccessControl",
			"VerifyAssembly",
			"package.$($schemaManifest.Org).$($schemaManifest.Ident)"
		} )
			StringAssert.Contains( clientAccess, marker );
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
			"hexagon-v2-manual-remote-acceptance/4",
			"manual_operator_attestation",
			"hexagon_sha",
			"hl2rp_sha",
			"source_fingerprint",
			"runtime_artifact_ids",
			"hexagon-v2-runtime-environment/2",
			"runtime.sbox.distribution",
			"runtime.sbox.engine_source_sha",
			"runtime.source.effective_input_fingerprint",
			"runtime_config_sha256",
			"engine/runtime fingerprint",
			"artifacts are unreferenced",
			"artifact_ids",
			"pre_shutdown",
			"post_restart",
			"HL2RP_RECOVERY_SNAPSHOT",
			"Pre-shutdown and post-restart recovery sequence/digest must match exactly.",
			"operator.statement",
			"does not execute or independently prove"
		} )
			StringAssert.Contains( verifier, marker );

		Assert.IsFalse( verifier.Contains( "HEXAGON_REMOTE_ASSERT", StringComparison.Ordinal ),
			"A text sentinel must not be presented as executable remote acceptance." );
		StringAssert.Contains( runbook, "Manual dedicated-server two-client runbook" );
		StringAssert.Contains( runbook, "outside both Git worktrees" );
		StringAssert.Contains( runbook, "HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown" );
		StringAssert.Contains( runbook, "HL2RP_RECOVERY_SNAPSHOT phase=post_restart" );
		StringAssert.Contains( runbook, "does **not** execute or independently prove" );
	}

	[TestMethod]
	public void ReleaseEvidencePublisherIsEffectiveInputBoundAndFailureCompensating()
	{
		var hexagon = RepositoryRoots.FindHexagon();
		var publisher = File.ReadAllText( Path.Combine(
			hexagon, "tools", "publish-release-evidence.ps1" ) );
		var inputPolicy = File.ReadAllText( Path.Combine(
			hexagon, "tools", "release-inputs.ps1" ) );
		var bundle = File.ReadAllText( Path.Combine(
			hexagon, "tools", "release-evidence-bundle.ps1" ) );

		foreach ( var marker in new[]
		{
			"Get-EffectiveReleaseInputs",
			"-RequireClean",
			"immutable-releases",
			"sbox-evidence-$runId",
			"-State pending",
			"-State success",
			"Publish-PairedFailure",
			"statusPublicationStarted",
			"matchingAssets[0].digest",
			"Get-ReleaseTagCommit",
			"target_url=$TargetUrl",
			"New-ReleaseEvidenceBundle"
		} )
			StringAssert.Contains( publisher, marker );

		StringAssert.Contains( inputPolicy, "Ignored release material exists" );
		StringAssert.Contains( inputPolicy, "Assert-GeneratedCompileInputsTracked" );
		StringAssert.Contains( inputPolicy, "Add-ReleaseInputFileBytes" );
		StringAssert.Contains( inputPolicy, "Code/Properties/launchSettings.json" );
		StringAssert.Contains( bundle, "hexagon-v2-release-evidence-bundle/1" );
		StringAssert.Contains( bundle, "1980, 1, 1" );
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

	private static bool IsClientVisibleSource( string path ) =>
		!path.EndsWith( ".Server.cs", StringComparison.OrdinalIgnoreCase ) &&
		!path.Split( Path.DirectorySeparatorChar )
			.Contains( "obj", StringComparer.OrdinalIgnoreCase );

	private static string MaskNonCodeSource( string source ) => NonCodeSource.Replace(
		source,
		static match => new string(
			match.Value.Select( character => character is '\r' or '\n' ? character : ' ' ).ToArray() ) );

	private static IEnumerable<int> FindAwaitInFinallyBlocks( string source )
	{
		foreach ( Match finallyMatch in Regex.Matches(
			source,
			@"\bfinally\b",
			RegexOptions.CultureInvariant ) )
		{
			var openBrace = source.IndexOf( '{', finallyMatch.Index + finallyMatch.Length );
			if ( openBrace < 0 ) continue;

			var depth = 0;
			for ( var index = openBrace; index < source.Length; index++ )
			{
				if ( source[index] == '{' ) depth++;
				else if ( source[index] == '}' && --depth == 0 )
				{
					var block = source.Substring( openBrace + 1, index - openBrace - 1 );
					foreach ( Match awaitMatch in Regex.Matches(
						block,
						@"\bawait\b",
						RegexOptions.CultureInvariant ) )
						yield return openBrace + 1 + awaitMatch.Index;
					break;
				}
			}
		}
	}

	private static int GetLineNumber( string source, int index ) =>
		source.Take( index ).Count( character => character == '\n' ) + 1;

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
