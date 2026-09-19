#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>A received line. A notice is the host talking to this player alone, not speech.</summary>
public readonly record struct ChatLine( string Text, bool IsNotice, ChatChannel Channel, RealTimeSince Age );

/// <summary>
/// Chat travels as host-only broadcasts filtered to the connections that should hear it. A client
/// never learns that a message it was out of range for existed.
/// </summary>
public static class Chat
{
	public const int MaximumLines = 100;

	/// <summary>Lines this client has received, oldest first.</summary>
	public static List<ChatLine> Lines { get; } = new();

	/// <summary>Bumped on every new line so panels can rebuild.</summary>
	public static int Version { get; private set; }

	public static void Clear()
	{
		Lines.Clear();
		Version++;
	}

	/// <summary>Host: deliver a message from a speaker to everyone in range of it.</summary>
	public static void Deliver( Player speaker, ChatMessage message )
	{
		if ( !Networking.IsHost || speaker.HostCharacter is not { } character ) return;
		if ( message.Channel == ChatChannel.Radio )
		{
			DeliverRadio( speaker, character, message.Text );
			return;
		}
		var range = ChatRules.Range( message.Channel );
		var origin = speaker.HostPosition;
		var listeners = Game.ActiveScene.GetAllComponents<Player>()
			.Where( listener => listener.Network.Owner is not null )
			.Where( listener => range is null ||
				(listener.HasCharacter && listener.HostPosition.Distance( origin ) <= range.Value) )
			.ToArray();
		// Speech is journaled with exactly who received it, which is what a dispute will ask.
		GameManager.Instance?.Journal?.Record( $"chat.{message.Channel.ToString().ToLowerInvariant()}", Actor.Of( character ),
			where: new[] { origin.x, origin.y, origin.z },
			witnesses: listeners.Where( listener => listener != speaker && listener.HostCharacter is not null ).Select( listener => listener.HostCharacter!.Id ),
			data: ("text", message.Text) );
		// Each listener gets their own line: the speaker's name if they know it, otherwise what they
		// see. Out-of-character talk is between players, so it carries the player's name instead.
		foreach ( var listener in listeners )
		{
			var label = message.Channel == ChatChannel.Ooc
				? speaker.Network.Owner?.DisplayName ?? "Someone"
				: listener.HostCharacter is { } hearing ? Recognition.Label( hearing, character ) : Recognition.Stranger( character.Description );
			using ( Rpc.FilterInclude( listener.Network.Owner! ) ) Receive( (int)message.Channel, ChatRules.Format( message.Channel, label, message.Text ) );
		}
	}

	/// <summary>Host: the first tuned radio a character carries, or null.</summary>
	public static string? TunedTo( CharacterData character ) =>
		character.Inventory.Items.FirstOrDefault( item => item.Frequency is not null && ItemDefinition.Find( item.Definition ) is { IsRadio: true } )?.Frequency;

	/// <summary>
	/// Host: speech into a radio. Those standing near hear someone speaking, as they would. Everyone
	/// else carrying a radio tuned the same hears a voice, wherever they are. Nobody else hears anything.
	/// </summary>
	private static void DeliverRadio( Player speaker, CharacterData character, string text )
	{
		if ( speaker.Network.Owner is not { } owner ) return;
		if ( speaker.IsIncapable || TunedTo( character ) is not { } frequency )
		{
			Tell( owner, speaker.IsIncapable ? "You cannot reach your radio." : "You have no radio tuned to anything." );
			return;
		}
		var origin = speaker.HostPosition;
		var present = Game.ActiveScene.GetAllComponents<Player>().Where( listener => listener.Network.Owner is not null && listener.HostCharacter is not null ).ToArray();
		var near = present.Where( listener => listener != speaker && listener.HostPosition.Distance( origin ) <= ChatRules.Range( ChatChannel.Radio )!.Value ).ToArray();
		var tuned = present.Where( listener => listener != speaker && !near.Contains( listener ) && TunedTo( listener.HostCharacter! ) == frequency ).ToArray();
		GameManager.Instance?.Journal?.Record( "chat.radio", Actor.Of( character ),
			where: new[] { origin.x, origin.y, origin.z },
			witnesses: near.Concat( tuned ).Where( listener => listener != speaker ).Select( listener => listener.HostCharacter!.Id ),
			data: new[] { ("text", text), ("frequency", frequency) } );
		using ( Rpc.FilterInclude( owner ) ) Receive( (int)ChatChannel.Radio, ChatRules.FormatRadio( frequency, character.Name, text ) );
		foreach ( var listener in near )
			using ( Rpc.FilterInclude( listener.Network.Owner! ) )
				Receive( (int)ChatChannel.Radio, ChatRules.Format( ChatChannel.Radio, Recognition.Label( listener.HostCharacter!, character ), text ) );
		foreach ( var listener in tuned )
			using ( Rpc.FilterInclude( listener.Network.Owner! ) )
				Receive( (int)ChatChannel.Radio, ChatRules.FormatRadio( frequency, Recognition.Voice( listener.HostCharacter!, character ), text ) );
	}

	/// <summary>Host: a line for one connection only, such as the reason an action was refused.</summary>
	public static void Tell( Connection connection, string text )
	{
		if ( !Networking.IsHost ) return;
		using ( Rpc.FilterInclude( connection ) ) Receive( -1, text );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private static void Receive( int channel, string text )
	{
		Lines.Add( new ChatLine( text, channel < 0, channel < 0 ? ChatChannel.Say : (ChatChannel)channel, 0 ) );
		if ( Lines.Count > MaximumLines ) Lines.RemoveAt( 0 );
		Version++;
	}
}
