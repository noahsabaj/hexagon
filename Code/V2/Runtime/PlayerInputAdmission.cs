#nullable enable

using System;

namespace Hexagon.V2.Runtime;

[Flags]
public enum PlayerInputButtons : byte
{
	None = 0,
	Run = 1 << 0,
	Duck = 1 << 1,
	Jump = 1 << 2
}

/// <summary>
/// Fixed-size, untrusted client movement sample. The host validates every field
/// before it can influence the canonical player controller.
/// </summary>
public readonly record struct PlayerInputFrame(
	int BodyGeneration,
	uint Sequence,
	float MoveX,
	float MoveY,
	float Pitch,
	float Yaw,
	PlayerInputButtons Buttons,
	uint JumpSequence );

public enum PlayerInputAdmissionFailure
{
	None = 0,
	RateLimited = 1,
	StaleBodyGeneration = 2,
	Replay = 3,
	Malformed = 4,
	InvalidButtons = 5,
	InvalidMove = 6,
	InvalidPitch = 7,
	StaleJumpSequence = 8
}

public static class PlayerInputAuthorityRules
{
	public const double IdleTimeoutSeconds = 0.250;
	public const double JumpCooldownSeconds = 0.500;

	public static bool IsInputFresh( bool hasInput, double acceptedAtSeconds, double nowSeconds ) =>
		hasInput && double.IsFinite( acceptedAtSeconds ) && double.IsFinite( nowSeconds ) &&
		nowSeconds >= acceptedAtSeconds && nowSeconds - acceptedAtSeconds <= IdleTimeoutSeconds;

	public static bool CanJump(
		bool isGrounded,
		float jumpSpeed,
		double lastJumpAtSeconds,
		double nowSeconds ) =>
		isGrounded && jumpSpeed > 0 && double.IsFinite( nowSeconds ) &&
		nowSeconds - lastJumpAtSeconds >= JumpCooldownSeconds;
}

public static class PlayerBodyAuthorityRules
{
	public static bool IsUsable(
		bool hasActiveCharacter,
		bool isDead,
		bool bodyIsValid,
		bool bodyIsEnabled ) =>
		hasActiveCharacter && !isDead && bodyIsValid && bodyIsEnabled;
}

public readonly record struct PlayerInputAuthenticationState(
	bool SessionExists,
	bool ExactPlayerInstance,
	bool ApplicationConnected,
	Guid CanonicalCharacterId,
	Guid ShellCharacterId,
	int CurrentBodyGeneration,
	int FrameBodyGeneration,
	bool BodyUsable );

public static class PlayerInputSessionAuthentication
{
	public static bool IsAuthorized( PlayerInputAuthenticationState state ) =>
		state.SessionExists && state.ExactPlayerInstance && state.ApplicationConnected &&
		state.CanonicalCharacterId != Guid.Empty &&
		state.CanonicalCharacterId == state.ShellCharacterId &&
		state.CurrentBodyGeneration == state.FrameBodyGeneration && state.BodyUsable;
}

public enum PredictionCorrectionKind
{
	Ignore = 0,
	Smooth = 1,
	Hard = 2
}

public readonly record struct PredictionPosition( float X, float Y, float Z )
{
	public bool IsFinite => float.IsFinite( X ) && float.IsFinite( Y ) && float.IsFinite( Z );
}

public readonly record struct PredictionCorrectionPlan(
	PredictionCorrectionKind Kind,
	PredictionPosition Delta );

public static class PredictionReconciliation
{
	public const float IgnoreDistance = 2f;
	public const float HardCorrectionDistance = 48f;

	public static PredictionCorrectionKind Decide( bool historyFound, float errorDistance )
	{
		if ( !historyFound || !float.IsFinite( errorDistance ) || errorDistance < 0 ||
			errorDistance > HardCorrectionDistance ) return PredictionCorrectionKind.Hard;
		return errorDistance > IgnoreDistance
			? PredictionCorrectionKind.Smooth
			: PredictionCorrectionKind.Ignore;
	}

	/// <summary>
	/// Computes correction against the predicted position captured for the exact
	/// processed input sequence. When history is present, applying the returned
	/// delta to the current predictor preserves motion simulated after that input.
	/// Missing history hard-corrects current prediction to the authoritative state.
	/// </summary>
	public static PredictionCorrectionPlan Calculate(
		bool historyFound,
		PredictionPosition authoritativeAtSequence,
		PredictionPosition predictedAtSequence,
		PredictionPosition currentPrediction )
	{
		if ( !authoritativeAtSequence.IsFinite || !currentPrediction.IsFinite ||
			(historyFound && !predictedAtSequence.IsFinite) )
			return new PredictionCorrectionPlan(
				PredictionCorrectionKind.Hard,
				new PredictionPosition( 0, 0, 0 ) );

		var baseline = historyFound ? predictedAtSequence : currentPrediction;
		var delta = new PredictionPosition(
			authoritativeAtSequence.X - baseline.X,
			authoritativeAtSequence.Y - baseline.Y,
			authoritativeAtSequence.Z - baseline.Z );
		var distance = MathF.Sqrt(
			delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z );
		return new PredictionCorrectionPlan( Decide( historyFound, distance ), delta );
	}
}

