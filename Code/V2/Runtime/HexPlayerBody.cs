#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;
using Sandbox;

namespace Hexagon.V2.Runtime;

/// <summary>Host-authored movement state tied to one processed input sequence.</summary>
public readonly record struct PlayerMovementState(
	int BodyGeneration,
	uint ProcessedInputSequence,
	Vector3 Position,
	Vector3 Velocity,
	float Pitch,
	float Yaw,
	bool IsGrounded,
	bool IsDucking )
{
	public bool IsFinite =>
		float.IsFinite( Position.x ) && float.IsFinite( Position.y ) && float.IsFinite( Position.z ) &&
		float.IsFinite( Velocity.x ) && float.IsFinite( Velocity.y ) && float.IsFinite( Velocity.z ) &&
		float.IsFinite( Pitch ) && float.IsFinite( Yaw );
}

/// <summary>
/// Client-owned identity/input shell. The playable body is an unowned network
/// object simulated by the host; an owner-local, never-networked predictor is
/// presentation only and is never exposed to authority callers.
/// </summary>
public sealed class HexPlayerBody : Component, ICameraModifier
{
	private const float InputSendIntervalSeconds = 1f / 30f;
	private const float PredictionSmoothSeconds = 0.100f;
	private const int PredictionHistoryCapacity = 128;

