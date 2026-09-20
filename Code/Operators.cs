#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What the people who run a city can do. There is one set of commands and two ways in: the server
/// console, and a staff account typing the same line in game. Either way the command is journaled
/// with who gave it, so staff are on the record like everyone else, and reading the journal is
/// itself an entry in it. Staff status is granted only from the console: it cannot be given away,
/// or taken, from inside the game.
/// </summary>
public static class Operators
{
	public const string Help = "whitelist <steamid> <faction> | unwhitelist <steamid> <faction> | give \"<name>\" <item> [count] | " +
		"teleport \"<name>\" <x> <y> <z> | bring \"<name>\" | goto \"<name>\" | hurt \"<name>\" <amount> | revive \"<name>\" | " +
		"payday | who | journal [text] [count]";

	[ConCmd( "hexagon_whitelist" )] public static void Whitelist( string steamId, string faction ) => FromConsole( "whitelist", steamId, faction );
	[ConCmd( "hexagon_unwhitelist" )] public static void Unwhitelist( string steamId, string faction ) => FromConsole( "unwhitelist", steamId, faction );
	[ConCmd( "hexagon_give" )] public static void Give( string characterName, string item, int count = 1 ) => FromConsole( "give", characterName, item, count.ToString() );
	[ConCmd( "hexagon_teleport" )] public static void Teleport( string characterName, float x, float y, float z ) => FromConsole( "teleport", characterName, x.ToString(), y.ToString(), z.ToString() );
	[ConCmd( "hexagon_hurt" )] public static void Hurt( string characterName, int amount ) => FromConsole( "hurt", characterName, amount.ToString() );
	[ConCmd( "hexagon_revive" )] public static void Revive( string characterName ) => FromConsole( "revive", characterName );
	[ConCmd( "hexagon_payday" )] public static void Payday() => FromConsole( "payday" );
	[ConCmd( "hexagon_who" )] public static void Who() => FromConsole( "who" );
	[ConCmd( "hexagon_journal" )] public static void Journal( string text = "", int count = 20 ) => FromConsole( "journal", text, count.ToString() );

	/// <summary>Console only, deliberately: who is staff is decided by whoever owns the server.</summary>
	[ConCmd( "hexagon_staff" )]
	public static void Staff( string steamId, int enabled )
	{
		if ( !TryHost( out var game ) ) return;
		if ( !long.TryParse( steamId, out var id ) || id <= 0 )
		{
			Log.Warning( "Usage: hexagon_staff <steamid64> <0|1>" );
			return;
		}
		var account = game.Account( id );
		account.IsStaff = enabled != 0;
		game.SaveAccount( account );
		game.Journal!.Record( "operator.staff", Actor.Console, $"account:{id}", data: ("staff", account.IsStaff.ToString()) );
		foreach ( var player in game.Scene.GetAllComponents<Player>() ) player.HostSendAccount();
		Log.Info( $"{id} is {(account.IsStaff ? "now" : "no longer")} staff." );
	}

	private static void FromConsole( params string[] words )
	{
		if ( !TryHost( out var game ) ) return;
		foreach ( var line in Execute( game, Actor.Console, null, words ) ) Log.Info( line );
	}

	/// <summary>Splits a typed line into words, keeping a "quoted name" as one.</summary>
	public static string[] Words( string line )
	{
		var words = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted = false;
		foreach ( var character in line )
		{
			if ( character == '"' ) quoted = !quoted;
			else if ( character == ' ' && !quoted )
			{
				if ( current.Length > 0 ) words.Add( current.ToString() );
				current.Clear();
			}
			else current.Append( character );
		}
		if ( current.Length > 0 ) words.Add( current.ToString() );
		return words.ToArray();
	}

	/// <summary>Host: carries out one command and returns what to tell whoever gave it.</summary>
	public static IReadOnlyList<string> Execute( GameManager game, Actor by, Player? asker, string[] words )
	{
		if ( words.Length == 0 ) return new[] { Help };
		var journal = game.Journal!;
		string Word( int index ) => index < words.Length ? words[index] : string.Empty;
		Player? Find( string name ) => game.Scene.GetAllComponents<Player>()
			.FirstOrDefault( value => value.HostCharacter?.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) == true );

