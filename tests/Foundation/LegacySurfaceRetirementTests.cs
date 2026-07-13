using System.IO;

namespace Hexagon.V2.Tests.Foundation;

[TestClass]
public sealed class LegacySurfaceRetirementTests
{
	private static readonly string[] RetiredRuntimeIdentifiers =
	[
		"DatabaseManager",
		"CharVarAttribute",
		"PermissionManager",
		"CharacterManager",
		"InventoryManager",
		"ItemManager",
		"HexCharacter",
		"HexInventory",
		"ItemInstance",
		"HexagonConfigComponent"
	];

	[TestMethod]
	public void RuntimeCodeContainsOnlyV2AndTheAssemblyImports()
	{
		var root = FindRoot();
		var codeRoot = Path.Combine(root, "Code");
		var unexpected = Directory.GetFiles(codeRoot, "*", SearchOption.AllDirectories)
			.Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".scss")
			.Where(path => !Path.GetRelativePath(codeRoot, path).Replace('\\', '/').StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
			.Where(path => !Path.GetRelativePath(codeRoot, path).Replace('\\', '/').StartsWith("V2/", StringComparison.Ordinal)
				&& !string.Equals(Path.GetFileName(path), "Assembly.cs", StringComparison.Ordinal)
				&& !string.Equals(Path.GetFileName(path), "hexagon.csproj.user", StringComparison.Ordinal))
			.Select(path => Path.GetRelativePath(root, path))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();

		Assert.IsEmpty(unexpected, $"Legacy runtime files remain: {string.Join(", ", unexpected)}");
	}

	[TestMethod]
	public void GeneratedAndResearchEraDocumentationIsRetired()
	{
		var root = FindRoot();
		foreach (var relative in new[]
		{
			"llms.txt",
			"llms-full.txt",
			"tools/llms-gen.ps1",
			"tools/docgen.ps1",
			"tools/wiki-sync.ps1",
			".github/workflows/wiki-sync.yml",
			"Editor/Assembly.cs",
			"Editor/MyEditorMenu.cs",
			"docs/research-helix.md",
			"docs/research-nutscript.md",
			"docs/schema-guide.md",
			"docs/api-reference.md"
		})
			Assert.IsFalse(File.Exists(Path.Combine(root, relative)), $"Obsolete artifact remains: {relative}");
	}

	[TestMethod]
	public void V2SourceDoesNotRetainRetiredRuntimeIdentifiers()
	{
		var root = FindRoot();
		var v2Root = Path.Combine(root, "Code", "V2");
		var violations = Directory.GetFiles(v2Root, "*.cs", SearchOption.AllDirectories)
			.SelectMany(path => File.ReadLines(path)
				.Select((line, index) => (Path: path, Line: line, Number: index + 1)))
			.Where(value => RetiredRuntimeIdentifiers.Any(identifier =>
				value.Line.Contains(identifier, StringComparison.Ordinal)))
			.Select(value => $"{Path.GetRelativePath(root, value.Path)}:{value.Number}")
			.ToArray();

		Assert.IsEmpty(violations, $"Retired runtime API identifiers remain: {string.Join(", ", violations)}");
	}

	private static string FindRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "hexagon.sbproj")))
			directory = directory.Parent;
		Assert.IsNotNull(directory, "Could not locate Hexagon repository root.");
		return directory.FullName;
	}
}
