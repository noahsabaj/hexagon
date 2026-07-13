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
	private PreparedPlayableBody? _preparedBody;
	private bool _stripRequestedDuringPreparation;

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
		var prepared = HostPreparePlayableBody( configure );
		if ( prepared.Failed )
			return OperationResult<GameObject>.Failure(
				prepared.Error!.Code, prepared.Error.Message, prepared.Error.Details );
		return prepared.Value.TryActivate( out var playableBody ) && playableBody is not null
			? OperationResult<GameObject>.Success( playableBody )
			: OperationResult<GameObject>.Failure(
				ErrorCode.Conflict, "The playable body replacement was invalidated before activation." );
	}

	/// <summary>
	/// Fully configures and network-spawns a disabled replacement while preserving
	/// the current body. Callers may complete fallible durable preparation, then
	/// activate the candidate with no remaining fallible composition work, or
	/// dispose it to leave the current body untouched.
	/// </summary>
	public OperationResult<PreparedPlayableBody> HostPreparePlayableBody( Action<GameObject> configure )
	{
		ArgumentNullException.ThrowIfNull( configure );
		if ( !Sandbox.Networking.IsHost )
			return OperationResult<PreparedPlayableBody>.Failure(
				ErrorCode.Unauthorized, "Only the host can prepare a playable body." );
		if ( HostConnection is null )
			return OperationResult<PreparedPlayableBody>.Failure(
				ErrorCode.NotFound, "The player connection is unavailable." );
		if ( _preparedBody is not null )
			return OperationResult<PreparedPlayableBody>.Failure(
				ErrorCode.Conflict, "A playable body replacement is already prepared." );

		var previous = PlayableBody;
		var candidate = new GameObject( GameObject, false, "Hexagon Playable Body" );
		try
		{
			configure( candidate );
			candidate.NetworkSpawn( HostConnection );
			var prepared = new PreparedPlayableBody( this, candidate, previous );
			_preparedBody = prepared;
			return OperationResult<PreparedPlayableBody>.Success( prepared );
		}
		catch ( Exception exception )
		{
			candidate.Destroy();
			Log.Error( exception, "Hexagon schema failed to compose a playable body." );
			return OperationResult<PreparedPlayableBody>.Failure(
				ErrorCode.InternalError, "The playable body could not be composed." );
		}
	}

	/// <summary>
	/// Removes every schema-owned controller/model/camera component by destroying
	/// their dedicated child. If a replacement is being prepared, the current body
	/// is disabled immediately and destruction is resolved with that preparation:
	/// aborting or attempted activation completes the requested strip.
	/// The host-owned identity shell and FromHost state remain.
	/// </summary>
	public OperationResult HostStripPlayableBody()
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can strip a playable body." );
		if ( _preparedBody is not null )
		{
			_stripRequestedDuringPreparation = true;
			try
			{
				if ( _playableBody is not null && _playableBody.IsValid() ) _playableBody.Enabled = false;
				return OperationResult.Success();
			}
			catch ( Exception exception )
			{
				Log.Error( exception, "Hexagon could not disable a playable body while replacement was pending." );
				return OperationResult.Failure(
					ErrorCode.InternalError, "The playable body could not be disabled while replacement was pending." );
			}
		}
		return DestroyPlayableBody();
	}

	private OperationResult DestroyPlayableBody()
	{
		Exception? cleanupFailure = null;
		try
		{
			if ( _playableBody is not null && _playableBody.IsValid() ) _playableBody.Destroy();
		}
		catch ( Exception exception ) { cleanupFailure ??= exception; }
		_playableBody = null;
		if ( cleanupFailure is null ) return OperationResult.Success();
		Log.Error( cleanupFailure, "Hexagon could not fully destroy a playable body." );
		return OperationResult.Failure(
			ErrorCode.InternalError, "The playable body could not be fully destroyed." );
	}

	public sealed class PreparedPlayableBody : IDisposable
	{
		private readonly HexPlayerBody _owner;
		private readonly GameObject _candidate;
		private readonly GameObject? _previous;
		private bool _completed;
		private bool _activated;

		internal PreparedPlayableBody( HexPlayerBody owner, GameObject candidate, GameObject? previous )
		{
			_owner = owner;
			_candidate = candidate;
			_previous = previous;
		}

		/// <summary>
		/// Activates an already configured and spawned candidate unless an external
		/// lifecycle strip invalidated the preparation. Engine cleanup is contained and
		/// logged so no exception can hide a preceding durable commit.
		/// </summary>
		public bool TryActivate( out GameObject? playableBody )
		{
			playableBody = null;
			if ( _completed )
			{
				if ( !_activated ) return false;
				playableBody = _owner.PlayableBody;
				return playableBody is not null;
			}
			if ( _owner._stripRequestedDuringPreparation )
			{
				Abort( completeDeferredStrip: true );
				return false;
			}
			_completed = true;
			_activated = true;
			_owner._preparedBody = null;
			try
			{
				_candidate.Enabled = true;
				_owner._playableBody = _candidate;
				if ( _previous is not null && _previous.IsValid() ) _previous.Destroy();
			}
			catch ( Exception exception )
			{
				// The candidate is already configured and network-spawned. Preserve it as
				// the authoritative body and contain best-effort replacement cleanup.
				_owner._playableBody = _candidate;
				Log.Error( exception, "Hexagon could not fully activate a prepared playable body." );
			}
			playableBody = _candidate;
			return true;
		}

		public void Dispose()
		{
			if ( _completed ) return;
			Abort( _owner._stripRequestedDuringPreparation );
		}

		private void Abort( bool completeDeferredStrip )
		{
			_completed = true;
			if ( ReferenceEquals( _owner._preparedBody, this ) ) _owner._preparedBody = null;
			_owner._stripRequestedDuringPreparation = false;
			try
			{
				if ( _candidate.IsValid() ) _candidate.Destroy();
			}
			catch ( Exception exception )
			{
				Log.Error( exception, "Hexagon could not destroy a prepared playable body." );
			}
			if ( completeDeferredStrip ) _ = _owner.DestroyPlayableBody();
		}
	}

}
