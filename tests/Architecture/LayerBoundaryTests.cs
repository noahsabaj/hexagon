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
	public void AuthoritativeBodyLifecycleUsesAnIndependentDormantUnownedNetworkRoot()
	{
		var playerBody = Path.Combine( V2Root(), "Runtime", "HexPlayerBody.cs" );
		var source = SourceWithoutComments( playerBody );

		StringAssert.Contains( source, "new GameObject( false, \"Hexagon Authoritative Body\" )" );
		Assert.IsFalse( source.Contains( "new GameObject( GameObject, false", StringComparison.Ordinal ) );
		StringAssert.Contains( source, "Owner = null!" );
		StringAssert.Contains( source, "StartEnabled = false" );
		StringAssert.Contains( source, "OwnerTransfer = OwnerTransfer.Fixed" );
		StringAssert.Contains( source, "OrphanedMode = NetworkOrphaned.Destroy" );
		StringAssert.Contains( source, "controller.UseInputControls = false" );
		StringAssert.Contains( source, "Components.Get<PlayerController>( FindMode.EverythingInSelf )" );
		StringAssert.Contains( source, "NetworkMode = NetworkMode.Never" );
		StringAssert.Contains( source, "if ( _predictedBody is not null ) DestroyPredictedBody()" );
		StringAssert.Contains( source, "AuthoritativeBodyReplacement.RequireActivated" );
		StringAssert.Contains( source, "_candidate.Network.Refresh()" );
	}

	[TestMethod]
	public void IdentityShellDestructionCleansIndependentAuthoritativeBodies()
	{
		var playerBody = Path.Combine( V2Root(), "Runtime", "HexPlayerBody.cs" );
		var source = SourceWithoutComments( playerBody );
		var onDestroy = source.IndexOf( "protected override void OnDestroy()", StringComparison.Ordinal );
		var preparedCleanup = source.IndexOf( "_preparedBody?.Dispose()", onDestroy, StringComparison.Ordinal );
		var activeCleanup = source.IndexOf( "DestroyAuthoritativeBody()", preparedCleanup, StringComparison.Ordinal );

		Assert.IsGreaterThanOrEqualTo( 0, onDestroy );
		Assert.IsGreaterThan( onDestroy, preparedCleanup );
		Assert.IsGreaterThan( preparedCleanup, activeCleanup );
	}

	[TestMethod]
	public void ClientInputCannotBecomeSpatialAuthority()
	{
		var playerBody = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexPlayerBody.cs" ) );
		StringAssert.Contains( playerBody, "Rpc.Host( NetFlags.OwnerOnly | NetFlags.UnreliableNoDelay )" );
		StringAssert.Contains( playerBody, "Rpc.Caller.Id != HostConnection.Id" );
		StringAssert.Contains( playerBody, "_inputAdmission.TryBeginAttempt" );
		StringAssert.Contains( playerBody, "_inputAdmission.TryAcceptCharged" );
		StringAssert.Contains( playerBody, "HostInputAuthenticator" );
		StringAssert.Contains( playerBody, "HasProcessedInput" );
		StringAssert.Contains( playerBody, "ProcessedMovementState" );
		StringAssert.Contains( playerBody, "PredictionReconciliation.Calculate" );
		StringAssert.Contains( playerBody, "AuthoritativeBody" );
		Assert.IsFalse( playerBody.Contains( "ProcessedInputSequence == 0", StringComparison.Ordinal ),
			"Sequence zero is valid after wrap and cannot be used as the unprocessed-input sentinel." );
		Assert.IsFalse( playerBody.Contains( "PlayableBody", StringComparison.Ordinal ) );

		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		StringAssert.Contains( runtime, "connection.CanSpawnObjects = false" );
		StringAssert.Contains( runtime, "connection.CanRefreshObjects = false" );
		StringAssert.Contains( runtime, "connection.CanDestroyObjects = false" );
		StringAssert.Contains( runtime, "PlayerInputSessionAuthentication.IsAuthorized" );
	}

	[TestMethod]
	public void ProjectNetworkingAndPredictionCollisionDefaultsAreFailClosed()
	{
		var networking = File.ReadAllText( Path.Combine( ProductRoot(), "ProjectSettings", "Networking.config" ) );
		foreach ( var permission in new[]
		{
			"\"ClientsCanSpawnObjects\": false",
			"\"ClientsCanRefreshObjects\": false",
			"\"ClientsCanDestroyObjects\": false"
		} ) StringAssert.Contains( networking, permission );
		StringAssert.Contains( networking, "\"UpdateRate\": 30" );
		var collision = File.ReadAllText( Path.Combine( ProductRoot(), "ProjectSettings", "Collision.config" ) );
		StringAssert.Contains( collision, "\"b\": \"prediction\"" );
		StringAssert.Contains( collision, "\"r\": \"Ignore\"" );
	}

	[TestMethod]
	public void HostServicePublicationAndRpcShutdownAreFailClosed()
	{
		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		StringAssert.Contains( runtime, "OperationResult<HexHostServicesComponent> CreateHostServices()" );
		StringAssert.Contains( runtime, "HostServicePublication.RequirePublished" );
		StringAssert.Contains( runtime, "servicesObject.NetworkSpawn" );
		StringAssert.Contains( runtime, "if ( servicesObject.IsValid() ) servicesObject.Destroy()" );
		StringAssert.Contains( runtime, "if ( hostServices.Failed )" );

		var shutdown = runtime.IndexOf( "private async Task<OperationResult> ShutdownHostAsync()", StringComparison.Ordinal );
		var commandDrain = runtime.IndexOf( "_hostOperations.DrainAsync()", shutdown, StringComparison.Ordinal );
		var disconnect = runtime.IndexOf( "application.Disconnected", shutdown, StringComparison.Ordinal );
		var applicationDrain = runtime.IndexOf( "application.DisposeAsync", shutdown, StringComparison.Ordinal );
		var persistenceDrain = runtime.IndexOf( "persistence.ShutdownAsync", shutdown, StringComparison.Ordinal );
		var persistenceDispose = runtime.IndexOf( "persistence.DisposeAsync", shutdown, StringComparison.Ordinal );
		var quiescedEvidence = runtime.IndexOf( "application.CompleteQuiescedShutdown", shutdown, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, shutdown );
		Assert.IsLessThan( commandDrain, disconnect, "Disconnect callbacks must revoke sessions before RPC dispatch drains." );
		Assert.IsLessThan( applicationDrain, commandDrain, "RPC dispatch must finish before application disposal." );
		Assert.IsLessThan( persistenceDrain, applicationDrain, "Application disposal must finish before persistence shutdown." );
		Assert.IsLessThan( persistenceDispose, persistenceDrain, "Persistence must stop before it is disposed." );
		Assert.IsLessThan( quiescedEvidence, persistenceDispose, "Quiesced evidence must follow persistence disposal." );

		var services = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexHostServicesComponent.cs" ) );
		StringAssert.Contains( services, "runtime.TryStartHostOperation" );
		Assert.IsFalse( services.Contains( "_ = DispatchAsync", StringComparison.Ordinal ) );
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
