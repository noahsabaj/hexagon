#nullable enable

using System;
using System.Diagnostics;
using Hexagon.V2.Domain;
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
	private int _appliedCorrectionTick = -1;

	// Host movement validation. The envelope is supplied by the runtime from configuration so the host's
	// limits and the owning client's HexMoveModeWalk are configured from one source; it falls back to the
	// framework default until the runtime seats it.
	private HexMovementEnvelope _envelope = HexMovementEnvelope.Default;
	private MovementAudit _audit = MovementAudit.Unprimed;
	// Sustained out-of-envelope reporting trips a kick. Measured in SECONDS of violation rather than a
	// count of them, because a count silently changes meaning whenever the tick rate or the correction
	// cooldown moves; honest lag produces bursts, not seconds of unbroken violation.
	private double _violationSeconds;

	/// <summary>
	/// Host-supplied movement envelope; see <see cref="HexMovementEnvelope"/>. Setting it publishes the
	/// two values the owning client's <see cref="HexMoveModeWalk"/> needs, so client physics and host
	/// validation are configured from one source instead of two constants that must be kept equal.
	/// </summary>
	internal HexMovementEnvelope MovementEnvelope
	{
		get => _envelope;
		set
		{
			_envelope = value;
			if ( !Sandbox.Networking.IsHost ) return;
			MovementStepHeight = value.StepHeight;
			MovementGroundAngle = value.GroundAngleDegrees;
		}
	}

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
	// The movement envelope the owning client configures its walk mode from. Host-authored so a client
	// cannot widen the limits its own physics obey and then report movement the host would refuse.
	[Sync( SyncFlags.FromHost )] public float MovementStepHeight { get; private set; }
	[Sync( SyncFlags.FromHost )] public float MovementGroundAngle { get; private set; }

	internal Connection? HostConnection { get; set; }

	/// <summary>
	/// Resolves where this player belongs. Supplied by the runtime when the body is spawned so
	/// that connect placement and every later embodiment ask the same question of the same
	/// implementation, rather than embodiment replaying a copy of the connect-time answer.
	/// </summary>
	internal Func<HexSpawnRequest, OperationResult<Transform>>? HostSpawnSelector { get; set; }

	/// <summary>
	/// The playable body. The player is a single connection-owned object, so this is
	/// that object itself while embodied, else null — matching the "stripped body"
	/// contract every caller already handles.
	/// </summary>
	public GameObject? AuthoritativeBody => IsEmbodied && GameObject.IsValid() ? GameObject : null;

	/// <summary>
	/// The host's authoritative world position for gameplay decisions (interaction reach,
	/// line-of-sight, combat traces, proximity chat). On the host a remote player is a network
	/// proxy whose transform the owning client authors, so this returns the last position that
	/// passed movement validation — a client cannot teleport it. For the host's own body and on
	/// clients it is the live transform. Spatial gameplay checks must resolve against this, never
	/// the raw client-authored <see cref="GameObject.WorldPosition"/>.
	/// </summary>
	public Vector3 AuthoritativeWorldPosition =>
		Sandbox.Networking.IsHost && GameObject.IsValid() && GameObject.Network.IsProxy && _audit.Primed
			? new Vector3( _audit.Last.X, _audit.Last.Y, _audit.Last.Z )
			: (GameObject.IsValid() ? GameObject.WorldPosition : AuthoritativePosition);

	/// <summary>
	/// The host-authoritative gameplay position for a player-body GameObject (the object carrying a
	/// <see cref="HexPlayerBody"/>), or its raw transform when it carries none. Use this for a spatial
	/// gameplay decision that starts from a body GameObject rather than the component in hand.
	/// </summary>
	public static Vector3 AuthoritativeWorldPositionOf( GameObject body ) =>
		body.IsValid() && body.Components.Get<HexPlayerBody>() is { } player
			? player.AuthoritativeWorldPosition
			: (body.IsValid() ? body.WorldPosition : Vector3.Zero);

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
			// includeDisabled matters on RESPAWN. HostDisembody deliberately disables the controller
			// rather than removing it (the object carries identity and network ownership), and the
			// gamemode's GetOrAddComponent uses FindMode.EverythingInSelf, so it hands back that same
			// disabled instance instead of adding a fresh one. Looking it up enabled-only therefore
			// found nothing and threw on every respawn - the whole death cycle was unreachable until
			// an administrative kill made it possible to die on demand. Enabled again below.
			var controller = GameObject.Components.Get<PlayerController>( includeDisabled: true );
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
			// Seat the framework walk mode BEFORE the controller enables: PlayerController runs
			// GetOrAddComponent<MoveModeWalk>() as it starts, so a mode present by then is the one it
			// adopts and no stock duplicate is created. It carries the host-published step height and
			// ground angle, keeping client physics and host validation on one envelope.
			GameObject.GetOrAddComponent<HexMoveModeWalk>();
			controller.Enabled = true;
			IsMovementLocked = false;
			IsEmbodied = true;
			// Ask where this player belongs rather than replaying where they first appeared, so a
			// respawn is placed by the same rule that placed the connect, and a game that overrides
			// that rule is honoured on both paths.
			var placement = ResolveSpawn( isRespawn: true );
			if ( placement.Failed )
				return OperationResult<GameObject>.Failure( placement.Error!.Code, placement.Error.Message );
			SeatAt( placement.Value );
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

	/// <summary>
	/// Moves this player authoritatively. This is the ONLY correct way for a host to place a
	/// player: on the host a remote body is a client-owned proxy, so writing its transform is
	/// overwritten on the owner's next tick while <see cref="AuthoritativeWorldPosition"/> keeps
	/// the old value — interaction reach and combat traces would then resolve against a point the
	/// player is not standing on. Seating the baseline and pulsing a correction is what makes the
	/// owner actually move, and holds validation off for the round trip so a deliberate move is
	/// never mistaken for a teleport.
	/// </summary>
	public OperationResult HostPlaceAt( Transform destination )
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can place a player." );
		if ( !GameObject.IsValid() )
			return OperationResult.Failure( ErrorCode.NotFound, "The player body is unavailable." );
		SeatAt( destination );
		GameObject.Network.Refresh();
		return OperationResult.Success();
	}

	/// <summary>Host-authoritative placement without the network refresh the caller may batch.</summary>
	private void SeatAt( Transform destination )
	{
		// The rotation is a starting facing only; the owning client authors look direction, so
		// only the position is corrected and enforced.
		GameObject.WorldTransform = destination.WithScale( 1 );
		IssueCorrection( destination.Position, restock: true );
	}

	private OperationResult<Transform> ResolveSpawn( bool isRespawn )
	{
		if ( HostSpawnSelector is not { } selector )
			return OperationResult<Transform>.Failure(
				ErrorCode.InternalError, "The player body has no spawn selector." );
		return selector( new HexSpawnRequest(
			ConnectionGuid,
			CharacterGuid == Guid.Empty ? null : new CharacterId( CharacterGuid ),
			IsRespawn: isRespawn ) );
	}

	// Seats the authoritative baseline and pulses a correction the owner applies, holding
	// off validation for the round-trip so a legitimate host teleport is never re-flagged.
	/// <summary>
	/// Publishes a host-authored position the owner snaps to, and reseats the movement model there.
	/// </summary>
	/// <param name="target">The world position the owner is told to adopt.</param>
	/// <param name="restock">
	/// True for a deliberate host placement (spawn, respawn, teleport), where the player legitimately
	/// starts fresh. False for an anti-cheat correction, which carries the jitter and step reservoirs
	/// across unchanged: refilling them would let a client restock budget by deliberately tripping a
	/// violation, and emptying them would make one lag spike cascade into the next.
	/// </param>
	private void IssueCorrection( Vector3 target, bool restock )
	{
		AuthoritativePosition = target;
		CorrectionTick++;
		// Reopen the audit window at the destination: a deliberate placement is not travel the player
		// performed, so it must not be charged against their envelope.
		_audit = MovementAudit.OpenAt(
			new MovementSample( target.x, target.y, target.z ), MonotonicSeconds() );
		if ( restock ) _violationSeconds = 0;
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

	/// <summary>
	/// Audits the owner-reported transform over a window. See <see cref="HexMovementValidator"/> for why
	/// this cannot be a per-tick check: the host observes an interpolated reconstruction, whose per-tick
	/// deltas are artifacts of buffer depth and packet timing rather than of player movement.
	/// </summary>
	private void HostValidateMovement()
	{
		if ( GameObject.Components.Get<PlayerController>() is not { } controller ) return;
		var position = GameObject.WorldPosition;
		var frozen = !HasActiveCharacter || IsDead || IsMovementLocked;

		var (decision, audit) = HexMovementValidator.Observe(
			_audit,
			new MovementSample( position.x, position.y, position.z ),
			MonotonicSeconds(),
			controller.RunSpeed,
			controller.JumpSpeed,
			frozen,
			_envelope );

		var previousAccepted = _audit.Last;
		_audit = audit;

		if ( decision.Verdict == HexMovementValidator.Verdict.Accepted )
		{
			// Only a completed clean window pays down the violation clock, so a client cannot bank
			// credit by standing still between abusive windows.
			if ( decision.WindowClosed )
				_violationSeconds = Math.Max( 0.0, _violationSeconds - _envelope.AuditWindowSeconds );
			return;
		}

		if ( decision.Verdict == HexMovementValidator.Verdict.Teleport )
		{
			// A single impossible step. Correcting here is precise and the snap is small, so it stays.
			var lastGood = new Vector3( previousAccepted.X, previousAccepted.Y, previousAccepted.Z );
			Log.Info(
				$"HEXAGON_MOVEMENT_TELEPORT connection={HostConnection?.Id} account={PlatformAccountDisplay} " +
				$"reported={position} correctedTo={lastGood} frozen={frozen}" );
			IssueCorrection( lastGood, restock: false );
			_violationSeconds += _envelope.AuditWindowSeconds;
		}
		else
		{
			// A window's travel exceeded the envelope. Deliberately NOT corrected: snapping a player
			// back a window's worth of movement is worse than the abuse, and per-tick snapping is what
			// fed the rubber-banding. Detection escalates to a kick instead.
			Log.Info(
				$"HEXAGON_MOVEMENT_VIOLATION connection={HostConnection?.Id} account={PlatformAccountDisplay} " +
				$"path={decision.HorizontalPath:F1}/{decision.HorizontalBudget:F1} " +
				$"rise={decision.NetRise:F1}/{decision.RiseBudget:F1} frozen={frozen} " +
				$"sustained={_violationSeconds:F1}s" );
			_violationSeconds += _envelope.AuditWindowSeconds;
		}

		if ( _violationSeconds >= _envelope.ViolationKickSeconds ) KickForMovement();
	}

	// A client that keeps reporting out-of-envelope positions is ignoring corrections or forging its
	// transform; drop it. Resetting the score lets a reconnecting client start clean.
	private void KickForMovement()
	{
		_violationSeconds = 0;
		var connection = HostConnection;
		Log.Warning(
			$"HEXAGON_MOVEMENT_KICK connection={connection?.Id} account={PlatformAccountDisplay} character={CharacterGuid}" );
		connection?.Kick( "Movement validation failed repeatedly." );
	}

	private static double MonotonicSeconds() =>
		Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
