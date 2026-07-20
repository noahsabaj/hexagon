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
	public void PlayerIsASingleConnectionOwnedObjectRunningANativeController()
	{
		var playerBody = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexPlayerBody.cs" ) );
		// The player is one connection-owned object running a native, owner-simulated
		// PlayerController. This is the engine idiom (ownership == authority) that keeps the
		// body from being pinned to the world origin by host physics.
		StringAssert.Contains( playerBody, "controller.UseInputControls = true" );
		StringAssert.Contains( playerBody, "controller.UseLookControls = true" );
		StringAssert.Contains( playerBody, "controller.EnablePressing = true" );
		// No separate unowned body, and no revived custom client predictor.
		Assert.IsFalse( playerBody.Contains( "Owner = null!", StringComparison.Ordinal ),
			"The player body is the connection-owned object; no unowned host-simulated body may be spawned." );
		Assert.IsFalse( playerBody.Contains( "NetworkMode = NetworkMode.Never", StringComparison.Ordinal ),
			"The custom client predictor is removed in favour of the native controller's prediction." );

		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		StringAssert.Contains( runtime, "playerObject.NetworkSpawn( connection )" );
	}

	[TestMethod]
	public void ClientInputCannotBecomeSpatialAuthority()
	{
		var playerBody = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexPlayerBody.cs" ) );
		// Movement is owner-simulated, but position AUTHORITY stays on the host: it validates
		// the owner-reported transform against the controller's speed envelope and corrects
		// only through a host-authored channel. A client can never mint spatial authority.
		StringAssert.Contains( playerBody, "Sandbox.Networking.IsHost && GameObject.Network.IsProxy && IsEmbodied" );
		StringAssert.Contains( playerBody, "HostValidateMovement" );
		StringAssert.Contains( playerBody, "HexMovementValidator.Evaluate" );
		// The correction pulse is the only authority write to position, and it is host-authored.
		StringAssert.Contains( playerBody, "[Sync( SyncFlags.FromHost )] public Vector3 AuthoritativePosition" );
		StringAssert.Contains( playerBody, "[Sync( SyncFlags.FromHost )] public int CorrectionTick" );
		// The owner APPLIES corrections; it does not author them.
		StringAssert.Contains( playerBody, "GameObject.WorldPosition = AuthoritativePosition" );
		StringAssert.Contains( playerBody, "AuthoritativeBody" );
		// Gameplay resolves spatial checks against the host-validated position (not the raw client
		// transform), and a client that keeps reporting out-of-envelope positions is enforced
		// against — kicked, not merely nudged.
		StringAssert.Contains( playerBody, "public Vector3 AuthoritativeWorldPosition" );
		StringAssert.Contains( playerBody, "connection?.Kick(" );
		// The deleted owner->host input pump must not return in any form.
		Assert.IsFalse( playerBody.Contains( "SubmitInputFrame", StringComparison.Ordinal ),
			"Movement is owner-simulated; there must be no owner->host input RPC." );
		Assert.IsFalse( playerBody.Contains( "PlayableBody", StringComparison.Ordinal ) );

		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		StringAssert.Contains( runtime, "connection.CanSpawnObjects = false" );
		StringAssert.Contains( runtime, "connection.CanRefreshObjects = false" );
		StringAssert.Contains( runtime, "connection.CanDestroyObjects = false" );
	}

	[TestMethod]
	public void ProjectNetworkingAndPredictionCollisionDefaultsAreFailClosed()
	{
		var networking = File.ReadAllText( Path.Combine( ProductRoot(), "ProjectSettings", "Networking.config" ) );
		foreach ( var permission in new[]
		{
			"\"ClientsCanSpawnObjects\": false",
			"\"ClientsCanRefreshObjects\": false",
			"\"ClientsCanDestroyObjects\": false",
			// Host migration would hand authority to a machine with no host application,
			// no domain services, and no persistence lease; both flags must stay closed.
			"\"DestroyLobbyWhenHostLeaves\": true",
			"\"AutoSwitchToBestHost\": false"
		} ) StringAssert.Contains( networking, permission );
		StringAssert.Contains( networking, "\"UpdateRate\": 30" );

		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		StringAssert.Contains( runtime, "void Component.INetworkListener.OnBecameHost( Connection previousHost )" );
		StringAssert.Contains( runtime, "HEXAGON_HOST_MIGRATION_REFUSED" );
		StringAssert.Contains( runtime, "Networking.Disconnect()" );
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
		var pairedDisconnectLoop = runtime.IndexOf( "foreach ( var disconnect in disconnects )", shutdown, StringComparison.Ordinal );
		var sessionDisconnect = runtime.IndexOf( "disconnect.Session.Disconnect()", pairedDisconnectLoop, StringComparison.Ordinal );
		var disconnect = runtime.IndexOf( "application.Disconnected", shutdown, StringComparison.Ordinal );
		var applicationDrain = runtime.IndexOf( "application.DisposeAsync", shutdown, StringComparison.Ordinal );
		var persistenceDrain = runtime.IndexOf( "persistence.ShutdownAsync", shutdown, StringComparison.Ordinal );
		var persistenceDispose = runtime.IndexOf( "persistence.DisposeAsync", shutdown, StringComparison.Ordinal );
		var quiescedEvidence = runtime.IndexOf( "application.CompleteQuiescedShutdown", shutdown, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, shutdown );
		Assert.IsLessThan( disconnect, sessionDisconnect,
			"Each client session must be revoked immediately before its application disconnect callback." );
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
	public void ClientStateSyncShellAppliesOnlyAfterScopeCaptureAndEncodeAborts()
	{
		var services = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexHostServicesComponent.cs" ) );
		var send = services.IndexOf( "public void SendClientState(", StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, send );
		var scopeCapture = services.IndexOf( "runtime.CaptureClientScope( recipient )", send, StringComparison.Ordinal );
		var encodeAbort = services.IndexOf( "LogSnapshotWireFailure( \"ENCODE\", \"client-state\"", send, StringComparison.Ordinal );
		var applyShell = services.IndexOf( "player.HostApplyPublicSnapshot( publicSnapshot )", send, StringComparison.Ordinal );
		var wireSend = services.IndexOf( "ReceiveClientState( scope.Value, encoded.Value )", send, StringComparison.Ordinal );
		Assert.IsGreaterThanOrEqualTo( 0, applyShell );
		Assert.IsGreaterThanOrEqualTo( 0, wireSend );
		Assert.IsLessThan( applyShell, scopeCapture,
			"The scope-capture abort must run before the replicated [Sync] shell is mutated." );
		Assert.IsLessThan( applyShell, encodeAbort,
			"The wire-encode abort must run before the replicated [Sync] shell is mutated." );
		Assert.IsLessThan( wireSend, applyShell,
			"The [Sync] shell mutation must sit immediately before the send, after every abort exit." );
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
	public void PersistenceHandoffDefersToRecoveryAndQuarantineIsOperatorArmed()
	{
		var runtime = SourceWithoutComments( Path.Combine( V2Root(), "Runtime", "HexagonRuntimeSystem.cs" ) );
		// The scene-handoff gate awaits the previous owner (bounded) and then defers ownership and
		// integrity to the exclusive lease and WAL recovery — it must never fail-closed on the
		// predecessor's drain outcome again (that was the self-poisoning wedge).
		StringAssert.Contains( runtime, "AwaitPredecessorSettlementAsync" );
		StringAssert.Contains( runtime, "SceneHandoffPolicy.EvaluatePredecessor" );
		Assert.IsFalse( runtime.Contains( "did not drain cleanly", StringComparison.Ordinal ),
			"The barrier must not fail-closed on a predecessor drain; the lease and recovery are the authorities." );
		// Corruption recovery is operator-armed and one-shot, never automatic.
		// The arming is consumed per host-start (read into a local, then reset) so it cannot linger
		// and silently quarantine a later store.
		StringAssert.Contains( runtime, "var quarantineCorruptStore = HexagonRuntimeOverrides.QuarantineCorruptStore" );
		StringAssert.Contains( runtime, "HexagonRuntimeOverrides.QuarantineCorruptStore = false" );
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