/// <summary>
/// Deterministic per-session admission boundary. Tokens are consumed before
/// validation so malformed traffic cannot evade the rate limit.
/// </summary>
public sealed class PlayerInputAdmission
{
	public const double DefaultRatePerSecond = 60.0;
	public const double DefaultBurst = 15.0;
	private const PlayerInputButtons KnownButtons =
		PlayerInputButtons.Run | PlayerInputButtons.Duck | PlayerInputButtons.Jump;

	private readonly double _ratePerSecond;
	private readonly double _burst;
	private double _tokens;
	private double _lastRefillSeconds;
	private bool _hasClock;
	private bool _hasAcceptedSequence;
	private uint _lastAcceptedSequence;
	private bool _hasAcceptedJumpSequence;
	private uint _lastAcceptedJumpSequence;

	public PlayerInputAdmission(
		double ratePerSecond = DefaultRatePerSecond,
		double burst = DefaultBurst )
	{
		if ( !double.IsFinite( ratePerSecond ) || ratePerSecond <= 0 )
			throw new ArgumentOutOfRangeException( nameof(ratePerSecond) );
		if ( !double.IsFinite( burst ) || burst < 1 )
			throw new ArgumentOutOfRangeException( nameof(burst) );
		_ratePerSecond = ratePerSecond;
		_burst = burst;
		_tokens = burst;
	}

	public bool TryAccept(
		PlayerInputFrame frame,
		int expectedBodyGeneration,
		double nowSeconds,
		out PlayerInputFrame accepted,
		out PlayerInputAdmissionFailure failure )
	{
		if ( !TryBeginAttempt( nowSeconds, out failure ) )
		{
			accepted = default;
			return false;
		}
		return TryAcceptCharged( frame, expectedBodyGeneration, out accepted, out failure );
	}

	/// <summary>Charges one attempt before caller/session/body validation.</summary>
	public bool TryBeginAttempt( double nowSeconds, out PlayerInputAdmissionFailure failure )
	{
		if ( TryCharge( nowSeconds ) )
		{
			failure = PlayerInputAdmissionFailure.None;
			return true;
		}
		failure = PlayerInputAdmissionFailure.RateLimited;
		return false;
	}

	internal bool TryAcceptCharged(
		PlayerInputFrame frame,
		int expectedBodyGeneration,
		out PlayerInputFrame accepted,
		out PlayerInputAdmissionFailure failure )
	{
		accepted = default;
		if ( frame.BodyGeneration != expectedBodyGeneration )
		{
			failure = PlayerInputAdmissionFailure.StaleBodyGeneration;
			return false;
		}
		if ( _hasAcceptedSequence && !IsNewer( frame.Sequence, _lastAcceptedSequence ) )
		{
			failure = PlayerInputAdmissionFailure.Replay;
			return false;
		}
		if ( !float.IsFinite( frame.MoveX ) || !float.IsFinite( frame.MoveY ) ||
			!float.IsFinite( frame.Pitch ) || !float.IsFinite( frame.Yaw ) )
		{
			failure = PlayerInputAdmissionFailure.Malformed;
			return false;
		}
		if ( (frame.Buttons & ~KnownButtons) != 0 )
		{
			failure = PlayerInputAdmissionFailure.InvalidButtons;
			return false;
		}
		var moveLengthSquared = frame.MoveX * frame.MoveX + frame.MoveY * frame.MoveY;
		if ( moveLengthSquared > 1.0002f )
		{
			failure = PlayerInputAdmissionFailure.InvalidMove;
			return false;
		}
		if ( frame.Pitch is < -90f or > 90f )
		{
			failure = PlayerInputAdmissionFailure.InvalidPitch;
			return false;
		}
		if ( _hasAcceptedJumpSequence && frame.JumpSequence != _lastAcceptedJumpSequence &&
			!IsNewer( frame.JumpSequence, _lastAcceptedJumpSequence ) )
		{
			failure = PlayerInputAdmissionFailure.StaleJumpSequence;
			return false;
		}

		accepted = frame with { Yaw = NormalizeYaw( frame.Yaw ) };
		_lastAcceptedSequence = frame.Sequence;
		_hasAcceptedSequence = true;
		_lastAcceptedJumpSequence = frame.JumpSequence;
		_hasAcceptedJumpSequence = true;
		failure = PlayerInputAdmissionFailure.None;
		return true;
	}

	public void Reset()
	{
		_tokens = _burst;
		_hasClock = false;
		_hasAcceptedSequence = false;
		_lastAcceptedSequence = 0;
		_hasAcceptedJumpSequence = false;
		_lastAcceptedJumpSequence = 0;
	}

	public static bool IsNewer( uint candidate, uint current ) =>
		candidate != current && unchecked((int)(candidate - current)) > 0;

	public static float NormalizeYaw( float yaw )
	{
		var normalized = yaw % 360f;
		if ( normalized >= 180f ) normalized -= 360f;
		if ( normalized < -180f ) normalized += 360f;
		return normalized;
	}

	private bool TryCharge( double nowSeconds )
	{
		if ( !double.IsFinite( nowSeconds ) ) return false;
		if ( !_hasClock )
		{
			_lastRefillSeconds = nowSeconds;
			_hasClock = true;
		}
		else if ( nowSeconds > _lastRefillSeconds )
		{
			_tokens = Math.Min( _burst, _tokens + (nowSeconds - _lastRefillSeconds) * _ratePerSecond );
			_lastRefillSeconds = nowSeconds;
		}
		if ( _tokens < 1.0 ) return false;
		_tokens -= 1.0;
		return true;
	}
}
