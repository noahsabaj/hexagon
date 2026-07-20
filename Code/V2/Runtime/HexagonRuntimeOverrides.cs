#nullable enable

using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Host-only launch overrides. s&amp;box initializes ConVars from matching +switches,
/// allowing verification to isolate data without rewriting the authored scene.
/// </summary>
internal static class HexagonRuntimeOverrides
{
	[ConVar( "hexagon-data-root", ConVarFlags.Server | ConVarFlags.Hidden,
		Help = "Relative FileSystem.Data prefix for an isolated Hexagon host run." )]
	public static string PersistenceRoot { get; set; } = string.Empty;

	[ConVar( "hexagon-verification-probe", ConVarFlags.Server | ConVarFlags.Hidden,
		Help = "Opaque verification scenario exposed to the schema host application." )]
	public static string VerificationProbe { get; set; } = string.Empty;

	[ConVar( "hexagon-persistence-quarantine", ConVarFlags.Server,
		Help = "Operator-armed one-shot recovery: on the next host start, if this store is genuinely " +
			"corrupt, archive it aside and rebuild an empty store instead of failing closed. Consumed " +
			"and reset once it takes effect." )]
	public static bool QuarantineCorruptStore { get; set; }
}
