#nullable enable

using Hexagon.V2.Runtime;

namespace Hexagon.V2.Tests.Runtime;

[TestClass]
public sealed class PlayerInputAdmissionTests
{
	[TestMethod]
	public void AcceptsAndNormalizesWellFormedInput()
	{
		var admission = new PlayerInputAdmission();
		var frame = Frame( sequence: 1 ) with { Yaw = 540f };

		Assert.IsTrue( admission.TryAccept( frame, 7, 10, out var accepted, out var failure ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.None, failure );
		Assert.AreEqual( -180f, accepted.Yaw );
	}

	[TestMethod]
	public void RejectsWrongGenerationReplayMalformedAndOversizedMove()
	{
		var admission = new PlayerInputAdmission();
		Assert.IsFalse( admission.TryAccept( Frame( 1 ) with { BodyGeneration = 6 }, 7, 1, out _, out var generation ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.StaleBodyGeneration, generation );
		Assert.IsTrue( admission.TryAccept( Frame( 1 ), 7, 1, out _, out _ ) );
		Assert.IsFalse( admission.TryAccept( Frame( 1 ), 7, 1, out _, out var replay ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.Replay, replay );
		Assert.IsFalse( admission.TryAccept( Frame( 2 ) with { Pitch = float.NaN }, 7, 1, out _, out var malformed ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.Malformed, malformed );
		Assert.IsFalse( admission.TryAccept( Frame( 3 ) with { MoveX = 1f, MoveY = 1f }, 7, 1, out _, out var move ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.InvalidMove, move );
	}

	[TestMethod]
	public void InvalidAttemptsConsumeTheBurstBudget()
	{
		var admission = new PlayerInputAdmission( ratePerSecond: 1, burst: 2 );
		Assert.IsFalse( admission.TryAccept( Frame( 1 ) with { Pitch = float.NaN }, 7, 0, out _, out _ ) );
		Assert.IsFalse( admission.TryAccept( Frame( 2 ) with { Pitch = float.NaN }, 7, 0, out _, out _ ) );
		Assert.IsFalse( admission.TryAccept( Frame( 3 ), 7, 0, out _, out var limited ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.RateLimited, limited );
		Assert.IsTrue( admission.TryAccept( Frame( 3 ), 7, 1, out _, out _ ) );
	}

	[TestMethod]
	public void SessionAndBodyRejectionsCanBeChargedBeforeValidation()
	{
		var admission = new PlayerInputAdmission( ratePerSecond: 1, burst: 2 );
		Assert.IsTrue( admission.TryBeginAttempt( 0, out _ ) );
		Assert.IsTrue( admission.TryBeginAttempt( 0, out _ ) );
		Assert.IsFalse( admission.TryBeginAttempt( 0, out var limited ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.RateLimited, limited );
	}

	[TestMethod]
	public void SequenceComparisonSupportsUIntWrapAndRejectsStaleJumpSequence()
	{
		var admission = new PlayerInputAdmission();
		Assert.IsTrue( admission.TryAccept( Frame( uint.MaxValue ) with { JumpSequence = 4 }, 7, 0, out _, out _ ) );
		Assert.IsTrue( admission.TryAccept( Frame( 0 ) with { JumpSequence = 5 }, 7, 0, out _, out _ ) );
		Assert.IsFalse( admission.TryAccept( Frame( 1 ) with { JumpSequence = 4 }, 7, 0, out _, out var stale ) );
		Assert.AreEqual( PlayerInputAdmissionFailure.StaleJumpSequence, stale );
	}

	[TestMethod]
	public void AuthorityTimeoutAndJumpRulesUseInclusiveSecurityBoundaries()
	{
		Assert.IsTrue( PlayerInputAuthorityRules.IsInputFresh( true, 10, 10.250 ) );
		Assert.IsFalse( PlayerInputAuthorityRules.IsInputFresh( true, 10, 10.251 ) );
		Assert.IsFalse( PlayerInputAuthorityRules.IsInputFresh( false, 10, 10 ) );
		Assert.IsTrue( PlayerInputAuthorityRules.CanJump( true, 300, 10, 10.500 ) );
		Assert.IsFalse( PlayerInputAuthorityRules.CanJump( true, 300, 10, 10.499 ) );
		Assert.IsFalse( PlayerInputAuthorityRules.CanJump( false, 300, 10, 11 ) );
		Assert.IsFalse( PlayerInputAuthorityRules.CanJump( true, 0, 10, 11 ) );
	}

	[TestMethod]
	public void DisabledDeadOrUnboundBodiesCannotMintSpatialAuthority()
	{
		Assert.IsTrue( PlayerBodyAuthorityRules.IsUsable( true, false, true, true ) );
		Assert.IsFalse( PlayerBodyAuthorityRules.IsUsable( true, false, true, false ) );
		Assert.IsFalse( PlayerBodyAuthorityRules.IsUsable( true, true, true, true ) );
		Assert.IsFalse( PlayerBodyAuthorityRules.IsUsable( false, false, true, true ) );
	}

	[TestMethod]
	public void SessionAuthenticationRequiresCanonicalCharacterGenerationAndExactPlayer()
	{
		var character = Guid.NewGuid();
		var valid = new PlayerInputAuthenticationState(
			true, true, true, character, character, 4, 4, true );
		Assert.IsTrue( PlayerInputSessionAuthentication.IsAuthorized( valid ) );
		Assert.IsFalse( PlayerInputSessionAuthentication.IsAuthorized(
			valid with { ExactPlayerInstance = false } ) );
		Assert.IsFalse( PlayerInputSessionAuthentication.IsAuthorized(
			valid with { CanonicalCharacterId = Guid.NewGuid() } ) );
		Assert.IsFalse( PlayerInputSessionAuthentication.IsAuthorized(
			valid with { FrameBodyGeneration = 3 } ) );
		Assert.IsFalse( PlayerInputSessionAuthentication.IsAuthorized(
			valid with { BodyUsable = false } ) );
	}

	[TestMethod]
	public void PredictionCorrectionThresholdsAreDeterministic()
	{
		Assert.AreEqual( PredictionCorrectionKind.Hard, PredictionReconciliation.Decide( false, 0 ) );
		Assert.AreEqual( PredictionCorrectionKind.Ignore, PredictionReconciliation.Decide( true, 2 ) );
		Assert.AreEqual( PredictionCorrectionKind.Smooth, PredictionReconciliation.Decide( true, 2.001f ) );
		Assert.AreEqual( PredictionCorrectionKind.Smooth, PredictionReconciliation.Decide( true, 48 ) );
		Assert.AreEqual( PredictionCorrectionKind.Hard, PredictionReconciliation.Decide( true, 48.001f ) );
		Assert.AreEqual( PredictionCorrectionKind.Hard, PredictionReconciliation.Decide( true, float.NaN ) );
	}

	[TestMethod]
	public void ReconciliationUsesTheAcknowledgedSampleAndPreservesNewerMotion()
	{
		var plan = PredictionReconciliation.Calculate(
			historyFound: true,
			authoritativeAtSequence: new PredictionPosition( 7, 0, 0 ),
			predictedAtSequence: new PredictionPosition( 10, 0, 0 ),
			currentPrediction: new PredictionPosition( 15, 0, 0 ) );

		Assert.AreEqual( PredictionCorrectionKind.Smooth, plan.Kind );
		Assert.AreEqual( -3f, plan.Delta.X );
		Assert.AreEqual( 12f, 15f + plan.Delta.X,
			"The five units simulated after the acknowledged frame must be preserved." );
	}

	[TestMethod]
	public void MissingHistoryHardCorrectsCurrentPredictionToAuthority()
	{
		var plan = PredictionReconciliation.Calculate(
			historyFound: false,
			authoritativeAtSequence: new PredictionPosition( 20, 2, 1 ),
			predictedAtSequence: default,
			currentPrediction: new PredictionPosition( 100, 3, 1 ) );

		Assert.AreEqual( PredictionCorrectionKind.Hard, plan.Kind );
		Assert.AreEqual( -80f, plan.Delta.X );
		Assert.AreEqual( -1f, plan.Delta.Y );
	}

	private static PlayerInputFrame Frame( uint sequence ) => new(
		BodyGeneration: 7,
		Sequence: sequence,
		MoveX: 0.25f,
		MoveY: -0.5f,
		Pitch: 10f,
		Yaw: 20f,
		Buttons: PlayerInputButtons.Run,
		JumpSequence: 1 );
}
