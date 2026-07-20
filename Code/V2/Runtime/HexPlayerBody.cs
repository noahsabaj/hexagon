#nullable enable

using System;
using System.Diagnostics;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Connection-owned player object. The owning client runs a native
/// <see cref="PlayerController"/> (movement, look, and pressing); the host authors
/// identity via <c>[Sync(FromHost)]</c> and governs position by validating the
/// owner-reported transform and issuing host-authored correction pulses. Adopting the
/// engine's ownership==authority idiom keeps the body from fighting the physics/network
/// layers — the previous unowned, host-simulated body was pinned to the world origin
/// because on the host its physics body seeded at the origin and drove the transform.
/// </summary>
public sealed class HexPlayerBody : Component, IRuntimePlayer
{
	private const float CorrectionCooldownSeconds = 0.35f;

	private int _appliedCorrectionTick = -1;

	// Host movement validation / spawn baseline.
	private Transform _hostSpawnTransform;
	private Vector3 _lastValidatedPosition;
	private double _lastValidatedSeconds;
	private double _correctionCooldownUntilSeconds;
	private bool _validatorPrimed;

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
	[Sync( SyncFlags.FromHost )] public bool IsEmbodied { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool IsMovementLocked { get; private set; }
	[Sync( SyncFlags.FromHost )] public Vector3 AuthoritativePosition { get; private set; }
	[Sync( SyncFlags.FromHost )] public int CorrectionTick { get; private set; }

	internal Connection? HostConnection { get; set; }

	/// <summary>
	/// The playable body. The player is a single connection-owned object, so this is
	/// that object itself while embodied, else null — matching the "stripped body"
	/// contract every caller already handles.
	/// </summary>
	public GameObject? AuthoritativeBody => IsEmbodied && GameObject.IsValid() ? GameObject : null;

	public bool TryGetUsableAuthoritativeBody( out GameObject body )
	{
		body = GameObject;
		var controller = GameObject.IsValid() ? GameObject.Components.Get<PlayerController>() : null;
		return PlayerBodyAuthorityRules.IsUsable(
			HasActiveCharacter,
			IsDead,
			IsEmbodied && GameObject.IsValid(),
			IsEmbodied && GameObject.IsValid() && controller is { Enabled: true } );
	}

	/// <summary>
	/// True when <paramref name="source"/> is an input/press source belonging to the
	/// local owning client's player object (its native controller or this shell). Callers
	/// must still route mutations through host command authority; this is a presentation
	/// and local-source check only.
	/// </summary>
	public static bool IsLocalPredictionSource( Component? source ) =>
		source is not null && source.GameObject.IsValid() &&
		source.GameObject.Components.Get<HexPlayerBody>() is { } shell && shell.IsOwnerLocal;

	private bool IsOwnerLocal => GameObject.Network.Active && GameObject.Network.IsOwner;

	internal void HostSetConnection( Connection connection )
	{
		HostConnection = connection;
		_hostSpawnTransform = GameObject.WorldTransform.WithScale( 1 );
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

	// --- Embodiment (host authority) ---

	/// <summary>
	/// Configures and enables the owning client's native player body at the host-held
	/// spawn baseline. The gamemode <paramref name="configure"/> callback adds the
	/// PlayerController, model, and tags; this enforces the owner-simulated control
	/// contract, seats the authoritative position, and publishes the embodied state.
	/// </summary>
	public OperationResult<GameObject> HostEmbody( Action<GameObject> configure )
	{
		ArgumentNullException.ThrowIfNull( configure );
		if ( !Sandbox.Networking.IsHost )
			return OperationResult<GameObject>.Failure( ErrorCode.Unauthorized, "Only the host can embody a player." );
		if ( HostConnection is null )
			return OperationResult<GameObject>.Failure( ErrorCode.NotFound, "The player connection is unavailable." );
		try
		{
			configure( GameObject );
			var controller = GameObject.Components.Get<PlayerController>();
			if ( controller is null )
				throw new InvalidOperationException( "An embodied player requires a PlayerController." );
			controller.UseInputControls = true;
			controller.UseLookControls = true;
			controller.UseCameraControls = true;
			controller.UseAnimatorControls = true;
			controller.EnablePressing = true;
			// The animated model lives on a child "Body" object (created by the gamemode's
			// configure callback); it must NOT sit on this object — PlayerController.UpdateAnimation
			// sets renderer.LocalPosition for the duck-bob, which on the body object would zero its
			// world position and pin us to the origin. Link the controller to it EXPLICITLY (as the
			// engine's own CreateBodyRenderer does): the auto-link runs when the controller enables,
			// which is before the child exists, so it misses it and UpdateAnimation stays gated off
			// (the model would never leave its idle pose).
			if ( GameObject.Components.Get<SkinnedModelRenderer>( FindMode.InChildren ) is { } renderer )
			{
				renderer.Enabled = true;
				controller.Renderer = renderer;
			}
			controller.Enabled = true;
			IsMovementLocked = false;
			IsEmbodied = true;
			IssueCorrection( _hostSpawnTransform.Position );
			GameObject.Network.Refresh();
			return OperationResult<GameObject>.Success( GameObject );
		}
		catch ( Exception exception )
		{
			Log.Error( exception, "Hexagon could not embody the player body." );
			return OperationResult<GameObject>.Failure( ErrorCode.InternalError, "The player body could not be composed." );
		}
	}

	/// <summary>
	/// Disables movement/rendering on the owned player object without destroying it (the
	/// object carries identity and network ownership). Mirrors the previous body strip.
	/// </summary>
	public OperationResult HostDisembody()
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can disembody a player." );
		IsEmbodied = false;
		IsMovementLocked = false;
		// Also called during host teardown/drain, where this object may already be
		// destroyed. Component cleanup is best-effort and must never fail the drain.
		if ( !GameObject.IsValid() ) return OperationResult.Success();
		try
		{
			if ( GameObject.Components.Get<PlayerController>() is { } controller )
			{
				controller.WishVelocity = Vector3.Zero;
				controller.Enabled = false;
			}
			if ( GameObject.Components.Get<SkinnedModelRenderer>( FindMode.InChildren ) is { } renderer )
				renderer.Enabled = false;
			if ( !Game.IsClosing ) GameObject.Network.Refresh();
		}
		catch ( Exception exception )
		{
			Log.Warning( $"Hexagon player disembody cleanup was degraded during teardown: {exception.Message}" );
		}
		return OperationResult.Success();
	}

	/// <summary>Host-authoritative movement lock (e.g. restraints); the owner honors it.</summary>
	public OperationResult HostSetMovementLocked( bool locked )
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can lock player movement." );
		IsMovementLocked = locked;
		if ( locked && GameObject.Components.Get<PlayerController>() is { } controller )
			controller.WishVelocity = Vector3.Zero;
		if ( GameObject.IsValid() ) GameObject.Network.Refresh();
		return OperationResult.Success();
	}

