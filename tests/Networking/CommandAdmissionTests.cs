#nullable enable

using Hexagon.V2.Networking;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class CommandAdmissionTests
{
	[TestMethod]
	public void WeightedBucketRefillsExactlyAndRejectedAttemptsAreNotRefunded()
	{
		var admission = new CommandAdmissionController( 1_000 );
		var first = admission.TryBegin( CommandRequestId.New(), 16, 0 );
		var rejected = admission.TryBegin( CommandRequestId.New(), 1, 0 );
		var halfRefill = admission.TryBegin( CommandRequestId.New(), 4, 500 );
		var noRefund = admission.TryBegin( CommandRequestId.New(), 1, 500 );

		Assert.IsTrue( first.Accepted );
		Assert.AreEqual( CommandAdmissionFailure.RateLimited, rejected.Failure );
		Assert.AreEqual( TimeSpan.FromMilliseconds( 125 ), rejected.RetryAfter );
		Assert.IsTrue( halfRefill.Accepted );
		Assert.AreEqual( CommandAdmissionFailure.RateLimited, noRefund.Failure );
	}

	[TestMethod]
	public void ActiveCapIsSixteenAndTheSeventeenthAttemptStillConsumesItsToken()
	{
		var admission = new CommandAdmissionController( 1_000 );
		for ( var index = 0; index < CommandAdmissionController.MaximumActiveRequests; index++ )
			Assert.IsTrue( admission.TryBegin( CommandRequestId.New(), 1, index * 125 ).Accepted );

		var before = admission.AvailableUnits;
		var overflow = admission.TryBegin( CommandRequestId.New(), 1, 2_000 );

		Assert.AreEqual( CommandAdmissionFailure.RateLimited, overflow.Failure );
		Assert.AreEqual( CommandAdmissionController.MaximumActiveRequests, admission.ActiveCount );
		Assert.AreEqual( before, admission.AvailableUnits, 0.0001,
			"The one-unit refill was consumed before the active-cap rejection." );
	}

	[TestMethod]
	public void DuplicateRequestIsChargedBeforeReplayRejection()
	{
		var admission = new CommandAdmissionController( 1_000 );
		var request = CommandRequestId.New();
		Assert.IsTrue( admission.TryBegin( request, 2, 0 ).Accepted );
		Assert.IsTrue( admission.Finish( request ) );
		var before = admission.AvailableUnits;

		var duplicate = admission.TryBegin( request, 4, 0 );

		Assert.AreEqual( CommandAdmissionFailure.Duplicate, duplicate.Failure );
		Assert.AreEqual( before - 4, admission.AvailableUnits, 0.0001 );
	}

	[TestMethod]
	public void MalformedPayloadValidationOccursAfterAdmissionAndDoesNotRefund()
	{
		var admission = new CommandAdmissionController( 1_000 );
		var admitted = admission.TryBegin( CommandRequestId.New(), 2, 0 );
		var malformed = ClientPayloadLimits.Validate(
			new SendChatCommand( "ic", new string( 'x', ClientPayloadLimits.MaximumStringCharacters + 1 ) ) );

		Assert.IsTrue( admitted.Accepted );
		Assert.AreEqual( ErrorCode.InvalidArgument, malformed.Error!.Code );
		Assert.AreEqual( CommandAdmissionController.BurstUnits - 2, admission.AvailableUnits, 0.0001 );
	}

	[TestMethod]
	public void AuthenticatedIngressChargesBeforeSessionScopeValidationAndUnauthenticatedIngressDoesNotCharge()
	{
		var admission = new CommandAdmissionController( 1_000 );
		var order = new List<string>();
		var unauthenticated = CommandIngressAdmission.Evaluate<object>(
			false,
			() =>
			{
				order.Add( "admission" );
				return admission.TryBegin( CommandRequestId.New(), 4, 0 );
			},
			() =>
			{
				order.Add( "session" );
				return OperationResult<object>.Success( new object() );
			},
			() => order.Add( "finish" ) );

		Assert.IsFalse( unauthenticated.Authenticated );
		Assert.IsFalse( unauthenticated.SessionEvaluated );
		Assert.IsEmpty( order );
		Assert.AreEqual( CommandAdmissionController.BurstUnits, admission.AvailableUnits, 0.0001 );

		var requestId = CommandRequestId.New();
		var forgedScope = CommandIngressAdmission.Evaluate<object>(
			true,
			() =>
			{
				order.Add( "admission" );
				return admission.TryBegin( requestId, 4, 0 );
			},
			() =>
			{
				order.Add( "session" );
				return OperationResult<object>.Failure( ErrorCode.Unauthorized, "stale scope" );
			},
			() =>
			{
				order.Add( "finish" );
				Assert.IsTrue( admission.Finish( requestId ) );
			} );

		Assert.IsTrue( forgedScope.Authenticated );
		Assert.IsTrue( forgedScope.Admission.Accepted );
		Assert.IsTrue( forgedScope.SessionEvaluated );
		Assert.AreEqual( ErrorCode.Unauthorized, forgedScope.Session!.Value.Error!.Code );
		CollectionAssert.AreEqual( new[] { "admission", "session", "finish" }, order );
		Assert.AreEqual( CommandAdmissionController.BurstUnits - 4, admission.AvailableUnits, 0.0001 );
		Assert.AreEqual( 0, admission.ActiveCount );
	}

	[TestMethod]
	public void AuthenticatedIngressPreservesRateLimitRetryAndSkipsSessionValidationWhenRejected()
	{
		var admission = new CommandAdmissionController( 1_000 );
		Assert.IsTrue( admission.TryBegin( CommandRequestId.New(), CommandAdmissionController.BurstUnits, 0 ).Accepted );
		var sessionEvaluated = false;

		var rejected = CommandIngressAdmission.Evaluate<object>(
			true,
			() => admission.TryBegin( CommandRequestId.New(), 1, 0 ),
			() =>
			{
				sessionEvaluated = true;
				return OperationResult<object>.Success( new object() );
			},
			() => Assert.Fail( "A request rejected by admission was never active and cannot be finished." ) );

		Assert.IsTrue( rejected.Authenticated );
		Assert.AreEqual( CommandAdmissionFailure.RateLimited, rejected.Admission.Failure );
		Assert.AreEqual( TimeSpan.FromMilliseconds( 125 ), rejected.Admission.RetryAfter );
		Assert.IsFalse( rejected.SessionEvaluated );
		Assert.IsFalse( sessionEvaluated );
	}

	[TestMethod]
	public void SchemaCostResolutionBoundsLookupAndChargesMalformedAttemptsAsUnknown()
	{
		var lookupCount = 0;
		int? ResolveKnown( string id )
		{
			lookupCount++;
			return id switch { "cheap" => 1, "standard" => 2, "expensive" => 4, _ => null };
		}

		Assert.AreEqual( 1, SchemaCommandAdmissionCost.Resolve( "cheap", ResolveKnown ) );
		Assert.AreEqual( 2, SchemaCommandAdmissionCost.Resolve( "standard", ResolveKnown ) );
		Assert.AreEqual( 4, SchemaCommandAdmissionCost.Resolve( "expensive", ResolveKnown ) );
		Assert.AreEqual( 8, SchemaCommandAdmissionCost.Resolve( "unknown", ResolveKnown ) );
		Assert.AreEqual( 4, lookupCount );

		foreach ( var malformedId in new[]
		{
			new string( 'x', ClientPayloadLimits.MaximumIdentifierCharacters + 1 ),
			"\ud800"
		} )
		{
			var lookupsBefore = lookupCount;
			var cost = SchemaCommandAdmissionCost.Resolve( malformedId, ResolveKnown );
			var admission = new CommandAdmissionController( 1_000 );
			var admitted = admission.TryBegin( CommandRequestId.New(), cost, 0 );
			var validation = ClientPayloadLimits.Validate(
				new RunSchemaCommandCommand( malformedId, new Dictionary<string, SnapshotValue>() ) );

			Assert.AreEqual( SchemaCommandAdmissionCost.UnknownCommandUnits, cost );
			Assert.AreEqual( lookupsBefore, lookupCount,
				"Malformed identifiers must not reach the schema registry lookup." );
			Assert.IsTrue( admitted.Accepted );
			Assert.AreEqual( ErrorCode.InvalidArgument, validation.Error!.Code );
			Assert.AreEqual( CommandAdmissionController.BurstUnits - SchemaCommandAdmissionCost.UnknownCommandUnits,
				admission.AvailableUnits, 0.0001 );
		}
	}

	[TestMethod]
	public void RateLimitedOperationResultRoundTripsAnExplicitBoundedRetryDelay()
	{
		var hostResult = OperationResultWireContract.RateLimited(
			"budget exhausted", TimeSpan.FromMilliseconds( 125.25 ) );
		var wireDelay = OperationResultWireContract.EncodeRetryAfterMilliseconds( hostResult );
		var clientResult = OperationResultWireContract.Decode(
			false, (int)ErrorCode.RateLimited, hostResult.Error!.Message, wireDelay );

		Assert.AreEqual( 126, wireDelay );
		Assert.AreEqual( ErrorCode.RateLimited, clientResult.Error!.Code );
		Assert.AreEqual(
			"126",
			clientResult.Error.Details![OperationResultWireContract.RetryAfterMillisecondsDetail] );

		var clamped = OperationResultWireContract.Decode(
			false, (int)ErrorCode.RateLimited, "slow down", int.MaxValue );
		Assert.AreEqual(
			OperationResultWireContract.MaximumRetryAfterMilliseconds.ToString( System.Globalization.CultureInfo.InvariantCulture ),
			clamped.Error!.Details![OperationResultWireContract.RetryAfterMillisecondsDetail] );
	}

	[TestMethod]
	public void OperationResultWireDefaultsMalformedRetryDelayAndIgnoresItForOtherFailures()
	{
		var malformedRateLimit = OperationResult.Failure(
			ErrorCode.RateLimited,
			"budget exhausted",
			new Dictionary<string, string>( StringComparer.Ordinal )
			{
				[OperationResultWireContract.RetryAfterMillisecondsDetail] = "not-a-duration"
			} );
		Assert.AreEqual(
			OperationResultWireContract.DefaultRetryAfterMilliseconds,
			OperationResultWireContract.EncodeRetryAfterMilliseconds( malformedRateLimit ) );

		var conflict = OperationResultWireContract.Decode(
			false, (int)ErrorCode.Conflict, "conflict", 999 );
		Assert.IsNull( conflict.Error!.Details );
		Assert.AreEqual( 0, OperationResultWireContract.EncodeRetryAfterMilliseconds( conflict ) );
	}

	[TestMethod]
	public void ReconciliationPendingErrorCodeRoundTripsWithoutDowngrade()
	{
		var decoded = OperationResultWireContract.Decode(
			false,
			(int)ErrorCode.ReconciliationPending,
			"committed and pending",
			0 );

		Assert.AreEqual( ErrorCode.ReconciliationPending, decoded.Error!.Code );
	}

	[TestMethod]
	public async Task ParallelBeginFinishAndDisconnectPreserveTheActiveCapAndState()
	{
		for ( var iteration = 0; iteration < 50; iteration++ )
		{
			var admission = new CommandAdmissionController( 1_000 );
			var requests = Enumerable.Range( 0, 64 ).Select( _ => CommandRequestId.New() ).ToArray();
			var results = await Task.WhenAll( requests.Select( request =>
				Task.Run( () => admission.TryBegin( request, 1, 0 ) ) ) );
			Assert.AreEqual( CommandAdmissionController.MaximumActiveRequests,
				results.Count( result => result.Accepted ) );
			Assert.AreEqual( CommandAdmissionController.MaximumActiveRequests, admission.ActiveCount );

			await Task.WhenAll( requests.Select( request => Task.Run( () => admission.Finish( request ) ) ) );
			Assert.AreEqual( 0, admission.ActiveCount );

			await Task.WhenAll(
				Task.Run( admission.Disconnect ),
				Task.Run( () => admission.TryBegin( CommandRequestId.New(), 1, 1_000 ) ) );
			Assert.IsLessThanOrEqualTo( CommandAdmissionController.MaximumActiveRequests, admission.ActiveCount );
		}
	}
}
