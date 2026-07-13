#nullable enable

using System;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Client-owned input/body object. Every server-authored synchronized member is
/// explicitly FromHost, so ownership cannot grant authority over identity/state.
/// </summary>
public sealed class HexPlayerBody : Component
{
	private GameObject? _playableBody;

	[Sync( SyncFlags.FromHost )] public Guid ConnectionGuid { get; private set; }
	[Sync( SyncFlags.FromHost )] public ulong PlatformAccountDisplay { get; private set; }
	[Sync( SyncFlags.FromHost )] public string PlatformDisplayName { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public Guid CharacterGuid { get; private set; }
	[Sync( SyncFlags.FromHost )] public string CharacterName { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string CharacterDescription { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string CharacterModel { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string FactionId { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string ClassId { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public bool HasActiveCharacter { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool IsDead { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool IsWeaponRaised { get; private set; }

	internal Connection? HostConnection { get; set; }
	public GameObject? PlayableBody => _playableBody is not null && _playableBody.IsValid()
		? _playableBody
		: null;

	internal void HostSetConnection( Connection connection )
	{
		HostConnection = connection;
		ConnectionGuid = connection.Id;
		PlatformAccountDisplay = connection.SteamId.ValueUnsigned;
		PlatformDisplayName = connection.DisplayName;
	}

	internal void HostApplyPublicSnapshot( PlayerPublicSnapshot snapshot )
	{
		ConnectionGuid = snapshot.ConnectionId.Value;
		PlatformAccountDisplay = snapshot.PlatformAccountId;
		PlatformDisplayName = snapshot.PlatformDisplayName;
		CharacterGuid = snapshot.CharacterId?.Value ?? Guid.Empty;
		CharacterName = snapshot.ReplicatedCharacterName;
		CharacterDescription = snapshot.Description;
		CharacterModel = snapshot.Model?.Value ?? string.Empty;
		FactionId = snapshot.Faction?.Value ?? string.Empty;
		ClassId = snapshot.Class?.Value ?? string.Empty;
		HasActiveCharacter = snapshot.HasCharacter;
		IsDead = snapshot.IsDead;
		IsWeaponRaised = snapshot.IsWeaponRaised;
	}

	internal void HostClearCharacter()
	{
		CharacterGuid = Guid.Empty;
		CharacterName = string.Empty;
		CharacterDescription = string.Empty;
		CharacterModel = string.Empty;
		FactionId = string.Empty;
		ClassId = string.Empty;
		HasActiveCharacter = false;
		IsDead = false;
		IsWeaponRaised = false;
		_ = HostStripPlayableBody();
	}

	/// <summary>
	/// Creates the schema-owned playable child atomically, then network-spawns it
	/// with the same client owner as this input shell. Schema code adds its
	/// controller, model, and camera components through <paramref name="configure"/>.
	/// </summary>
	public OperationResult<GameObject> HostBuildPlayableBody( Action<GameObject> configure )
	{
		ArgumentNullException.ThrowIfNull( configure );
		if ( !Sandbox.Networking.IsHost )
			return OperationResult<GameObject>.Failure( ErrorCode.Unauthorized, "Only the host can build a playable body." );
		if ( HostConnection is null )
			return OperationResult<GameObject>.Failure( ErrorCode.NotFound, "The player connection is unavailable." );

		var previous = PlayableBody;
		var candidate = new GameObject( GameObject, false, "Hexagon Playable Body" );
		try
		{
			configure( candidate );
			candidate.Enabled = true;
			candidate.NetworkSpawn( HostConnection );
			if ( previous is not null && previous.IsValid() ) previous.Destroy();
			_playableBody = candidate;
			return OperationResult<GameObject>.Success( candidate );
		}
		catch ( Exception exception )
		{
			candidate.Destroy();
			Log.Error( exception, "Hexagon schema failed to compose a playable body." );
			return OperationResult<GameObject>.Failure( ErrorCode.InternalError, "The playable body could not be composed." );
		}
	}

	/// <summary>
	/// Removes every schema-owned controller/model/camera component by destroying
	/// their dedicated child. The host-owned identity shell and FromHost state remain.
	/// </summary>
	public OperationResult HostStripPlayableBody()
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can strip a playable body." );
		if ( _playableBody is not null && _playableBody.IsValid() ) _playableBody.Destroy();
		_playableBody = null;
		return OperationResult.Success();
	}

}
