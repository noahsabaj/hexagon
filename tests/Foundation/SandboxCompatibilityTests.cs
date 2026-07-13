using System.IO;

namespace Hexagon.V2.Tests.Foundation;

[TestClass]
public sealed class SandboxCompatibilityTests
{
	private static readonly string[] ForbiddenSourceTokens =
	[
		".ConfigureAwait(",
		"Volatile.",
		"ReaderWriterLockSlim",
		"CryptographicOperations.FixedTimeEquals",
		".IsInterface",
		".IsByRef",
		".IsPointer",
		".IsInstanceOfType",
		"ExceptionDispatchInfo",
		"System.Reflection",
		"RuntimeHelpers.",
		"Activator.CreateInstance",
		".GetProperties(",
		".GetFields(",
		".MakeGenericType(",
		".GetGenericArguments(",
		"await using",
		"Task.WhenAll(",
		"TaskScheduler",
		"Environment.ProcessId"
	];

	private static readonly (string Name, string Pattern)[] ForbiddenRawIoSignatures =
	[
		("File static API", @"\bFile\s*\."),
		("Directory static API", @"\bDirectory\s*\."),
		("Path.GetFullPath", @"\bPath\s*\.\s*GetFullPath\s*\("),
		("FileStream", @"\bFileStream\b"),
		("FileInfo", @"\bFileInfo\b"),
		("DirectoryInfo", @"\bDirectoryInfo\b"),
		("FileMode", @"\bFileMode\b"),
		("FileAccess", @"\bFileAccess\b"),
		("FileShare", @"\bFileShare\b"),
		("FileOptions", @"\bFileOptions\b"),
		("SearchOption", @"\bSearchOption\b")
	];

	[TestMethod]
	public void SandboxIndependentV2CodeAvoidsKnownWhitelistViolations()
	{
		var root = FindHexagonRoot();
		var sourceRoots = new[] { Path.Combine( root, "Code", "V2" ) }
			.Where( Directory.Exists )
			.ToArray();

		var violations = new List<string>();
		foreach ( var path in sourceRoots.SelectMany( sourceRoot =>
			Directory.GetFiles( sourceRoot, "*.cs", SearchOption.AllDirectories ) )
			.Where( path => !path.Contains(
				$"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
				StringComparison.OrdinalIgnoreCase ) ) )
		{
			var source = File.ReadAllText( path );
			if ( System.Text.RegularExpressions.Regex.IsMatch(
				source,
				@"^\s*(?:private|protected|internal|public)\s+(?:static\s+)?volatile\s+",
				System.Text.RegularExpressions.RegexOptions.Multiline ) )
				violations.Add(
					$"{Path.GetRelativePath( root, path )} declares a volatile field (lowers to forbidden IsVolatile)" );
			if ( System.Text.RegularExpressions.Regex.IsMatch(
				source,
				@"finally\s*\{[^{}]*\bawait\b",
				System.Text.RegularExpressions.RegexOptions.Singleline ) )
				violations.Add(
					$"{Path.GetRelativePath( root, path )} awaits inside a finally block (lowers to forbidden ExceptionDispatchInfo)" );
			if ( System.Text.RegularExpressions.Regex.IsMatch(
				source,
				@"catch(?:\s*\([^)]*\))?\s*\{[^{}]*\bawait\b[^{}]*\bthrow\s*;",
				System.Text.RegularExpressions.RegexOptions.Singleline ) )
				violations.Add(
					$"{Path.GetRelativePath( root, path )} awaits before a bare catch rethrow (lowers to forbidden ExceptionDispatchInfo)" );
			var lines = File.ReadAllLines( path );
			for ( var index = 0; index < lines.Length; index++ )
			{
				foreach ( var token in ForbiddenSourceTokens )
				{
					if ( lines[index].Contains( token, StringComparison.Ordinal ) )
						violations.Add( $"{Path.GetRelativePath( root, path )}:{index + 1} contains '{token}'" );
				}

				foreach ( var signature in FindForbiddenRawIoSignatures( lines[index] ) )
					violations.Add(
						$"{Path.GetRelativePath( root, path )}:{index + 1} uses forbidden raw I/O signature '{signature}'" );
			}
		}

		Assert.HasCount(
			0,
			violations,
			"Known s&box whitelist violations were found:" + Environment.NewLine + string.Join( Environment.NewLine, violations ) );
	}

	[TestMethod]
	public void RawIoSignatureGuardCoversOriginalSb1000SurfaceWithoutFalsePositives()
	{
		// Keep fixtures split so the production-source scanner can never match this test file if its roots expand.
		var separator = string.Empty;
		var forbiddenFixtures = new[]
		{
			"File" + separator + ".Exists( path )",
			"Path" + separator + ".GetFullPath( path )",
			"File" + separator + "Stream? lease",
			"File" + separator + "Info info",
			"Directory" + separator + "Info directory",
			"File" + separator + "Mode.OpenOrCreate",
			"File" + separator + "Access.ReadWrite",
			"File" + separator + "Share.None",
			"File" + separator + "Options.WriteThrough",
			"Search" + separator + "Option.AllDirectories",
			"Directory" + separator + " . EnumerateFiles( root )"
		};

		foreach ( var fixture in forbiddenFixtures )
			Assert.IsNotEmpty(
				FindForbiddenRawIoSignatures( fixture ),
				$"Expected raw I/O fixture to be rejected: {fixture}" );

		var allowedFixtures = new[]
		{
			"var path = issue.Path;",
			"var fileName = record.FileName;",
			"IReadOnlyList<string> directories",
			"storage.ExistsAsync( path, cancellationToken )"
		};

		foreach ( var fixture in allowedFixtures )
			Assert.IsEmpty(
				FindForbiddenRawIoSignatures( fixture ),
				$"Expected non-I/O fixture to remain allowed: {fixture}" );
	}

	private static string[] FindForbiddenRawIoSignatures( string sourceLine ) => ForbiddenRawIoSignatures
		.Where( signature => System.Text.RegularExpressions.Regex.IsMatch(
			sourceLine,
			signature.Pattern,
			System.Text.RegularExpressions.RegexOptions.CultureInvariant ) )
		.Select( signature => signature.Name )
		.ToArray();

	private static string FindHexagonRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hexagon.sbproj" ) ) )
			directory = directory.Parent;

		Assert.IsNotNull( directory, "Could not locate the Hexagon repository root." );
		return directory.FullName;
	}
}