	private readonly PlayerInputAdmission _inputAdmission = new();
	private readonly Queue<PredictedFrameSnapshot> _predictionHistory = new();
	private readonly List<GameObject> _retiredBodies = new();
	private PreparedAuthoritativeBody? _preparedBody;
	private bool _stripRequestedDuringPreparation;
	private Transform _hostSpawnTransform;
	private PlayerInputFrame _latestHostInput;
	private bool _hasLatestHostInput;
	private double _lastAcceptedInputSeconds;
	private uint _lastAppliedJumpSequence;
	private bool _hasAppliedJumpSequence;
	private double _lastJumpSeconds = double.NegativeInfinity;
	private GameObject? _predictedBody;
	private PlayerController? _predictedController;
	private GameObject? _presentedAuthoritativeBody;
	private int _predictedGeneration = -1;
	private uint _nextInputSequence;
	private uint _nextJumpSequence;
	private bool _pendingJump;
	private uint _lastReconciledSequence;
	private float _inputSendAccumulator;
	private Vector3 _smoothCorrection;
	private int _localGeneration = -1;
	private Angles _localEyeAngles;
	private bool _localLookInitialized;
	private bool _localThirdPerson = true;
	private float _cameraDistance = 20f;

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
	[Sync( SyncFlags.FromHost )] public GameObject? AuthoritativeBody { get; private set; }
	[Sync( SyncFlags.FromHost )] public int BodyGeneration { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool HasProcessedInput { get; private set; }
	[Sync( SyncFlags.FromHost )] public uint ProcessedInputSequence { get; private set; }
	[Sync( SyncFlags.FromHost )] public PlayerMovementState ProcessedMovementState { get; private set; }

	internal Connection? HostConnection { get; set; }
	internal Func<HexPlayerBody, PlayerInputFrame, bool>? HostInputAuthenticator { get; set; }

	public bool TryGetUsableAuthoritativeBody( out GameObject body )
	{
		body = AuthoritativeBody!;
		return PlayerBodyAuthorityRules.IsUsable(
			HasActiveCharacter,
			IsDead,
			body is not null && body.IsValid(),
			body is not null && body.IsValid() && body.Enabled );
	}

	/// <summary>
	/// Identifies the engine input controller created by this shell for local-only
	/// prediction. Callers must still submit commands to host authority; this is
	/// only a presentation/input-source check.
	/// </summary>
	public static bool IsLocalPredictionSource( Component? source ) =>
		(source is PlayerController &&
			source.Components.Get<HexLocalPredictionMarker>() is { } marker &&
			marker.IsCurrent( source )) ||
		(source is HexPlayerBody shell && shell.IsOwnerLocal);

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

	/// <summary>
	/// Creates and atomically activates an unowned, host-simulated body.
	/// </summary>
	public OperationResult<GameObject> HostBuildAuthoritativeBody( Action<GameObject> configure )
	{
		var prepared = HostPrepareAuthoritativeBody( configure );
		if ( prepared.Failed )
			return OperationResult<GameObject>.Failure(
				prepared.Error!.Code, prepared.Error.Message, prepared.Error.Details );
		return prepared.Value.TryActivate();
	}

	/// <summary>
	/// Fully configures and network-spawns a disabled replacement while preserving
	/// the current body. The initial transform comes only from host-held state: the
	/// previous canonical body or the original host-selected spawn transform.
	/// </summary>
	public OperationResult<PreparedAuthoritativeBody> HostPrepareAuthoritativeBody( Action<GameObject> configure )
	{
		ArgumentNullException.ThrowIfNull( configure );
		if ( !Sandbox.Networking.IsHost )
			return OperationResult<PreparedAuthoritativeBody>.Failure(
				ErrorCode.Unauthorized, "Only the host can prepare an authoritative body." );
		if ( HostConnection is null )
			return OperationResult<PreparedAuthoritativeBody>.Failure(
				ErrorCode.NotFound, "The player connection is unavailable." );
		if ( _preparedBody is not null )
			return OperationResult<PreparedAuthoritativeBody>.Failure(
				ErrorCode.Conflict, "An authoritative body replacement is already prepared." );

		var previous = AuthoritativeBody is not null && AuthoritativeBody.IsValid()
			? AuthoritativeBody
			: null;
		var trustedTransform = previous?.WorldTransform ?? _hostSpawnTransform;
		var candidate = new GameObject( false, "Hexagon Authoritative Body" )
		{
			WorldTransform = trustedTransform.WithScale( 1 )
		};
		try
		{
			configure( candidate );
			candidate.WorldTransform = trustedTransform.WithScale( 1 );
			var controller = candidate.Components.Get<PlayerController>( FindMode.EverythingInSelf );
			if ( controller is null )
				throw new InvalidOperationException( "An authoritative body requires a PlayerController." );
			controller.UseInputControls = false;
			controller.UseLookControls = false;
			controller.UseCameraControls = false;
			controller.EnablePressing = false;
			var spawned = candidate.NetworkSpawn( new NetworkSpawnOptions
			{
				Owner = null!,
				StartEnabled = false,
				OwnerTransfer = OwnerTransfer.Fixed,
				OrphanedMode = NetworkOrphaned.Destroy
			} );
			if ( !spawned )
			{
				DestroyOrRetain( candidate, "rejected replacement" );
				return OperationResult<PreparedAuthoritativeBody>.Failure(
					ErrorCode.InternalError, "The authoritative body could not be network-spawned." );
			}
			var prepared = new PreparedAuthoritativeBody( this, candidate, previous );
			_preparedBody = prepared;
			return OperationResult<PreparedAuthoritativeBody>.Success( prepared );
		}
		catch ( Exception exception )
		{
			DestroyOrRetain( candidate, "failed replacement" );
			Log.Error( exception, "Hexagon schema failed to compose an authoritative body." );
			return OperationResult<PreparedAuthoritativeBody>.Failure(
				ErrorCode.InternalError, "The authoritative body could not be composed." );
		}
	}

	public OperationResult HostStripAuthoritativeBody()
	{
		if ( !Sandbox.Networking.IsHost )
			return OperationResult.Failure( ErrorCode.Unauthorized, "Only the host can strip an authoritative body." );
		if ( _preparedBody is not null )
		{
			_stripRequestedDuringPreparation = true;
			try
			{
				if ( AuthoritativeBody is not null && AuthoritativeBody.IsValid() )
				{
					AuthoritativeBody.Enabled = false;
					AuthoritativeBody.Network.Refresh();
				}
				return OperationResult.Success();
			}
			catch ( Exception exception )
			{
				Log.Error( exception, "Hexagon could not disable an authoritative body while replacement was pending." );
				return OperationResult.Failure(
					ErrorCode.InternalError, "The authoritative body could not be disabled while replacement was pending." );
			}
		}
		return DestroyAuthoritativeBody();
	}

	protected override void OnUpdate()
	{
		if ( IsOwnerLocal )
		{
			EnsureLocalGeneration();
			UpdateLocalPresentationInput();
			if ( Sandbox.Networking.IsHost ) UpdateListenHostInteraction();
		}
		UpdatePredictionPresentation();
	}

	protected override void OnFixedUpdate()
	{
		if ( Sandbox.Networking.IsHost )
		{
			CleanupRetiredBodies();
			PublishCompletedHostMovement();
			ApplyHostInput();
		}
		if ( IsOwnerLocal )
		{
			EnsureLocalGeneration();
			if ( Sandbox.Networking.IsHost )
			{
				if ( _predictedBody is not null ) DestroyPredictedBody();
			}
			else EnsurePredictedBody();
			CaptureAndSendInput();
			if ( !Sandbox.Networking.IsHost )
			{
				ReconcilePrediction();
				ApplySmoothCorrection();
			}
		}
		else if ( _predictedBody is not null )
		{
			DestroyPredictedBody();
		}
	}

	[Rpc.Host( NetFlags.OwnerOnly | NetFlags.UnreliableNoDelay )]
	private void SubmitInputFrame( PlayerInputFrame frame )
	{
		var now = MonotonicSeconds();
		if ( !_inputAdmission.TryBeginAttempt( now, out _ ) ) return;
		if ( !Sandbox.Networking.IsHost || HostConnection is null || Rpc.Caller.Id != HostConnection.Id ) return;
		if ( HostInputAuthenticator is null || !HostInputAuthenticator( this, frame ) ) return;
		if ( !TryGetUsableAuthoritativeBody( out _ ) ) return;
		if ( !_inputAdmission.TryAcceptCharged(
			frame, BodyGeneration, out var accepted, out _ ) ) return;
		_latestHostInput = accepted;
		_hasLatestHostInput = true;
		_lastAcceptedInputSeconds = now;
	}

	private void ApplyHostInput()
	{
		if ( !TryGetUsableAuthoritativeBody( out var body ) )
		{
			if ( AuthoritativeBody is { } unavailable && unavailable.IsValid() &&
				unavailable.Components.Get<PlayerController>() is { } unavailableController )
				unavailableController.WishVelocity = Vector3.Zero;
			return;
		}
		var controller = body.Components.Get<PlayerController>();
		if ( controller is null ) return;
		var now = MonotonicSeconds();
		if ( !PlayerInputAuthorityRules.IsInputFresh(
			_hasLatestHostInput, _lastAcceptedInputSeconds, now ) )
		{
			controller.WishVelocity = Vector3.Zero;
			return;
		}

		var input = _latestHostInput;
		controller.EyeAngles = new Angles( input.Pitch, input.Yaw, 0 );
		var wantsDuck = input.Buttons.HasFlag( PlayerInputButtons.Duck );
		controller.UpdateDucking( wantsDuck );
		var wantsRun = input.Buttons.HasFlag( PlayerInputButtons.Run );
		if ( controller.RunByDefault ) wantsRun = !wantsRun;
		var speed = wantsDuck
			? controller.DuckedSpeed
			: wantsRun ? controller.RunSpeed : controller.WalkSpeed;
		var localMove = new Vector3( input.MoveX, input.MoveY, 0 );
		controller.WishVelocity = Rotation.FromYaw( input.Yaw ) * localMove * speed;

		if ( input.Buttons.HasFlag( PlayerInputButtons.Jump ) &&
			(!_hasAppliedJumpSequence || PlayerInputAdmission.IsNewer( input.JumpSequence, _lastAppliedJumpSequence )) )
		{
			_lastAppliedJumpSequence = input.JumpSequence;
			_hasAppliedJumpSequence = true;
			if ( PlayerInputAuthorityRules.CanJump(
				controller.IsOnGround, controller.JumpSpeed, _lastJumpSeconds, now ) )
			{
				_lastJumpSeconds = now;
				controller.Jump( Vector3.Up * controller.JumpSpeed );
			}
		}
		ProcessedInputSequence = input.Sequence;
		HasProcessedInput = true;
	}

	private void PublishCompletedHostMovement()
	{
		if ( !HasProcessedInput || !TryGetUsableAuthoritativeBody( out var body ) ) return;
		var controller = body.Components.Get<PlayerController>();
		if ( controller is null ) return;
		var eye = controller.EyeAngles;
		var snapshot = new PlayerMovementState(
			BodyGeneration,
			ProcessedInputSequence,
			body.WorldPosition,
			controller.Velocity,
			eye.pitch,
			PlayerInputAdmission.NormalizeYaw( eye.yaw ),
			controller.IsOnGround,
			controller.IsDucking );
		if ( snapshot.IsFinite ) ProcessedMovementState = snapshot;
	}

	private void EnsurePredictedBody()
	{
		if ( Sandbox.Networking.IsHost )
		{
			DestroyPredictedBody();
			return;
		}
		var authority = AuthoritativeBody;
		if ( authority is null || !authority.IsValid() || !authority.Enabled || !HasActiveCharacter || IsDead )
		{
			DestroyPredictedBody();
			return;
		}
		if ( _predictedBody is not null && _predictedBody.IsValid() &&
			_predictedGeneration == BodyGeneration ) return;

		DestroyPredictedBody();
		var predictor = new GameObject( false, "Hexagon Predicted Body" )
		{
			NetworkMode = NetworkMode.Never,
			WorldTransform = authority.WorldTransform.WithScale( 1 )
		};
		predictor.Tags.Add( "prediction" );
		var controller = predictor.AddComponent<PlayerController>();
		controller.BodyCollisionTags = new TagSet();
		controller.BodyCollisionTags.Add( "prediction" );
		controller.CameraCollisionIgnore.Add( "playerclip" );
		controller.UseCameraControls = false;
		controller.UseAnimatorControls = false;
		controller.EnableFootstepSounds = false;
		predictor.AddComponent<HexLocalPredictionMarker>().Bind( this, BodyGeneration );
		var authoritativeController = authority.Components.Get<PlayerController>();
		if ( authoritativeController is not null ) controller.EyeAngles = authoritativeController.EyeAngles;
		predictor.Enabled = true;
		_predictedBody = predictor;
		_predictedController = controller;
		_predictedGeneration = BodyGeneration;
	}

	private void CaptureAndSendInput()
	{
		if ( !TryGetUsableAuthoritativeBody( out _ ) ) return;
		if ( Input.Pressed( "Jump" ) )
		{
			_nextJumpSequence++;
			_pendingJump = true;
		}
		_inputSendAccumulator += Math.Clamp( Time.Delta, 0f, 0.1f );
		if ( _inputSendAccumulator < InputSendIntervalSeconds ) return;
		_inputSendAccumulator %= InputSendIntervalSeconds;

		var move = Input.AnalogMove.ClampLength( 1 );
		var buttons = PlayerInputButtons.None;
		if ( Input.Down( "run" ) ) buttons |= PlayerInputButtons.Run;
		if ( Input.Down( "duck" ) ) buttons |= PlayerInputButtons.Duck;
		if ( _pendingJump ) buttons |= PlayerInputButtons.Jump;
		var eye = _predictedController is not null && _predictedController.IsValid()
			? _predictedController.EyeAngles
			: _localEyeAngles;
		var frame = new PlayerInputFrame(
			BodyGeneration,
			++_nextInputSequence,
			move.x,
			move.y,
			Math.Clamp( eye.pitch, -90f, 90f ),
			PlayerInputAdmission.NormalizeYaw( eye.yaw ),
			buttons,
			_nextJumpSequence );
		SubmitInputFrame( frame );
		_pendingJump = false;
		if ( _predictedBody is not null && _predictedBody.IsValid() )
		{
			_predictionHistory.Enqueue( new PredictedFrameSnapshot( frame.Sequence, _predictedBody.WorldPosition ) );
			while ( _predictionHistory.Count > PredictionHistoryCapacity ) _predictionHistory.Dequeue();
		}
	}

	private void ReconcilePrediction()
	{
		var movement = ProcessedMovementState;
		if ( !HasProcessedInput || movement.BodyGeneration != BodyGeneration ||
			movement.ProcessedInputSequence == _lastReconciledSequence || !movement.IsFinite ||
			_predictedBody is null || !_predictedBody.IsValid() ||
			AuthoritativeBody is null || !AuthoritativeBody.IsValid() ) return;
		_lastReconciledSequence = movement.ProcessedInputSequence;
		var found = false;
		var acknowledgedPrediction = Vector3.Zero;
		while ( _predictionHistory.Count > 0 )
		{
			var sample = _predictionHistory.Peek();
			if ( sample.Sequence == movement.ProcessedInputSequence )
			{
				found = true;
				acknowledgedPrediction = sample.Position;
				_predictionHistory.Dequeue();
				break;
			}
			if ( PlayerInputAdmission.IsNewer( sample.Sequence, movement.ProcessedInputSequence ) ) break;
			_predictionHistory.Dequeue();
		}

		var current = _predictedBody.WorldPosition;
		var plan = PredictionReconciliation.Calculate(
			found,
			new PredictionPosition( movement.Position.x, movement.Position.y, movement.Position.z ),
			new PredictionPosition( acknowledgedPrediction.x, acknowledgedPrediction.y, acknowledgedPrediction.z ),
			new PredictionPosition( current.x, current.y, current.z ) );
		var correction = new Vector3( plan.Delta.X, plan.Delta.Y, plan.Delta.Z );
		switch ( plan.Kind )
		{
			case PredictionCorrectionKind.Hard:
				_predictedBody.WorldPosition = found
					? _predictedBody.WorldPosition + correction
					: movement.Position;
				_predictedBody.Transform.ClearInterpolation();
				_smoothCorrection = Vector3.Zero;
				break;
			case PredictionCorrectionKind.Smooth:
				_smoothCorrection = correction;
				break;
			case PredictionCorrectionKind.Ignore:
				_smoothCorrection = Vector3.Zero;
				break;
		}
	}

	private void ApplySmoothCorrection()
	{
		if ( _predictedBody is null || !_predictedBody.IsValid() || _smoothCorrection.IsNearlyZero() ) return;
		var fraction = Math.Clamp( Time.Delta / PredictionSmoothSeconds, 0f, 1f );
		var delta = _smoothCorrection * fraction;
		_predictedBody.WorldPosition += delta;
		_smoothCorrection -= delta;
	}

	private void UpdatePredictionPresentation()
	{
		var authority = AuthoritativeBody;
		if ( _presentedAuthoritativeBody is not null &&
			(!ReferenceEquals( _presentedAuthoritativeBody, authority ) || !IsOwnerLocal) )
		{
			if ( _presentedAuthoritativeBody.IsValid() ) _presentedAuthoritativeBody.Tags.Remove( "viewer" );
			_presentedAuthoritativeBody = null;
		}
		if ( !TryGetUsableAuthoritativeBody( out authority ) || !IsOwnerLocal ) return;
		_presentedAuthoritativeBody = authority;
		if ( _localThirdPerson ) authority.Tags.Remove( "viewer" );
		else authority.Tags.Add( "viewer" );
	}

	private void EnsureLocalGeneration()
	{
		if ( _localGeneration == BodyGeneration ) return;
		_localGeneration = BodyGeneration;
		_nextInputSequence = 0;
		_nextJumpSequence = 0;
		_pendingJump = false;
		_lastReconciledSequence = 0;
		_inputSendAccumulator = InputSendIntervalSeconds;
		_predictionHistory.Clear();
		_smoothCorrection = Vector3.Zero;
		_localLookInitialized = false;
		if ( AuthoritativeBody is { } body && body.IsValid() &&
			body.Components.Get<PlayerController>() is { } controller )
		{
			_localEyeAngles = controller.EyeAngles;
			_localLookInitialized = true;
		}
	}

	private void UpdateLocalPresentationInput()
	{
		if ( !TryGetUsableAuthoritativeBody( out var body ) ) return;
		if ( Sandbox.Networking.IsHost )
		{
			if ( !_localLookInitialized && body.Components.Get<PlayerController>() is { } controller )
			{
				_localEyeAngles = controller.EyeAngles;
				_localLookInitialized = true;
			}
			_localEyeAngles += Input.AnalogLook;
			_localEyeAngles.pitch = Math.Clamp( _localEyeAngles.pitch, -90f, 90f );
			_localEyeAngles.yaw = PlayerInputAdmission.NormalizeYaw( _localEyeAngles.yaw );
			_localEyeAngles.roll = 0;
		}
		else if ( _predictedController is not null && _predictedController.IsValid() )
		{
			_localEyeAngles = _predictedController.EyeAngles;
			_localLookInitialized = true;
		}
		if ( Input.Pressed( "view" ) )
		{
			_localThirdPerson = !_localThirdPerson;
			_cameraDistance = 20f;
		}
	}

	private void UpdateListenHostInteraction()
	{
		if ( !Input.Pressed( "use" ) || !TryGetUsableAuthoritativeBody( out var body ) ) return;
		var controller = body.Components.Get<PlayerController>();
		var origin = controller?.EyePosition ?? body.WorldPosition + Vector3.Up * 64f;
		var ray = new Ray( origin, _localEyeAngles.ToRotation().Forward );
		var trace = Scene.Trace.Ray( ray, 130f )
			.IgnoreGameObjectHierarchy( body.Root )
			.WithoutTags( "prediction" )
			.Run();
		if ( trace.GameObject is null ) return;
		foreach ( var pressable in trace.GameObject.GetComponentsInParent<IPressable>( includeSelf: true ) )
		{
			var pressEvent = new IPressable.Event { Ray = ray, Source = this };
			if ( !pressable.CanPress( pressEvent ) ) continue;
			_ = pressable.Press( pressEvent );
			break;
		}
	}

	int ICameraModifier.CameraOrder => 0;

	void ICameraModifier.ModifyCamera( CameraComponent camera, ref CameraView view )
	{
		if ( !IsOwnerLocal || Scene.Camera != camera || !TryGetUsableAuthoritativeBody( out var body ) ) return;
		var controller = body.Components.Get<PlayerController>();
		var eyePosition = controller?.EyePosition ?? body.WorldPosition + Vector3.Up * 64f;
		var rotation = _localEyeAngles.ToRotation();
		camera.RenderExcludeTags.Add( "viewer" );
		if ( _localThirdPerson )
		{
			var cameraDelta = rotation.Forward * -256f + rotation.Up * 12f;
			var cameraTrace = Scene.Trace.FromTo( eyePosition, eyePosition + cameraDelta )
				.IgnoreGameObjectHierarchy( body.Root )
				.WithoutTags( "prediction" )
				.Radius( 8 )
				.Run();
			if ( cameraTrace.StartedSolid )
				_cameraDistance = _cameraDistance.LerpTo( cameraDelta.Length, Time.Delta * 100f );
			else if ( cameraTrace.Distance < _cameraDistance )
				_cameraDistance = _cameraDistance.LerpTo( cameraTrace.Distance, Time.Delta * 200f );
			else
				_cameraDistance = _cameraDistance.LerpTo( cameraTrace.Distance, Time.Delta * 2f );
			eyePosition += cameraDelta.Normal * _cameraDistance;
		}
		view.Position = eyePosition;
		view.Rotation = rotation;
		view.FieldOfView = Preferences.FieldOfView;
	}

	private void DestroyOrRetain( GameObject? body, string reason )
	{
		if ( body is null || !body.IsValid() ) return;
		try { body.Destroy(); }
		catch ( Exception exception )
		{
			Log.Error( exception, $"Hexagon could not destroy a {reason} authoritative body; cleanup will retry." );
		}
		if ( body.IsValid() && !_retiredBodies.Contains( body ) ) _retiredBodies.Add( body );
	}

	private void CleanupRetiredBodies()
	{
		for ( var index = _retiredBodies.Count - 1; index >= 0; index-- )
		{
			var body = _retiredBodies[index];
			var retry = AuthoritativeBodyRetirement.Retire(
				() => body.IsValid(),
				() =>
				{
					body.Enabled = false;
					body.Network.Refresh();
				},
				body.Destroy,
				() => { } );
			foreach ( var failure in retry.Failures )
				Log.Error( failure, "Hexagon retired authoritative body cleanup retry was degraded." );
			if ( retry.IsClean ) _retiredBodies.RemoveAt( index );
		}
	}

	private OperationResult DestroyAuthoritativeBody()
	{
		var previous = AuthoritativeBody;
		AuthoritativeBodyRetirementResult? retirement = null;
		if ( previous is not null )
		{
			retirement = AuthoritativeBodyRetirement.Retire(
				() => previous.IsValid(),
				() =>
				{
					previous.Enabled = false;
					previous.Network.Refresh();
				},
				previous.Destroy,
				() =>
				{
					if ( !_retiredBodies.Contains( previous ) ) _retiredBodies.Add( previous );
				} );
			foreach ( var failure in retirement.Failures )
				Log.Error( failure, "Hexagon authoritative body strip cleanup was degraded." );
		}
		AuthoritativeBody = null;
		AdvanceBodyGeneration();
		ResetHostInput();
		Exception? publicationFailure = null;
		try { GameObject.Network.Refresh(); }
		catch ( Exception exception ) { publicationFailure = exception; }
		if ( retirement?.IsClean != false && publicationFailure is null ) return OperationResult.Success();
		if ( publicationFailure is not null )
			Log.Error( publicationFailure, "Hexagon could not publish authoritative body removal." );
		return OperationResult.Failure(
			ErrorCode.InternalError,
			retirement?.Retained == true
				? "The authoritative body was disabled and retained for cleanup retry."
				: "The authoritative body could not be fully destroyed." );
	}

	private void DestroyPredictedBody()
	{
		if ( _presentedAuthoritativeBody is not null && _presentedAuthoritativeBody.IsValid() )
			_presentedAuthoritativeBody.Tags.Remove( "viewer" );
		_presentedAuthoritativeBody = null;
		if ( _predictedBody is not null && _predictedBody.IsValid() ) _predictedBody.Destroy();
		_predictedBody = null;
		_predictedController = null;
		_predictedGeneration = -1;
		_predictionHistory.Clear();
		_smoothCorrection = Vector3.Zero;
	}

	private void AdvanceBodyGeneration()
	{
		BodyGeneration = BodyGeneration == int.MaxValue ? 1 : BodyGeneration + 1;
		HasProcessedInput = false;
		ProcessedInputSequence = 0;
		ProcessedMovementState = default;
	}

	private void ResetHostInput()
	{
		_inputAdmission.Reset();
		HasProcessedInput = false;
		_latestHostInput = default;
		_hasLatestHostInput = false;
		_lastAcceptedInputSeconds = 0;
		_lastAppliedJumpSequence = 0;
		_hasAppliedJumpSequence = false;
		_lastJumpSeconds = double.NegativeInfinity;
	}

	private static double MonotonicSeconds() =>
		Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

	protected override void OnDestroy()
	{
		DestroyPredictedBody();
		HostInputAuthenticator = null;
		if ( Sandbox.Networking.IsHost )
		{
			_preparedBody?.Dispose();
			_preparedBody = null;
			_stripRequestedDuringPreparation = false;
			_ = DestroyAuthoritativeBody();
			CleanupRetiredBodies();
		}
		base.OnDestroy();
	}

	public sealed class PreparedAuthoritativeBody : IDisposable
	{
		private readonly HexPlayerBody _owner;
		private readonly GameObject _candidate;
		private readonly GameObject? _previous;
		private bool _completed;
		private bool _activated;
		private OperationError? _failure;

		internal PreparedAuthoritativeBody( HexPlayerBody owner, GameObject candidate, GameObject? previous )
		{
			_owner = owner;
			_candidate = candidate;
			_previous = previous;
		}

		public OperationResult<GameObject> TryActivate()
		{
			if ( _completed )
			{
				if ( _activated && ReferenceEquals( _owner.AuthoritativeBody, _candidate ) && _candidate.IsValid() )
					return OperationResult<GameObject>.Success( _candidate );
				return OperationResult<GameObject>.Failure(
					_failure?.Code ?? ErrorCode.Conflict,
					_failure?.Message ?? "The authoritative body replacement is no longer active.",
					_failure?.Details );
			}
			if ( _owner._stripRequestedDuringPreparation )
			{
				Abort( completeDeferredStrip: true );
				return OperationResult<GameObject>.Failure(
					ErrorCode.Conflict, "The authoritative body replacement was invalidated before activation." );
			}

			_completed = true;
			_owner._preparedBody = null;
			_owner._stripRequestedDuringPreparation = false;
			var previousGeneration = _owner.BodyGeneration;
			var previousHasProcessedInput = _owner.HasProcessedInput;
			var previousProcessedSequence = _owner.ProcessedInputSequence;
			var previousMovement = _owner.ProcessedMovementState;
			var activated = AuthoritativeBodyReplacement.RequireActivated(
				_candidate,
				activateCandidate: () =>
				{
					_candidate.Enabled = true;
					_candidate.Network.Refresh();
					return _candidate.IsValid() && _candidate.Enabled;
				},
				quiescePrevious: () =>
				{
					if ( _previous is null || !_previous.IsValid() ) return true;
					_previous.Enabled = false;
					_previous.Network.Refresh();
					return !_previous.Enabled;
				},
				publishCandidate: () =>
				{
					_owner.AuthoritativeBody = _candidate;
					_owner.AdvanceBodyGeneration();
					var controller = _candidate.Components.Get<PlayerController>();
					_owner.ProcessedMovementState = new PlayerMovementState(
						_owner.BodyGeneration,
						0,
						_candidate.WorldPosition,
						controller?.Velocity ?? Vector3.Zero,
						controller?.EyeAngles.pitch ?? 0,
						controller?.EyeAngles.yaw ?? 0,
						controller?.IsOnGround ?? false,
						controller?.IsDucking ?? false );
					_owner.GameObject.Network.Refresh();
					return ReferenceEquals( _owner.AuthoritativeBody, _candidate ) && _candidate.Enabled;
				},
				rollbackPublication: () =>
				{
					_owner.AuthoritativeBody = _previous;
					_owner.BodyGeneration = previousGeneration;
					_owner.HasProcessedInput = previousHasProcessedInput;
					_owner.ProcessedInputSequence = previousProcessedSequence;
					_owner.ProcessedMovementState = previousMovement;
					_owner.GameObject.Network.Refresh();
				},
				restorePrevious: () =>
				{
					if ( _previous is null || !_previous.IsValid() ) return;
					_previous.Enabled = true;
					_previous.Network.Refresh();
				},
				discardCandidate: () =>
				{
					try
					{
						if ( _candidate.IsValid() )
						{
							_candidate.Enabled = false;
							_candidate.Network.Refresh();
						}
					}
					catch ( Exception exception )
					{
						Log.Error( exception, "Hexagon could not disable a failed authoritative body candidate." );
					}
					_owner.DestroyOrRetain( _candidate, "failed replacement" );
				},
				retirePrevious: () => _owner.DestroyOrRetain( _previous, "retired" ) );
			if ( activated.Failed )
			{
				_failure = activated.Error;
				Log.Error( $"Hexagon could not atomically activate a prepared authoritative body: {_failure!.Message}" );
				return activated;
			}
			_activated = true;
			_owner.ResetHostInput();
			return activated;
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
			_failure = new OperationError(
				ErrorCode.Conflict, "The authoritative body replacement was aborted." );
			_owner.DestroyOrRetain( _candidate, "aborted replacement" );
			if ( completeDeferredStrip ) _ = _owner.DestroyAuthoritativeBody();
		}
	}

	private readonly record struct PredictedFrameSnapshot( uint Sequence, Vector3 Position );
}

public sealed class HexLocalPredictionMarker : Component
{
	private HexPlayerBody? _owner;
	private int _generation;

	internal void Bind( HexPlayerBody owner, int generation )
	{
		_owner = owner;
		_generation = generation;
	}

	internal bool IsCurrent( Component source ) =>
		_owner is not null && _owner.IsValid() &&
		_owner.GameObject.Network.Active && _owner.GameObject.Network.IsOwner &&
		_owner.BodyGeneration == _generation &&
		ReferenceEquals( source.GameObject, GameObject );
}
