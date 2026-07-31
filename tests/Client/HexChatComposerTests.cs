#nullable enable

using System;
using Hexagon.V2.Client.Chat;

namespace Hexagon.V2.Tests.Client;

[TestClass]
public sealed class HexChatComposerTests
{
	private static HexChatComposer Composer() => new( new[]
	{
		new HexChatChannelView( "ic", "IC", new[] { "ic", "say" }, AllowedWhileDead: false ),
		new HexChatChannelView( "ooc", "OOC", new[] { "ooc" }, AllowedWhileDead: false ),
		new HexChatChannelView( "me", "ME", new[] { "me", "emote" }, AllowedWhileDead: false ),
		new HexChatChannelView( "admin", "ADMIN", new[] { "a" }, AllowedWhileDead: true )
	} );

	[TestMethod]
	public void BareTextGoesToTheStickyChannel()
	{
		var intent = Composer().Compose( "hello there", isDead: false );

		Assert.AreEqual( HexChatIntentKind.Channel, intent.Kind );
		Assert.AreEqual( "ic", intent.ChannelId );
		Assert.AreEqual( "hello there", intent.Text );
	}

	[TestMethod]
	public void APrefixRoutesOneMessageWithoutMovingTheStickyChannel()
	{
		var composer = Composer();

		var intent = composer.Compose( "/ooc brb", isDead: false );

		Assert.AreEqual( HexChatIntentKind.Channel, intent.Kind );
		Assert.AreEqual( "ooc", intent.ChannelId );
		Assert.AreEqual( "brb", intent.Text );
		Assert.AreEqual( "ic", composer.StickyChannelId, "A one-off prefix must not retarget the sticky channel." );
	}

	[TestMethod]
	public void ABarePrefixRetargetsTheStickyChannelInstead()
	{
		var composer = Composer();

		var intent = composer.Compose( "/ooc", isDead: false );

		Assert.AreEqual( HexChatIntentKind.Console, intent.Kind );
		Assert.AreEqual( "ooc", composer.StickyChannelId );
	}

	/// <summary>Everything after the prefix is the message, spacing and all.</summary>
	[TestMethod]
	public void MessageTextSurvivesInternalSpacing()
	{
		var intent = Composer().Compose( "/me looks  around   slowly", isDead: false );

		Assert.AreEqual( "looks  around   slowly", intent.Text );
	}

	[TestMethod]
	public void AnUnknownPrefixBecomesACommandForTheGameToDispatch()
	{
		var intent = Composer().Compose( "/kill someone", isDead: false );

		Assert.AreEqual( HexChatIntentKind.Command, intent.Kind );
		Assert.AreEqual( "/kill someone", intent.Text, "The command layer must receive the whole line." );
	}

	[TestMethod]
	public void DeathSilencesChannelsThatDoNotAllowIt()
	{
		var composer = Composer();

		var speech = composer.Compose( "hello", isDead: true );
		var prefixed = composer.Compose( "/ooc hello", isDead: true );
		var permitted = composer.Compose( "/a hello", isDead: true );

		Assert.AreEqual( HexChatIntentKind.Console, speech.Kind );
		Assert.AreEqual( HexChatIntentKind.Console, prefixed.Kind );
		Assert.AreEqual( HexChatIntentKind.Channel, permitted.Kind,
			"A channel marked AllowedWhileDead must still work." );
	}

	/// <summary>
	/// Cycling while dead must not park the player on a channel that will refuse everything they
	/// type — the key would appear to work and then nothing would send.
	/// </summary>
	[TestMethod]
	public void CyclingWhileDeadSkipsChannelsTheDeadCannotUse()
	{
		var composer = Composer();

		for ( var step = 0; step < 6; step++ )
		{
			composer.CycleChannel( isDead: true );
			Assert.AreEqual( "admin", composer.StickyChannelId );
		}
	}

	[TestMethod]
	public void CyclingWhileAliveVisitsEveryChannelAndWrapsAround()
	{
		var composer = Composer();
		var seen = new List<string> { composer.StickyChannelId };

		for ( var step = 0; step < 3; step++ ) seen.Add( composer.CycleChannel( isDead: false ) );

		CollectionAssert.AreEquivalent( new[] { "ic", "ooc", "me", "admin" }, seen );
		Assert.AreEqual( "ic", composer.CycleChannel( isDead: false ) );
	}

	[TestMethod]
	public void EmptyInputDoesNothing()
	{
		Assert.AreEqual( HexChatIntentKind.Ignored, Composer().Compose( "   ", isDead: false ).Kind );
		Assert.AreEqual( HexChatIntentKind.Ignored, Composer().Compose( null, isDead: false ).Kind );
	}

	[TestMethod]
	public void ABareSlashExplainsItselfRatherThanSpeaking()
	{
		var intent = Composer().Compose( "/", isDead: false );

		Assert.AreEqual( HexChatIntentKind.Console, intent.Kind );
		StringAssert.Contains( intent.Message, "/help" );
	}
}
