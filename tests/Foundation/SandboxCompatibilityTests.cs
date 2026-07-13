using System.IO;

namespace Hexagon.V2.Tests.Foundation;

[TestClass]
public sealed class SandboxCompatibilityTests
{
	private static readonly string[] ForbiddenSourceTokens =
	[
		".ConfigureAwait(",
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
		"TaskScheduler"
	];

	[TestMethod]
	public void SandboxIndependentV2CodeAvoidsKnownWhitelistViolations()
	{
		var root = FindHexagonRoot();
		var sourceRoots = new[]
		{
			Path.Combine( root, "Code", "V2" ),
			Path.GetFullPath( Path.Combine( root, "..", "hl2rp-hexagon", "Code" ) )
		}.Where( Directory.Exists ).ToArray();

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
				@"finally\s*\{[^{}]*\bawait\b",
				System.Text.RegularExpressions.RegexOptions.Singleline ) )
				violations.Add(
					$"{Path.GetRelativePath( root, path )} awaits inside a finally block (lowers to forbidden ExceptionDispatchInfo)" );
			var lines = File.ReadAllLines( path );
			for ( var index = 0; index < lines.Length; index++ )
			{
				foreach ( var token in ForbiddenSourceTokens )
				{
					if ( lines[index].Contains( token, StringComparison.Ordinal ) )
						violations.Add( $"{Path.GetRelativePath( root, path )}:{index + 1} contains '{token}'" );
				}
			}
		}

		Assert.HasCount(
			0,
			violations,
			"Known s&box whitelist violations were found:" + Environment.NewLine + string.Join( Environment.NewLine, violations ) );
	}

	private static string FindHexagonRoot()
	{
		var directory = new DirectoryInfo( AppContext.BaseDirectory );
		while ( directory is not null && !File.Exists( Path.Combine( directory.FullName, "hexagon.sbproj" ) ) )
			directory = directory.Parent;

		Assert.IsNotNull( directory, "Could not locate the Hexagon repository root." );
		return directory.FullName;
	}
}
