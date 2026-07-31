#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.V2.Application;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Kernel.Definitions;
using Hexagon.V2.Kernel.Policies;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Tests.Application;

[TestClass]
public sealed class ChatServiceTests
{
	[TestMethod]
	public void UnicodeLimitCountsScalarsRatherThanUtf16CodeUnits()
	{
		var scalar = char.ConvertFromUtf32( 0x1F600 );
		var accepted = ChatService.Normalize( string.Concat( Enumerable.Repeat( scalar, 512 ) ) );
		var rejected = ChatService.Normalize( string.Concat( Enumerable.Repeat( scalar, 513 ) ) );

		Assert.IsTrue( accepted.Succeeded, accepted.Error?.Message );
		Assert.AreEqual( 1024, accepted.Value.Length );
		Assert.AreEqual( ErrorCode.InvalidArgument, rejected.Error!.Code );
	}

	[TestMethod]
	public void NormalizeComposesUnicodeTrimsEdgesAndRejectsControlCharacters()
	{
		var normalized = ChatService.Normalize( "  Cafe\u0301  " );
		var control = ChatService.Normalize( "valid\u0001text" );

		Assert.IsTrue( normalized.Succeeded, normalized.Error?.Message );
		Assert.AreEqual( "Caf\u00E9", normalized.Value );
		Assert.AreEqual( ErrorCode.InvalidArgument, control.Error!.Code );
	}

	[TestMethod]
	public void MalformedUnicodeFailsClosedWithoutThrowing()
	{
		var result = ChatService.Normalize( "broken\uD800" );

		Assert.AreEqual( ErrorCode.InvalidArgument, result.Error!.Code );
	}

	[TestMethod]
	public void UnknownChannelAndDeniedPermissionFailClosedBeforeInventoryCapture()
	{
		var fixture = new ChatFixture( permissionAllowed: false );

		var unknown = fixture.Service.Send( fixture.Actor, fixture.Character, "missing", "hello" );
		var denied = fixture.Service.Send( fixture.Actor, fixture.Character, "dispatch", "hello" );

		Assert.AreEqual( ErrorCode.UnknownDefinition, unknown.Error!.Code );
		Assert.AreEqual( ErrorCode.Unauthorized, denied.Error!.Code );
		Assert.AreEqual( 0, fixture.Inventory.CaptureCount );
		Assert.AreEqual( 0, fixture.Recipients.ResolveCount );
	}

	[TestMethod]
	public void SchemaRejectsChannelThatReferencesUnknownPermission()
	{
		var report = SchemaCompiler.Validate( new DelegateSchema( builder =>
			builder.RegisterChatChannel( new ChatChannelDefinition( "dispatch", "missing" ) ) ) );

		Assert.IsTrue( report.Issues.Any( issue => issue.Code == ErrorCode.UnknownDefinition ) );
	}

