#nullable enable

using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Architecture;

[TestClass]
public sealed class LayerBoundaryTests
{
	private static readonly Regex BlockComments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
	private static readonly Regex LineComments = new(@"//.*?$", RegexOptions.Multiline | RegexOptions.Compiled);
	private static readonly Regex SyncAttribute = new(@"\[\s*Sync(?<body>[^\]]*)\]", RegexOptions.Compiled);
	private static readonly Regex AuthorityApi = new(
		@"\bRpc\.|\[\s*Rpc\b|\[\s*Sync\b|\bSyncFlags\.|\bFileSystem\.Data\b",
		RegexOptions.Compiled);

	public TestContext TestContext { get; set; } = null!;

	[TestMethod]
	public void DomainAndApplicationAreSandboxIndependent()
	{
		var violations = ProductFiles("Domain", "Application")
			.Select(file => (File: file, Source: SourceWithoutComments(file)))
			.Where(value => ContainsAny(value.Source, "using Sandbox", "Sandbox.") ||
				AuthorityApi.IsMatch(value.Source))
			.Select(value => Relative(value.File))
			.ToArray();

		Assert.IsEmpty(violations,
			$"Domain/Application must remain Sandbox-independent: {string.Join(", ", violations)}");
	}

	[TestMethod]
	public void SandboxAndAuthorityApisAreRestrictedToRuntimeAndInfrastructure()
	{
		var violations = ProductFiles()
			.Select(file => (File: file, Source: SourceWithoutComments(file)))
			.Where(value => ContainsAny(value.Source, "using Sandbox", "Sandbox.") ||
				AuthorityApi.IsMatch(value.Source))
			.Where(value => !IsAllowedSandboxLayer(value.File))
			.Select(value => Relative(value.File))
			.ToArray();

		Assert.IsEmpty(violations,
			$"Sandbox/authority APIs are restricted to Runtime and Infrastructure: {string.Join(", ", violations)}");
	}

	[TestMethod]
	public void EveryV2SyncAttributeIsExplicitlyHostAuthored()
	{
		var violations = new List<string>();
		foreach (var file in ProductFiles())
		{
			var source = SourceWithoutComments(file);
			foreach (Match match in SyncAttribute.Matches(source))
			{
				if (!match.Groups["body"].Value.Contains("SyncFlags.FromHost", StringComparison.Ordinal))
					violations.Add($"{Relative(file)}: {match.Value}");
			}
		}

		Assert.IsEmpty(violations,
			$"Every v2 synchronized field must be host-authored: {string.Join(" | ", violations)}");
	}

	[TestMethod]
	public void ReplicatedPlayerBodyIsPresentationOnlyAndNeverWritesTheClientStore()
	{
		var playerBody = Path.Combine(V2Root(), "Runtime", "HexPlayerBody.cs");
		var source = SourceWithoutComments(playerBody);
		var forbidden = new[] { "HexClientStore", "ClientStore", "ApplyState(", "ReplacePlayer(" };
		var violations = forbidden.Where(value => source.Contains(value, StringComparison.Ordinal)).ToArray();

		Assert.IsEmpty(violations,
			$"Replicated player state must remain presentation-only: {string.Join(", ", violations)}");
	}

	[TestMethod]
	public void ClientLayerDoesNotReferenceServerAggregatesOrPersistence()
	{
		var forbidden = new[]
		{
			"Hexagon.V2.Persistence",
			"CharacterRecord",
			"InventoryRecord",
			"ItemRecord",
			"WorldItemRecord",
			"TypedPayload",
			"DocumentSnapshot",
			"IPersistenceProvider"
		};
		var violations = ProductFiles("Client")
			.Select(file => (File: file, Source: SourceWithoutComments(file)))
			.Where(value => ContainsAny(value.Source, forbidden))
			.Select(value => Relative(value.File))
			.ToArray();

		Assert.IsEmpty(violations,
			$"Client may depend only on snapshots, commands, IDs, and kernel results: {string.Join(", ", violations)}");
	}