	// Seats the authoritative baseline and pulses a correction the owner applies, holding
	// off validation for the round-trip so a legitimate host teleport is never re-flagged.
	private void IssueCorrection( Vector3 target )
	{
		AuthoritativePosition = target;
		CorrectionTick++;
		_lastValidatedPosition = target;
		_validatorPrimed = true;
		_lastValidatedSeconds = MonotonicSeconds();
		_correctionCooldownUntilSeconds = _lastValidatedSeconds + CorrectionCooldownSeconds;
	}

	// --- Update loops ---

	protected override void OnUpdate()
	{
		if ( !GameObject.IsValid() || !IsOwnerLocal ) return;
		// Lock the cursor to the game for mouse-look while embodied and alive; release it
		// (Auto: shown only when pointer-events UI is on-screen) for character selection and
		// death. The native PlayerController owns the camera, look, and first/third-person.
		Mouse.Visibility = IsEmbodied && !IsDead ? MouseVisibility.Hidden : MouseVisibility.Auto;
	}

	protected override void OnFixedUpdate()
	{
		if ( !GameObject.IsValid() ) return;
		if ( IsOwnerLocal )
		{
			OwnerReconcileControl();
			OwnerApplyCorrection();
		}
		if ( Sandbox.Networking.IsHost && GameObject.Network.IsProxy && IsEmbodied )
			HostValidateMovement();
	}

	// Owner snaps to any newly published authoritative position (spawn / respawn / anti-cheat).
	private void OwnerApplyCorrection()
	{
		if ( _appliedCorrectionTick == CorrectionTick ) return;
		_appliedCorrectionTick = CorrectionTick;
		if ( !IsEmbodied || !GameObject.IsValid() ) return;
		GameObject.WorldPosition = AuthoritativePosition;
		if ( GameObject.Components.Get<PlayerController>()?.Body is { } rigid && rigid.IsValid() )
		{
			rigid.Velocity = Vector3.Zero;
			rigid.AngularVelocity = Vector3.Zero;
		}
		GameObject.Network.ClearInterpolation();
	}

	// Owner reconciles its native controller to the host-authored authority flags:
	// the controller runs only while embodied and alive, and takes movement input only
	// while unlocked. Writes only on change so it is cheap to run every fixed update.
	private void OwnerReconcileControl()
	{
		if ( GameObject.Components.Get<PlayerController>() is not { } controller ) return;
		var shouldControl = IsEmbodied && !IsDead;
		if ( controller.Enabled != shouldControl ) controller.Enabled = shouldControl;
		if ( !shouldControl ) return;
		var inputAllowed = !IsMovementLocked;
		if ( controller.UseInputControls != inputAllowed )
		{
			controller.UseInputControls = inputAllowed;
			if ( !inputAllowed ) controller.WishVelocity = Vector3.Zero;
		}
	}

	// Host bounds the owner-reported transform to the controller's speed envelope.
	private void HostValidateMovement()
	{
		if ( GameObject.Components.Get<PlayerController>() is not { } controller ) return;
		var now = MonotonicSeconds();
		if ( now < _correctionCooldownUntilSeconds ) return;
		var position = GameObject.WorldPosition;
		if ( !_validatorPrimed )
		{
			_validatorPrimed = true;
			_lastValidatedPosition = position;
			_lastValidatedSeconds = now;
			return;
		}
		var dt = (float)Math.Clamp( now - _lastValidatedSeconds, 0.0, 0.5 );
		_lastValidatedSeconds = now;
		var frozen = !HasActiveCharacter || IsDead || IsMovementLocked;
		var decision = HexMovementValidator.Evaluate(
			new MovementSample( _lastValidatedPosition.x, _lastValidatedPosition.y, _lastValidatedPosition.z ),
			new MovementSample( position.x, position.y, position.z ),
			dt, controller.RunSpeed, controller.JumpSpeed, frozen );
		if ( decision.Corrected ) IssueCorrection( _lastValidatedPosition );
		else _lastValidatedPosition = position;
	}

	private static double MonotonicSeconds() =>
		Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