	[TestMethod]
	public void GlobalBucketAllowsFourMessagesPerFiveSeconds()
	{
		var fixture = new ChatFixture();

		var first = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "one" );
		var second = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "two" );
		var third = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "three" );
		var fourth = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "four" );
		var limited = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "five" );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 5 ) );
		var replenished = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "six" );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.IsTrue( second.Succeeded, second.Error?.Message );
		Assert.IsTrue( third.Succeeded, third.Error?.Message );
		Assert.IsTrue( fourth.Succeeded, fourth.Error?.Message );
		Assert.AreEqual( ErrorCode.Conflict, limited.Error!.Code );
		Assert.IsTrue( replenished.Succeeded, replenished.Error?.Message );
		Assert.AreEqual( 5, fixture.Inventory.CaptureCount );
	}

	[TestMethod]
	public void StricterChannelOverrideAppliesInAdditionToGlobalBucket()
	{
		var fixture = new ChatFixture();

		var first = fixture.Service.Send( fixture.Actor, fixture.Character, "dispatch", "first" );
		var limited = fixture.Service.Send( fixture.Actor, fixture.Character, "dispatch", "second" );
		fixture.Clock.Advance( TimeSpan.FromSeconds( 10 ) );
		var replenished = fixture.Service.Send( fixture.Actor, fixture.Character, "dispatch", "third" );

		Assert.IsTrue( first.Succeeded, first.Error?.Message );
		Assert.AreEqual( ErrorCode.Conflict, limited.Error!.Code );
		Assert.IsTrue( replenished.Succeeded, replenished.Error?.Message );
	}

	[TestMethod]
	public void AcceptedMessageCapturesLiveInventoryExactlyOnce()
	{
		var fixture = new ChatFixture();

		var result = fixture.Service.Send( fixture.Actor, fixture.Character, "ic", "hello" );

		Assert.IsTrue( result.Succeeded, result.Error?.Message );
		Assert.AreEqual( 1, fixture.Inventory.CaptureCount );
		Assert.AreEqual( 1, fixture.Recipients.ResolveCount );
		Assert.AreSame( fixture.Inventory.Snapshot, fixture.Recipients.LastInventory );
	}

	[TestMethod]
	public void HeldReservationAcrossRefillCannotCreateDoubleCredit()
	{
		var admission = new ChatAdmissionService();
		var connection = ConnectionId.New();
		var limit = new ChatRateLimit( 1, TimeSpan.FromSeconds( 10 ) );
		var start = DateTimeOffset.UnixEpoch;
		var held = admission.Reserve( connection, "request", limit, limit, start );

		Assert.IsTrue( held.Succeeded, held.Error?.Message );
		var blockedAfterFullRefill = admission.Reserve(
			connection, "request", limit, limit, start + limit.RefillPeriod );
		Assert.AreEqual( ErrorCode.Conflict, blockedAfterFullRefill.Error!.Code );

		held.Value.Dispose();
		var replacement = admission.Reserve(
			connection, "request", limit, limit, start + limit.RefillPeriod );
		Assert.IsTrue( replacement.Succeeded, replacement.Error?.Message );
		replacement.Value.Commit( start + limit.RefillPeriod );
		replacement.Value.Dispose();

		var spent = admission.Reserve(
			connection, "request", limit, limit, start + limit.RefillPeriod );
		Assert.AreEqual( ErrorCode.Conflict, spent.Error!.Code );
	}

	private sealed class ChatFixture
	{
		public ChatFixture( bool permissionAllowed = true )
		{
			Actor = ApplicationServiceTestEnvironment.Actor();
			Character = ApplicationServiceTestEnvironment.Character(
				Actor.AccountId, 1, Actor.CharacterId );
			Clock = new MutableClock();
			Inventory = new RecordingInventoryView();
			Recipients = new RecordingRecipientResolver();
			var schema = SchemaCompiler.Compile( new DelegateSchema( builder =>
			{
				builder.RegisterPermission( new PermissionDefinition( "dispatch" ) );
				builder.RegisterChatChannel( new ChatChannelDefinition( "ic" ) );
				builder.RegisterChatChannel( new ChatChannelDefinition( "dispatch", "dispatch" ) );
			} ) );
			if ( schema.Failed ) throw new InvalidOperationException( schema.Error!.Message );
			Service = new ChatService(
				schema.Value,
				new[]
				{
					new ChatChannelRule { Id = "ic" },
					new ChatChannelRule
					{
						Id = "dispatch",
						RateLimit = new ChatRateLimit( 1, TimeSpan.FromSeconds( 10 ) )
					}
				},
				new FixedPermissionAuthorizer( permissionAllowed ),
				Recipients,
				Inventory,
				Clock,
				ApplicationServiceTestEnvironment.AllowPolicy<ChatSendContext>() );
		}

		public InventoryActor Actor { get; }
		public CharacterRecord Character { get; }
		public MutableClock Clock { get; }
		public RecordingInventoryView Inventory { get; }
		public RecordingRecipientResolver Recipients { get; }
		public ChatService Service { get; }
	}

	private sealed class DelegateSchema : IHexSchema
	{
		private readonly Action<SchemaBuilder> _configure;

		public DelegateSchema( Action<SchemaBuilder> configure ) => _configure = configure;

		public string Id => "chat_test";
		public void Configure( SchemaBuilder builder ) => _configure( builder );
	}

	private sealed class FixedPermissionAuthorizer : IPermissionAuthorizer
	{
		private readonly bool _allowed;

		public FixedPermissionAuthorizer( bool allowed ) => _allowed = allowed;

		public bool HasPermission( AccountId accountId, CharacterId characterId, string permissionId ) =>
			_allowed;
	}

	private sealed class RecordingInventoryView : ILiveInventoryView
	{
		public LiveInventorySnapshot Snapshot { get; } = new(
			42,
			Array.Empty<LiveInventoryItemView>() );

		public int CaptureCount { get; private set; }

		public LiveInventorySnapshot Capture()
		{
			CaptureCount++;
			return Snapshot;
		}
	}

	private sealed class RecordingRecipientResolver : IChatRecipientResolver
	{
		public int ResolveCount { get; private set; }
		public LiveInventorySnapshot? LastInventory { get; private set; }

		public IReadOnlyList<ConnectionId> Resolve(
			ChatSendContext context,
			ChatChannelRule rule,
			LiveInventorySnapshot inventory )
		{
			ResolveCount++;
			LastInventory = inventory;
			return new[] { context.Actor.ConnectionId, context.Actor.ConnectionId };
		}
	}

	private sealed class MutableClock : IHexClock
	{
		public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
		public void Advance( TimeSpan elapsed ) => UtcNow += elapsed;
	}
}
