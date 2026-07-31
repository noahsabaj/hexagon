#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.V2.Client.Chat;

/// <summary>One channel as the composer needs to see it, free of any schema or engine type.</summary>
public sealed record HexChatChannelView(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Prefixes,
	bool AllowedWhileDead );

public enum HexChatIntentKind
{
	/// <summary>Speak on a channel.</summary>
	Channel = 0,
	/// <summary>Not a channel prefix — hand the whole line to the game's command layer.</summary>
	Command = 1,
	/// <summary>Answer locally; nothing leaves this client.</summary>
	Console = 2,
	/// <summary>Nothing to do (empty input).</summary>
	Ignored = 3
}

public sealed record HexChatIntent(
	HexChatIntentKind Kind,
	string ChannelId = "",
	string Text = "",
	string Message = "" );

/// <summary>
/// Decides what a typed line means: speech on some channel, a command for the game to dispatch, or
/// something to answer locally. Engine-neutral and unit tested, because this is the rule that stands
/// between what a player typed and who hears it — and the failure mode is a private line going to
/// the wrong channel, which cannot be taken back.
/// </summary>
public sealed class HexChatComposer
{
	public const char Prefix = '/';

	private readonly IReadOnlyList<HexChatChannelView> _channels;

	public HexChatComposer( IReadOnlyList<HexChatChannelView> channels, string? defaultChannelId = null )
	{
		ArgumentNullException.ThrowIfNull( channels );
		if ( channels.Count == 0 ) throw new ArgumentException( "At least one channel is required.", nameof(channels) );
		_channels = channels;
		StickyChannelId = defaultChannelId is not null && channels.Any( channel => channel.Id == defaultChannelId )
			? defaultChannelId
			: channels[0].Id;
	}

	/// <summary>The channel bare text goes to. Changed by the cycle key, never by a one-off prefix.</summary>
	public string StickyChannelId { get; private set; }

	public HexChatChannelView StickyChannel =>
		_channels.First( channel => channel.Id == StickyChannelId );

	public IReadOnlyList<HexChatChannelView> Channels => _channels;

	/// <summary>
	/// Advances the sticky channel. Skips channels the caller cannot currently use, so cycling while
	/// dead does not park the player on a channel that will silently refuse everything they type.
	/// </summary>
	public string CycleChannel( bool isDead )
	{
		var usable = _channels.Where( channel => !isDead || channel.AllowedWhileDead ).ToArray();
		if ( usable.Length == 0 ) return StickyChannelId;
		var index = Array.FindIndex( usable, channel => channel.Id == StickyChannelId );
		StickyChannelId = usable[(index + 1) % usable.Length].Id;
		return StickyChannelId;
	}

	/// <summary>
	/// Resolves a line. Channel prefixes are checked BEFORE commands, which is what players from this
	/// genre expect — so a prefix equal to a command name would shadow it, and a schema guard forbids
	/// that rather than leaving it to review.
	/// </summary>
	public HexChatIntent Compose( string? input, bool isDead )
	{
		if ( string.IsNullOrWhiteSpace( input ) ) return new HexChatIntent( HexChatIntentKind.Ignored );
		var text = input.Trim();

		if ( text[0] != Prefix )
			return Speak( StickyChannelId, text, isDead );

		var body = text[1..];
		var split = body.IndexOf( ' ' );
		var word = split < 0 ? body : body[..split];
		var rest = split < 0 ? string.Empty : body[(split + 1)..].Trim();

		if ( word.Length == 0 )
			return new HexChatIntent( HexChatIntentKind.Console, Message: "Type a command or channel after '/'. Try /help." );

		var channel = _channels.FirstOrDefault( candidate =>
			candidate.Prefixes.Any( prefix => string.Equals( prefix, word, StringComparison.OrdinalIgnoreCase ) ) );
		if ( channel is null )
			return new HexChatIntent( HexChatIntentKind.Command, Text: text );

		// A bare channel prefix with nothing after it retargets the sticky channel rather than
		// sending an empty line — typing "/ooc" then talking is the habit this serves.
		if ( rest.Length == 0 )
		{
			if ( isDead && !channel.AllowedWhileDead )
				return Refused( channel );
			StickyChannelId = channel.Id;
			return new HexChatIntent( HexChatIntentKind.Console, Message: $"Now speaking in {channel.DisplayName}." );
		}

		return Speak( channel.Id, rest, isDead );
	}

	private HexChatIntent Speak( string channelId, string text, bool isDead )
	{
		var channel = _channels.First( candidate => candidate.Id == channelId );
		if ( isDead && !channel.AllowedWhileDead ) return Refused( channel );
		return new HexChatIntent( HexChatIntentKind.Channel, channel.Id, text );
	}

	private static HexChatIntent Refused( HexChatChannelView channel ) =>
		new( HexChatIntentKind.Console, Message: $"The dead cannot speak in {channel.DisplayName}." );
}
