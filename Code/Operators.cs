#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// What the people who run a city can do. There is one set of commands and two ways in: the server
/// console, and a staff account typing the same line in game. Either way the line is journaled
/// with who gave it before it runs, so staff are on the record like everyone else, and reading the
/// journal is itself an entry in it. Staff status is granted only from the console: it cannot be
/// given away, or taken, from inside the game. A character is named in full, and need not be in
/// the city unless the command moves or hurts it. The dead are only ever restored.
/// </summary>
public static class Operators
{
	public const string Help = "whitelist <steamid> <faction> | unwhitelist <steamid> <faction> | give \"<name>\" <item> [count] [pattern] | " +
		"take \"<name>\" <item> [count] | move \"<from>\" \"<to>\" <item> [count] | " +
		"teleport \"<name>\" <x> <y> <z> | bring \"<name>\" | goto \"<name>\" | hurt \"<name>\" <amount> | revive \"<name>\" | release \"<name>\" | " +
		"restore \"<name>\" | faction \"<name>\" <faction> | door [\"<door>\"] <open|close|lock|unlock|release|assign \"<name>\"> | door | who | journal [text] [count] [days]";

	[ConCmd( "hexagon_whitelist" )] public static void Whitelist( string steamId, string faction ) => FromConsole( "whitelist", steamId, faction );
	[ConCmd( "hexagon_unwhitelist" )] public static void Unwhitelist( string steamId, string faction ) => FromConsole( "unwhitelist", steamId, faction );
	[ConCmd( "hexagon_give" )] public static void Give( string characterName, string item, int count = 0, string pattern = "" ) => FromConsole( "give", characterName, item, Count( count ), pattern );
	[ConCmd( "hexagon_take" )] public static void Take( string characterName, string item, int count = 0 ) => FromConsole( "take", characterName, item, Count( count ) );
	[ConCmd( "hexagon_move" )] public static void MoveBetween( string from, string to, string item, int count = 0 ) => FromConsole( "move", from, to, item, Count( count ) );
	[ConCmd( "hexagon_teleport" )] public static void Teleport( string characterName, float x, float y, float z ) => FromConsole( "teleport", characterName, Number( x ), Number( y ), Number( z ) );
	[ConCmd( "hexagon_hurt" )] public static void Hurt( string characterName, int amount ) => FromConsole( "hurt", characterName, Count( amount ) );
	[ConCmd( "hexagon_revive" )] public static void Revive( string characterName ) => FromConsole( "revive", characterName );
	[ConCmd( "hexagon_release" )] public static void Release( string characterName ) => FromConsole( "release", characterName );
	[ConCmd( "hexagon_restore" )] public static void Restore( string characterName ) => FromConsole( "restore", characterName );
	[ConCmd( "hexagon_faction" )] public static void SetFaction( string characterName, string faction ) => FromConsole( "faction", characterName, faction );
	[ConCmd( "hexagon_door" )] public static void OperateDoor( string door, string action, string owner = "" ) => FromConsole( "door", door, action, owner );
	[ConCmd( "hexagon_who" )] public static void Who() => FromConsole( "who" );
	[ConCmd( "hexagon_journal" )] public static void Journal( string text = "", int count = 0, int days = 0 ) => FromConsole( "journal", text, Count( count ), Count( days ) );

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
		game.Journal!.Record( JournalKinds.OperatorStaff, Actor.Console, $"account:{id}", data: ("staff", account.IsStaff.ToString()) );
		foreach ( var player in game.Scene.GetAllComponents<Player>() ) player.HostSendAccount();
		Log.Info( $"{id} is {(account.IsStaff ? "now" : "no longer")} staff." );
	}

	/// <summary>An optional number left out is an empty word, so the command uses its own default.</summary>
	private static string Count( int value ) => value > 0 ? value.ToString( CultureInfo.InvariantCulture ) : string.Empty;

	private static string Number( float value ) => value.ToString( CultureInfo.InvariantCulture );

	private static void FromConsole( params string[] words )
	{
		if ( !TryHost( out var game ) ) return;
		var length = words.Length;
		while ( length > 0 && words[length - 1].Length == 0 ) length--;
		words = words[..length];
		// Whoever has the console is on the record too.
		game.Journal!.Record( JournalKinds.StaffCommand, Actor.Console, data: ("line", CommandLine.Line( words )) );
		foreach ( var line in Execute( game, Actor.Console, null, words ) ) Log.Info( line );
	}

	/// <summary>Host: carries out one command and returns what to tell whoever gave it.</summary>
	public static IReadOnlyList<string> Execute( HexagonServices game, Actor by, Player? asker, string[] words )
	{
		if ( words.Length == 0 ) return new[] { Help };
		var journal = game.Journal!;
		var roster = game.Roster!;
		var characters = game.Characters!;
		string Word( int index ) => index < words.Length ? words[index] : string.Empty;
		// Anyone alive, in the city or not. The dead are only restored.
		CharacterData? Character( string name ) => name.Length == 0 ? null : roster.Named( name ) is { IsDead: false } living ? living : null;
		// Moving someone, or hurting them, needs them in the city.
		Player? Find( string name ) => Character( name ) is { } character ? Player.HostLoaded( character.Id ) : null;

		switch ( words[0].ToLowerInvariant() )
		{
			case "whitelist":
			case "unwhitelist":
			{
				if ( !long.TryParse( Word( 1 ), out var id ) || id <= 0 ) return new[] { $"Usage: {words[0]} <steamid64> <faction>" };
				if ( Faction( Word( 2 ) ) is not { } faction ) return new[] { NoFaction( Word( 2 ) ) };
				var account = game.Account( id );
				var adding = words[0].Equals( "whitelist", StringComparison.OrdinalIgnoreCase );
				account.Whitelists.Remove( faction.ResourcePath );
				if ( adding ) account.Whitelists.Add( faction.ResourcePath );
				game.SaveAccount( account );
				journal.Record( $"operator.{words[0].ToLowerInvariant()}", by, $"account:{id}", data: ("faction", faction.ResourceName) );
				var reply = new List<string> { $"{id} may {(adding ? "now" : "no longer")} create {faction.Title} characters." };
				if ( adding ) return reply;
				// A whitelist taken away takes the faction's characters out of the city with it.
				foreach ( var player in game.Scene.GetAllComponents<Player>().Where( value => value.HostCharacter is { } held && held.SteamId == id && held.Faction == faction.ResourcePath ).ToArray() )
				{
					reply.Add( $"{player.HostCharacter!.Name} was sent back to the menu." );
					player.HostSendToMenu( $"You are no longer whitelisted for {faction.Title}." );
				}
				return reply;
			}
			case "give":
			{
				if ( Character( Word( 1 ) ) is not { } target ) return new[] { "Usage: give \"<living character's name>\" <item> [count] [pattern]." };
				// An operator's gift is new matter entering the world, so it comes from a named source.
				if ( Item( Word( 2 ) ) is not { } definition ) return new[] { $"No item named '{Word( 2 )}'." };
				var count = int.TryParse( Word( 3 ), out var asked ) ? Math.Clamp( asked, 1, 100 ) : 1;
				string? pattern = null;
				if ( definition.IsKey )
				{
					// A key is cut when it is made, so staff say to what.
					var cut = Keys.Pattern( Word( 4 ) );
					if ( !cut.Ok ) return new[] { $"Usage: give \"<name>\" {definition.ResourceName} <count> <pattern>. {cut.Message}" };
					pattern = cut.Value;
				}
				var given = definition.IssueTo( game.Transfers!, Sources.Operator, target, by, count, pattern );
				if ( given.Ok ) Changed( game, target );
				return new[] { given.Ok ? $"Gave {(count > 1 ? $"{count} " : string.Empty)}{definition.Title}{(pattern is null ? string.Empty : $" cut to {pattern}")} to {target.Name}." : given.Message };
			}
			case "take":
			{
				// Out of the world on the record, as when staff undo something that should not have happened.
				if ( Character( Word( 1 ) ) is not { } target ) return new[] { "Usage: take \"<living character's name>\" <item> [count]." };
				if ( Item( Word( 2 ) ) is not { } definition ) return new[] { $"No item named '{Word( 2 )}'." };
				var removed = Remove( Word( 3 ), target, definition, ( pile, count ) => game.Transfers!.Destroy( Sinks.Operator, target, pile.Id, by, count ) );
				if ( removed == 0 ) return new[] { $"{target.Name} has no {definition.Title}." };
				Changed( game, target );
				return new[] { $"Took {removed} {definition.Title} from {target.Name}." };
			}
			case "move":
			{
				// Settling a dispute: what was taken goes back where it came from, by the same transfers as anything else.
				if ( Character( Word( 1 ) ) is not { } from || Character( Word( 2 ) ) is not { } to || from == to )
					return new[] { "Usage: move \"<from living character>\" \"<to living character>\" <item> [count]." };
				if ( Item( Word( 3 ) ) is not { } definition ) return new[] { $"No item named '{Word( 3 )}'." };
				var failed = string.Empty;
				var count = Remove( Word( 4 ), from, definition, ( pile, some ) =>
				{
					var moved = game.Transfers!.Move( from, to, pile.Id, by, some );
					if ( !moved.Ok ) failed = moved.Message;
					return moved;
				} );
				if ( count == 0 ) return new[] { failed.Length > 0 ? failed : $"{from.Name} has no {definition.Title}." };
				var done = $"Moved {count} {definition.Title} from {from.Name} to {to.Name}.{(failed.Length > 0 ? $" The rest did not fit: {failed}" : string.Empty)}";
				game.SaveTogether( from, to );
				characters.Changed( from );
				characters.Changed( to );
				return new[] { done };
			}
			case "teleport":
			{
				if ( Find( Word( 1 ) ) is not { } target || !Coordinate( Word( 2 ), out var x ) || !Coordinate( Word( 3 ), out var y ) || !Coordinate( Word( 4 ), out var z ) )
					return new[] { "Usage: teleport \"<character name>\" <x> <y> <z>. The character must be in the city." };
				return new[] { Move( journal, by, target, new Vector3( x, y, z ) ) };
			}
			case "bring":
			case "goto":
			{
				if ( Find( Word( 1 ) ) is not { } target || asker is null ) return new[] { $"Usage, in game only: {words[0]} \"<character name>\". The character must be in the city." };
				return new[] { words[0].Equals( "bring", StringComparison.OrdinalIgnoreCase )
					? Move( journal, by, target, asker.HostPosition + Vector3.Up * 4f )
					: Move( journal, by, asker, target.HostPosition + Vector3.Up * 4f ) };
			}
			case "hurt":
			{
				if ( Find( Word( 1 ) ) is not { } target || !int.TryParse( Word( 2 ), out var amount ) || amount <= 0 )
					return new[] { "Usage: hurt \"<character name>\" <amount>. The character must be in the city." };
				journal.Record( JournalKinds.OperatorHurt, by, target.HostCharacter!.HolderLabel, data: ("amount", amount.ToString()) );
				characters.Damage( target.HostCharacter!, amount, null, "operator", target.HostPosition );
				return new[] { $"Hurt {target.HostCharacter.Name} for {amount}." };
			}
			case "revive":
			{
				if ( Character( Word( 1 ) ) is not { } character ) return new[] { "Usage: revive \"<character name>\"." };
				journal.Record( JournalKinds.OperatorRevive, by, character.HolderLabel );
				var revived = characters.Revive( character );
				return new[] { revived.Ok ? $"Helped {character.Name} up." : revived.Message };
			}
			case "release":
			{
				if ( Character( Word( 1 ) ) is not { } character ) return new[] { "Usage: release \"<character name>\"." };
				if ( !character.IsRestrained ) return new[] { $"{character.Name}'s hands are not bound." };
				journal.Record( JournalKinds.OperatorRelease, by, character.HolderLabel );
				characters.Release( character );
				return new[] { $"Freed {character.Name}'s hands." };
			}
			case "restore":
			{
				// Undoing a death that broke the rules. The dead keep their names, so the name finds them.
				if ( Word( 1 ).Length == 0 || roster.Named( Word( 1 ) ) is not { } dead ) return new[] { "Usage: restore \"<dead character's name>\"." };
				var restored = characters.Restore( dead, by );
				if ( !restored.Ok ) return new[] { restored.Message };
				journal.Record( JournalKinds.OperatorRestore, by, dead.HolderLabel );
				return new[] { $"{dead.Name} is alive again, and gets up where the body lay." };
			}
			case "faction":
			{
				if ( Character( Word( 1 ) ) is not { } character ) return new[] { "Usage: faction \"<character name>\" <faction>." };
				if ( Faction( Word( 2 ) ) is not { } faction ) return new[] { NoFaction( Word( 2 ) ) };
				var was = FactionDefinition.Find( character.Faction )?.ResourceName ?? character.Faction;
				character.Faction = faction.ResourcePath;
				journal.Record( JournalKinds.OperatorFaction, by, character.HolderLabel, data: new[] { ("from", was), ("to", faction.ResourceName) } );
				Changed( game, character );
				var reply = new List<string> { $"{character.Name} is now {faction.Title}." };
				if ( faction.RequiresWhitelist && !game.Account( character.SteamId ).Whitelists.Contains( faction.ResourcePath ) )
					reply.Add( $"Their account is not whitelisted for {faction.Title}, so once they leave they cannot come back as {character.Name} until it is." );
				return reply;
			}
			case "door":
			{
				var doors = game.Scene.GetAllComponents<Door>().ToArray();
				// Alone, it lists them: in game there is no other way to learn what a door is called.
				if ( words.Length == 1 )
					return doors.Length == 0 ? new[] { "There are no doors." } : doors.Select( value => value.HostDescribe( id => roster.Find( id )?.Name ) ).ToArray();
				var door = doors.FirstOrDefault( value => value.GameObject.Name.Equals( Word( 1 ), StringComparison.OrdinalIgnoreCase ) );
				// Where the action is: after the door's name, or first when the door is left unnamed.
				var at = 2;
				// In game, a door left unnamed is the one beside the staff member.
				if ( door is null && asker is not null && DoorActions.Contains( Word( 1 ).ToLowerInvariant() ) )
				{
					door = doors.Select( value => (Door: value, Distance: value.GameObject.GetBounds().ClosestPoint( asker.HostPosition ).Distance( asker.HostPosition )) )
						.Where( value => value.Distance <= Door.Reach ).OrderBy( value => value.Distance ).Select( value => value.Door ).FirstOrDefault();
					at = 1;
					if ( door is null ) return new[] { "No door is within reach. Stand beside one, or name it; door alone lists them." };
				}
				if ( door is null ) return new[] { $"Usage: door [\"<door>\"] <{string.Join( "|", DoorActions )}>. No door is named '{Word( 1 )}'; door alone lists them." };
				var action = Word( at ).ToLowerInvariant();
				if ( action == "assign" )
				{
					// Staff hand out a building outright, as a player office would sell one.
					if ( Character( Word( at + 1 ) ) is not { } owner ) return new[] { "Usage: door [\"<door>\"] assign \"<living character's name>\"." };
					door.HostAssign( owner, by );
					journal.Record( JournalKinds.OperatorDoor, by, $"Door:{door.GameObject.Id:N}", data: new[] { ("action", action), ("owner", owner.Name) } );
					return new[] { $"{door.GameObject.Name}: assigned to {owner.Name}. The keys are not part of the title; give them separately." };
				}
				var done = door.HostOperate( action, by );
				if ( !done.Ok ) return new[] { done.Message };
				journal.Record( JournalKinds.OperatorDoor, by, $"Door:{door.GameObject.Id:N}", data: ("action", action) );
				return new[] { $"{door.GameObject.Name}: {action}." };
			}
			case "who":
			{
				// The one place a real name is shown to someone who was never told it. It is staff only, and on the record.
				var reply = game.Scene.GetAllComponents<Player>().Where( value => value.Network.Owner is not null ).Select( value =>
					$"{value.Network.Owner!.DisplayName} [{value.Network.Owner.SteamId.ValueUnsigned}] is {value.HostCharacter?.Name ?? "in the menu"}" +
					(value.HostCharacter is null ? string.Empty : $" at {value.HostPosition.x:0},{value.HostPosition.y:0},{value.HostPosition.z:0}") ).ToList();
				foreach ( var sleeper in game.Scene.GetAllComponents<Sleeper>() )
				{
					if ( sleeper.HostCharacter is not { } character ) continue;
					var at = sleeper.WorldPosition;
					reply.Add( $"[{character.SteamId}] is away; {character.Name} is {(character.IsDown ? "down" : "bound")} at {at.x:0},{at.y:0},{at.z:0}" );
				}
				return reply;
			}
			case "journal":
			{
				var count = int.TryParse( Word( 2 ), out var asked ) && asked > 0 ? Math.Min( asked, 50 ) : 20;
				var days = int.TryParse( Word( 3 ), out var back ) && back > 0 ? Math.Min( back, 14 ) : 2;
				var now = DateTimeOffset.UtcNow;
				var found = journal.Search( now, Word( 1 ), count, days );
				if ( found.Count == 0 ) return new[] { $"Nothing in the journal's last {days} days matches." };
				// Times are UTC. Once the answer is not all from today, each day is named.
				var reply = new List<string>();
				var onlyToday = found.All( entry => entry.At.UtcDateTime.Date == now.UtcDateTime.Date );
				DateTime? day = null;
				foreach ( var entry in found )
				{
					if ( !onlyToday && entry.At.UtcDateTime.Date != day )
					{
						day = entry.At.UtcDateTime.Date;
						reply.Add( $"{day:yyyy-MM-dd} (UTC)" );
					}
					reply.Add( entry.Describe() );
				}
				return reply;
			}
			default:
				return new[] { Help };
		}
	}

	private static readonly string[] DoorActions = { "open", "close", "lock", "unlock", "release", "assign" };

	private static FactionDefinition? Faction( string name ) =>
		FactionDefinition.All.FirstOrDefault( value => value.ResourceName.Equals( name, StringComparison.OrdinalIgnoreCase ) );

	private static string NoFaction( string name ) =>
		$"No faction named '{name}'. Known: {string.Join( ", ", FactionDefinition.All.Select( value => value.ResourceName ) )}";

	private static ItemDefinition? Item( string name ) =>
		ResourceLibrary.GetAll<ItemDefinition>().FirstOrDefault( value => value.ResourceName.Equals( name, StringComparison.OrdinalIgnoreCase ) );

	private static bool Coordinate( string text, out float value ) =>
		float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) && float.IsFinite( value );

	/// <summary>Takes up to the asked number of an item from a character, pile by pile, all of them when none is asked. Returns how many went.</summary>
	private static int Remove( string asked, CharacterData from, ItemDefinition definition, Func<ItemStack, int, Result> take ) =>
		CommandLine.TakeFromPiles( asked, from.Inventory.Items.Where( item => item.Definition == definition.ResourcePath ).ToArray(), ( pile, some ) => take( pile, some ).Ok );

	/// <summary>Host: a character staff changed is saved, and whoever can see it is shown the change.</summary>
	private static void Changed( HexagonServices game, CharacterData character )
	{
		game.Roster!.Save( character );
		game.Characters!.Changed( character );
	}

	private static string Move( Journal journal, Actor by, Player target, Vector3 to )
	{
		target.HostTeleport( to );
		journal.Record( JournalKinds.OperatorTeleport, by, target.HostCharacter!.HolderLabel, data: ("to", $"{to.x:0},{to.y:0},{to.z:0}") );
		return $"Moved {target.HostCharacter.Name}.";
	}

	private static bool TryHost( out HexagonServices game )
	{
		game = HexagonServices.Current!;
		if ( Networking.IsHost && game?.Roster is not null ) return true;
		Log.Warning( "This command only works on the host." );
		return false;
	}
}
