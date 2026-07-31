#nullable enable

using Hexagon.V2.Client;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Non-networked client composition root. Razor roots attach beneath this object.
/// </summary>
public sealed class HexClientRootComponent : Component
{
	public HexClientStore? Store { get; internal set; }
	public HexClientController? Controller { get; internal set; }
}