		switch ( words[0].ToLowerInvariant() )
		{
			case "whitelist":
			case "unwhitelist":
			{
				if ( !long.TryParse( Word( 1 ), out var id ) || id <= 0 ) return new[] { $"Usage: {words[0]} <steamid64> <faction>" };
				var faction = FactionDefinition.All.FirstOrDefault( value => value.ResourceName.Equals( Word( 2 ), StringComparison.OrdinalIgnoreCase ) );
				if ( faction is null ) return new[] { $"No faction named '{Word( 2 )}'. Known: {string.Join( ", ", FactionDefinition.All.Select( value => value.ResourceName ) )}" };
				var account = game.Account( id );
				var adding = words[0].Equals( "whitelist", StringComparison.OrdinalIgnoreCase );
				account.Whitelists.Remove( faction.ResourcePath );
				if ( adding ) account.Whitelists.Add( faction.ResourcePath );
				game.SaveAccount( account );
				journal.Record( $"operator.{words[0].ToLowerInvariant()}", by, $"account:{id}", data: ("faction", faction.ResourceName) );
				return new[] { $"{id} may {(adding ? "now" : "no longer")} create {faction.Title} characters." };
			}
			case "give":
			{
				var definition = ResourceLibrary.GetAll<ItemDefinition>().FirstOrDefault( value => value.ResourceName.Equals( Word( 2 ), StringComparison.OrdinalIgnoreCase ) );
				if ( Find( Word( 1 ) ) is not { } target || definition is null ) return new[] { "Usage: give \"<character name>\" <item> [count]. The character must be in the city." };
				var count = int.TryParse( Word( 3 ), out var asked ) ? Math.Clamp( asked, 1, 100 ) : 1;
				// An operator's gift is new matter entering the world, so it comes from a named source.
				var given = target.HostIssue( Sources.Operator, definition, by, count );
				return new[] { given.Ok ? $"Gave {(count > 1 ? $"{count} " : string.Empty)}{definition.Title} to {target.HostCharacter!.Name}." : given.Message };
			}
			case "teleport":
			{
				if ( Find( Word( 1 ) ) is not { } target || !float.TryParse( Word( 2 ), out var x ) || !float.TryParse( Word( 3 ), out var y ) || !float.TryParse( Word( 4 ), out var z ) )
					return new[] { "Usage: teleport \"<character name>\" <x> <y> <z>. The character must be in the city." };
				return new[] { Move( journal, by, target, new Vector3( x, y, z ) ) };
			}
			case "bring":
			case "goto":
			{
				if ( Find( Word( 1 ) ) is not { } target || asker is null ) return new[] { $"Usage, in game only: {words[0]} \"<character name>\"." };
				return new[] { words[0].Equals( "bring", StringComparison.OrdinalIgnoreCase )
					? Move( journal, by, target, asker.HostPosition + Vector3.Up * 4f )
					: Move( journal, by, asker, target.HostPosition + Vector3.Up * 4f ) };
			}
			case "hurt":
			{
				if ( Find( Word( 1 ) ) is not { } target || !int.TryParse( Word( 2 ), out var amount ) || amount <= 0 )
					return new[] { "Usage: hurt \"<character name>\" <amount>. The character must be in the city." };
				journal.Record( "operator.hurt", by, target.HostCharacter!.HolderLabel, data: ("amount", amount.ToString()) );
				target.HostDamage( amount, null, "operator" );
				return new[] { $"Hurt {target.HostCharacter.Name} for {amount}." };
			}
			case "revive":
			{
				if ( Find( Word( 1 ) ) is not { } target ) return new[] { "Usage: revive \"<character name>\". The character must be in the city." };
				journal.Record( "operator.revive", by, target.HostCharacter!.HolderLabel );
				var revived = target.HostRevive();
				return new[] { revived.Ok ? $"Helped {target.HostCharacter.Name} up." : revived.Message };
			}
			case "payday":
				return new[] { $"Paid {game.PayWages( by )} characters." };
			case "who":
				// The one place a real name is shown to someone who was never told it. It is staff only, and on the record.
				return game.Scene.GetAllComponents<Player>().Where( value => value.Network.Owner is not null ).Select( value =>
					$"{value.Network.Owner!.DisplayName} [{value.Network.Owner.SteamId.ValueUnsigned}] is {value.HostCharacter?.Name ?? "in the menu"}" +
					(value.HostCharacter is null ? string.Empty : $" at {value.HostPosition.x:0},{value.HostPosition.y:0},{value.HostPosition.z:0}") ).ToArray();
			case "journal":
			{
				var count = int.TryParse( Word( 2 ), out var asked ) ? Math.Clamp( asked, 1, 50 ) : 20;
				var found = journal.Search( DateTimeOffset.UtcNow, Word( 1 ), count );
				return found.Count == 0 ? new[] { "Nothing in today's journal matches." } : found.Select( value => value.Describe() ).ToArray();
			}
			default:
				return new[] { Help };
		}
	}

	private static string Move( Journal journal, Actor by, Player target, Vector3 to )
	{
		target.HostTeleport( to );
		journal.Record( "operator.teleport", by, target.HostCharacter!.HolderLabel, data: ("to", $"{to.x:0},{to.y:0},{to.z:0}") );
		return $"Moved {target.HostCharacter.Name}.";
	}

	private static bool TryHost( out GameManager game )
	{
		game = GameManager.Instance!;
		if ( Networking.IsHost && game?.Roster is not null ) return true;
		Log.Warning( "This command only works on the host." );
		return false;
	}
}
