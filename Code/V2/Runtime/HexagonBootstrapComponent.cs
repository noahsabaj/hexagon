#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Infrastructure;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// The only framework component required in a game scene. The selected schema
/// must be supplied explicitly by a mounted IHexSchemaSource.
/// </summary>
[Title( "Hexagon Bootstrap" )]
[Category( "Hexagon" )]
public sealed class HexagonBootstrapComponent : Component
{
	[Property]
	public string SchemaId { get; set; } = "hl2rp";

	/// <summary>
	/// Optional relative FileSystem.Data prefix used by isolated verification runs.
	/// Production scenes leave this blank and use
	/// hexagon/persistence/v3/&lt;schema-id&gt;. Legacy v2 data is never opened,
	/// migrated, or deleted.
	/// </summary>
	[Property]
	public string PersistenceRootOverride { get; set; } = string.Empty;

	/// <summary>
	/// Optional opaque scenario name exposed to the schema host application. The
	/// framework never executes test mutations itself.
	/// </summary>
	[Property]
	public string VerificationProbe { get; set; } = string.Empty;

	protected override void OnValidate()
	{
		base.OnValidate();
		SchemaId = string.IsNullOrWhiteSpace( SchemaId ) ? "hl2rp" : StableIdentifier.Require( SchemaId, nameof(SchemaId) );
		PersistenceRootOverride = string.IsNullOrWhiteSpace( PersistenceRootOverride )
			? string.Empty
			: PrefixedPersistenceStorage.Normalize( PersistenceRootOverride );
		VerificationProbe = (VerificationProbe ?? string.Empty).Trim();
		if ( VerificationProbe.Length > 128 ) VerificationProbe = VerificationProbe[..128];
	}
}