	[TestMethod]
	public void SnapshotContractsExposeNoServerOnlyNamesOrTypes()
	{
		var forbiddenPropertyNames = new HashSet<string>(StringComparer.Ordinal)
		{
			"SteamId",
			"AccountId",
			"IsDirty",
			"SchemaState",
			"BanExpiresAt",
			"Traits",
			"RevisionToken"
		};
		var forbiddenTypeNames = new HashSet<string>(StringComparer.Ordinal)
		{
			"Hexagon.V2.Domain.CharacterRecord",
			"Hexagon.V2.Domain.InventoryRecord",
			"Hexagon.V2.Domain.ItemRecord",
			"Hexagon.V2.Domain.WorldItemRecord",
			"Hexagon.V2.Domain.TypedPayload"
		};
		var snapshotTypes = typeof(PlayerPublicSnapshot).Assembly.GetTypes()
			.Where(type => type.Namespace == "Hexagon.V2.Networking" &&
				(type.Name.EndsWith("Snapshot", StringComparison.Ordinal) || type == typeof(SnapshotValue)))
			.ToArray();
		var violations = new List<string>();
		foreach (var type in snapshotTypes)
		{
			foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
			{
				if (forbiddenPropertyNames.Contains(property.Name))
					violations.Add($"{type.Name}.{property.Name}");
				if (ContainsForbiddenType(property.PropertyType, forbiddenTypeNames))
					violations.Add($"{type.Name}.{property.Name}: {property.PropertyType.FullName}");
			}
		}

		Assert.IsNotEmpty(snapshotTypes);
		Assert.IsEmpty(violations,
			$"Snapshots expose server-only members: {string.Join(", ", violations)}");
	}

	[TestMethod]
	public void LegacyNamespacesAreReportedWithoutGatingV2()
	{
		var codeRoot = Path.Combine(ProductRoot(), "Code");
		var legacyFiles = Directory.EnumerateFiles(codeRoot, "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.StartsWith(Path.Combine(codeRoot, "V2") + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase))
			.Where(file => Regex.IsMatch(SourceWithoutComments(file), @"\bnamespace\s+Hexagon(?:\.|;)",
				RegexOptions.CultureInvariant))
			.Select(file => Path.GetRelativePath(ProductRoot(), file))
			.OrderBy(file => file, StringComparer.Ordinal)
			.ToArray();

		TestContext.WriteLine($"Legacy Hexagon namespace files (non-gating): {legacyFiles.Length}");
		foreach (var file in legacyFiles.Take(25))
			TestContext.WriteLine(file);
	}

	[TestMethod]
	public void HostConstructionOccursOnlyAfterConfigurationAndRecoveredDomainValidation()
	{
		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		var configuration = runtime.IndexOf( "configuration.InitializeAsync", StringComparison.Ordinal );
		var validation = runtime.IndexOf( "new DomainInvariantValidator", StringComparison.Ordinal );
		var hostConstruction = runtime.IndexOf( "descriptor.CreateHostApplication", StringComparison.Ordinal );

		Assert.IsGreaterThanOrEqualTo( 0, configuration );
		Assert.IsLessThan( configuration, validation );
		Assert.IsLessThan( hostConstruction, configuration );
	}

	private static bool ContainsForbiddenType(Type type, IReadOnlySet<string> forbidden)
	{
		if (type.FullName is not null && forbidden.Contains(type.FullName))
			return true;
		if (type.IsArray)
			return ContainsForbiddenType(type.GetElementType()!, forbidden);
		return type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsForbiddenType(argument, forbidden));
	}

	private static bool ContainsAny(string source, params string[] values) =>
		values.Any(value => source.Contains(value, StringComparison.Ordinal));

	private static bool IsAllowedSandboxLayer(string file)
	{
		var relative = Path.GetRelativePath(V2Root(), file);
		var separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
		var layer = separator < 0 ? relative : relative[..separator];
		return layer is "Runtime" or "Infrastructure";
	}

	private static IEnumerable<string> ProductFiles(params string[] layers)
	{
		var roots = layers.Length == 0
			? Directory.EnumerateDirectories(V2Root())
			: layers.Select(layer => Path.Combine(V2Root(), layer));
		return roots.Where(Directory.Exists)
			.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories));
	}

	private static string SourceWithoutComments(string file)
	{
		var source = File.ReadAllText(file);
		return LineComments.Replace(BlockComments.Replace(source, string.Empty), string.Empty);
	}

	private static string Relative(string file) => Path.GetRelativePath(ProductRoot(), file);
	private static string V2Root() => Path.Combine(ProductRoot(), "Code", "V2");

	private static string ProductRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (Directory.Exists(Path.Combine(current.FullName, "Code", "V2")))
				return current.FullName;
			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate the Hexagon product root from the test output directory.");
	}
}
