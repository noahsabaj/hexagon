#nullable enable

using System;
using System.Linq;
using Sandbox;

namespace Hexagon.Dev;

/// <summary>
/// Lets the editor's automation play the game. It is never placed in a scene: a test adds it at
/// runtime and writes <see cref="Command"/>, and each command calls the same client request the
/// HUD would, so it travels the real RPC path and is judged by the real host rules. It refuses to
/// run outside the editor. The automation attaches it to the editor's copy of the scene, so it
/// always reaches the running game through <c>Game.ActiveScene</c> and <c>Player.Local</c>.
/// </summary>
[Title( "Hexagon Dev Driver" ), Category( "Hexagon" ), Icon( "smart_toy" )]
public sealed class DevDriver : Component
{
	private string _command = string.Empty;

	/// <summary>What happened to the last command, for the automation to read back.</summary>
	[Property] public string LastResult { get; set; } = string.Empty;

	/// <summary>Arguments are separated by '|', for example <c>create|John Doe|A tired resident of the city.|citizen</c>.</summary>
	[Property]
	public string Command
	{
		get => _command;
		set
		{
			_command = value ?? string.Empty;
			if ( _command.Length == 0 ) return;
			LastResult = DevCommands.Run( _command.Split( '|' ) );
			// The automation reads results back from the console.
			Log.Info( $"[dev] {_command} => {LastResult}" );
		}
	}
}

/// <summary>
/// The commands a test plays the game with. In the editor they arrive through <see cref="DevDriver"/>.
/// Against a dedicated server they arrive through the server: <see cref="PollInbox"/> or
/// <c>hexagon_dev_send</c> relays one to a client, which runs it as its own player and reports back. Both ends must have been
/// started with <c>+hexagon_dev 1</c>, so a normal server cannot drive a client and a normal client
/// cannot be driven.
/// </summary>
public static class DevCommands
{
	[ConVar( "hexagon_dev" )]
	public static bool Enabled { get; set; }

	/// <summary>Server console: <c>hexagon_dev_send 1 "say|Hello"</c>. Client 0 is the server itself; clients count from 1 in join order.</summary>
	[ConCmd( "hexagon_dev_send" )]
	public static void Send( int client, string command )
	{
		if ( !Networking.IsHost || !Enabled )
		{
			Log.Warning( "hexagon_dev_send needs a host started with +hexagon_dev 1." );
			return;
		}
		if ( client == 0 )
		{
			Log.Info( $"[dev] 0 {command} => {Run( command.Split( '|' ) )}" );
			return;
		}
		var target = GameManager.Instance?.Clients.ElementAtOrDefault( client - 1 );
		if ( target is null )
		{
			Log.Info( $"[dev] {client} {command} => no such client ({GameManager.Instance?.Clients.Count} connected)" );
			return;
		}
		using ( Rpc.FilterInclude( target ) ) Relay( client, command );
	}

	private static RealTimeSince _sincePoll;
	private static bool _announced;

	/// <summary>
	/// Host: a dedicated server does not read a redirected console, so a test leaves commands as files
	/// in the data folder's <c>dev-inbox</c>, one <c>client|command</c> per line, and reads the
	/// replies from the server's output.
	/// </summary>
	public static void PollInbox()
	{
		if ( !Enabled || !Networking.IsHost || _sincePoll < 0.25f ) return;
		_sincePoll = 0;
		var folder = $"{SandboxFileStore.Root}/dev-inbox";
		if ( !_announced )
		{
			// Created up front and announced, so the test can find where this server keeps its data.
			_announced = true;
			FileSystem.Data.CreateDirectory( folder );
			Log.Info( $"[dev] inbox at {FileSystem.Data.GetFullPath( folder )}" );
		}
		foreach ( var file in FileSystem.Data.FindFile( folder, "*.txt" ).OrderBy( value => value ) )
		{
			var path = $"{folder}/{file}";
			var lines = FileSystem.Data.ReadAllText( path ).Split( (char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
			FileSystem.Data.DeleteFile( path );
			foreach ( var line in lines )
			{
				var bar = line.IndexOf( '|' );
				if ( bar > 0 && int.TryParse( line[..bar], out var client ) ) Send( client, line[(bar + 1)..] );
			}
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	private static void Relay( int client, string command )
	{
		if ( Networking.IsHost ) return;
		Report( client, command, Run( command.Split( '|' ) ) );
	}

	[Rpc.Host( NetFlags.Reliable )]
	private static void Report( int client, string command, string result )
	{
		if ( Enabled ) Log.Info( $"[dev] {client} {command} => {result}" );
	}

	public static string Run( string[] args )
	{
		if ( !Game.IsEditor && !Enabled ) return "refused: not a dev session";
		if ( args[0] == "who" )
			return $"local={Player.Local is not null} connection={Connection.Local?.DisplayName} host={Networking.IsHost} active={Networking.IsActive} scene={Game.ActiveScene?.Name} pawns=[" +
				string.Join( ", ", Game.ActiveScene?.GetAllComponents<Player>().Select( value =>
					$"{value.GameObject.Name}: proxy={value.IsProxy} owner={value.Network.Owner?.DisplayName} netactive={value.Network.Active} claimed={value.WorldPosition} believed={value.HostPosition}" ) ?? Array.Empty<string>() ) + "]";
		if ( args[0] == "journal" )
		{
			// Today's journal as kind=count, refusals marked, so a test can assert what was remembered.
			var entries = GameManager.Instance?.Journal?.Read( DateTimeOffset.UtcNow );
			if ( entries is null ) return "no journal";
			// journal|<kind> describes the latest entry of that kind instead.
			if ( args.Length > 1 && !args[1].StartsWith( '#' ) )
				return entries.LastOrDefault( value => value.Kind == args[1] ) is { } last
					? $"ok={last.Ok} actor='{last.ActorName}' witnesses={last.Witnesses.Count} subject='{last.Subject}'"
					: "no such entry";
			return string.Join( " ", entries.GroupBy( value => value.Kind + (value.Ok ? "" : "!") ).Select( group => $"{group.Key}={group.Count()}" ) );
		}
		if ( args[0] == "console" )
		{
			// Runs a real console command, so operator commands are tested as an operator types them.
			// Never for a remote host: that would hand a server this machine's console.
			if ( !Game.IsEditor && !Networking.IsHost ) return "refused: not on a remote client";
			ConsoleSystem.Run( args[1] );
			return "ran";
		}
		if ( args[0] == "hostid" ) return $"local={Connection.Local?.SteamId.ValueUnsigned} host={Connection.Host?.SteamId.ValueUnsigned} address={Connection.Local?.Address} name={Connection.Local?.DisplayName}";
		if ( args[0] == "convar" ) return ConsoleSystem.GetValue( args[1] ) ?? "unset";
		if ( Player.Local is not { } player ) return "no local player";
		try
		{
			switch ( args[0] )
			{
				case "create":
					var faction = FactionDefinition.All.FirstOrDefault( value => value.ResourceName == args[3] );
					player.RequestCreateCharacter( args[1], args[2], faction?.ResourcePath ?? args[3] );
					return "sent create";
				case "enter":
					var target = player.Characters.FirstOrDefault( value => value.Name == args[1] );
					if ( target is null ) return $"no character named '{args[1]}' in: {string.Join( ", ", player.Characters.Select( value => value.Name ) )}";
					player.RequestEnterCity( target.Id );
					return "sent enter";
				case "leave":
					player.RequestLeaveCity();
					return "sent leave";
				case "delete":
					var doomed = player.Characters.FirstOrDefault( value => value.Name == args[1] );
					if ( doomed is null ) return "no such character";
					player.RequestDeleteCharacter( doomed.Id );
					return "sent delete";
				case "say":
					player.RequestSay( args[1] );
					return "sent say";
				case "move":
					var item = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( item is null ) return "no such item";
					player.RequestMoveItem( item.Id, int.Parse( args[2] ), int.Parse( args[3] ) );
					return "sent move";
				case "tune":
					var radio = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( radio is null ) return "no such item";
					player.RequestTune( radio.Id, args[2] );
					return "sent tune";
				case "split":
					var whole = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( whole is null ) return "no such item";
					player.RequestSplitItem( whole.Id, int.Parse( args[2] ) );
					return "sent split";
				case "merge":
					var part = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					var onto = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[2] ) );
					if ( part is null || onto is null ) return "no such item";
					player.RequestMoveItem( part.Id, onto.X, onto.Y );
					return "sent merge";
				case "door":
					var door = Game.ActiveScene.GetAllComponents<Door>().FirstOrDefault( value => value.GameObject.Name == args[1] );
					if ( door is null ) return "no such door";
					player.RequestAct( door, $"door.{args[2]}" );
					return $"sent door {args[2]}";
				case "drop":
				case "use":
					var held = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( held is null ) return "no such item";
					player.RequestItemAct( held.Id, $"item.{args[0]}" );
					return $"sent {args[0]}";
				case "pickup":
					var lying = Game.ActiveScene.GetAllComponents<WorldItem>().FirstOrDefault( value => value.Definition?.Title == args[1] );
					if ( lying is null ) return "nothing like that on the ground";
					player.RequestAct( lying, "item.take" );
					return "sent pickup";
				case "open":
					Component? container = Game.ActiveScene.GetAllComponents<Container>().FirstOrDefault( value => value.GameObject.Name == args[1] );
					container ??= Game.ActiveScene.GetAllComponents<Vendor>().FirstOrDefault( value => value.GameObject.Name == args[1] );
					if ( container is null ) return "no such container or vendor";
					player.RequestAct( container, ((IVerbTarget)container).Verbs[0].Id );
					return "sent open";
				case "take":
					var wanted = player.OpenInventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( wanted is null ) return "no such item";
					player.RequestTake( wanted.Id );
					return "sent take";
				case "put":
					var given = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( given is null ) return "no such item";
					player.RequestPut( given.Id );
					return "sent put";
				case "close":
					player.RequestCloseHolder();
					return "sent close";
				case "act":
					// A verb on the nearest other character, or on a body.
					Component? subject = args[1].StartsWith( "corpse." )
						? Game.ActiveScene.GetAllComponents<Corpse>().FirstOrDefault()
						: Game.ActiveScene.GetAllComponents<Player>().Where( value => value.IsProxy && value.HasCharacter )
							.OrderBy( value => value.WorldPosition.Distance( player.WorldPosition ) ).FirstOrDefault();
					if ( subject is null ) return "nobody there";
					player.RequestAct( subject, args[1] );
					return $"sent {args[1]}";
				case "attack":
					var foe = Game.ActiveScene.GetAllComponents<Player>().Where( value => value.IsProxy && value.HasCharacter )
						.OrderBy( value => value.WorldPosition.Distance( player.WorldPosition ) ).FirstOrDefault();
					player.RequestAttack( foe is null ? Vector3.Forward : foe.WorldPosition - player.WorldPosition );
					return "sent attack";
				case "equip":
					var inHand = player.Inventory?.Items.ElementAtOrDefault( int.Parse( args[1] ) );
					if ( inHand is null ) return "no such item";
					player.RequestItemAct( inHand.Id, "item.equip" );
					return "sent equip";
				case "pay":
					var payee = Game.ActiveScene.GetAllComponents<Player>().Where( value => value.IsProxy && value.HasCharacter )
						.OrderBy( value => value.WorldPosition.Distance( player.WorldPosition ) ).FirstOrDefault();
					if ( payee is null ) return "nobody there";
					player.RequestPay( payee, long.Parse( args[1] ) );
					return "sent pay";
				case "staff":
					player.RequestStaff( args[1] );
					return "sent staff";
				case "taketokens":
					player.RequestTakeTokens();
					return "sent taketokens";
				case "introduce":
					var stranger = Game.ActiveScene.GetAllComponents<Player>().FirstOrDefault( value => value.IsProxy && value.HasCharacter );
					if ( stranger is null ) return "nobody to meet";
					player.RequestAct( stranger, "person.introduce" );
					return "sent introduce";
				case "proxies":
					// What this client was told about everyone else. Names and factions must be empty.
					return string.Join( " ; ", Game.ActiveScene.GetAllComponents<Player>().Where( value => value.IsProxy )
						.Select( value => $"down={value.IsDown} restrained={value.IsRestrained} held='{ItemDefinition.Find( value.HeldItemPath )?.Title}' health={value.Health} has={value.HasCharacter} name='{value.CharacterName}' faction='{value.FactionPath}' seen='{value.CharacterDescription}' label='{player.LabelFor( value )}'" ) );
				case "steamid":
					return Connection.Local.SteamId.ValueUnsigned.ToString();
				case "net":
					return string.Join( " ; ", Game.ActiveScene.GetAllComponents<Door>().Select( value =>
						$"{value.GameObject.Name}: active={value.Network.Active} proxy={value.IsProxy} owner={value.Network.Owner?.DisplayName} mode={value.GameObject.NetworkMode}" ) )
						+ $" | host={Networking.IsHost} netactive={Networking.IsActive} manager={GameManager.Instance is not null} roster={GameManager.Instance?.Roster is not null}";
				case "goto":
					player.WorldPosition = new Vector3( float.Parse( args[1] ), float.Parse( args[2] ), float.Parse( args[3] ) );
					return "moved";
				case "state":
					return $"has={player.HasCharacter} name='{player.CharacterName}' faction='{player.Faction?.Title}' tokens={player.Tokens} " +
						$"characters=[{string.Join( ", ", player.Characters.Select( value => value.Name ) )}] " +
						$"items=[{string.Join( ", ", player.Inventory?.Items.Select( value => $"{Pile( value )}@{value.X},{value.Y}" ) ?? Array.Empty<string>() )}] " +
						$"doors=[{string.Join( ", ", Game.ActiveScene.GetAllComponents<Door>().Select( value => $"{value.GameObject.Name}:open={value.IsOpen},locked={value.IsLocked}{(value.IsOwned ? ",owned" : "")}" ) )}] " +
						$"health={player.Health} down={player.IsDown} restrained={player.IsRestrained} held='{ItemDefinition.Find( player.HeldItemPath )?.Title}' bodies={Game.ActiveScene.GetAllComponents<Corpse>().Count()} opentokens={player.OpenTokens} " +
						$"staff={player.IsStaff} said=[{string.Join( " / ", player.StaffLines.TakeLast( 4 ) )}] " +
						$"known=[{string.Join( ", ", player.Known.Values.OrderBy( value => value ) )}] " +
						$"open=[{player.OpenTitle}: {string.Join( ", ", player.OpenInventory?.Items.Select( Pile ) ?? Array.Empty<string>() )}] " +
						$"ground=[{string.Join( ", ", Game.ActiveScene.GetAllComponents<WorldItem>().Select( value => value.Definition?.Title ).OrderBy( value => value ) )}] " +
						$"pos={player.WorldPosition} chat=[{string.Join( " / ", Chat.Lines.TakeLast( 4 ).Select( value => value.Text ) )}]";
				default:
					return $"unknown command '{args[0]}'";
			}
		}
		catch ( Exception exception )
		{
			return $"threw: {exception.Message}";
		}
	}

	private static string Pile( Hexagon.Logic.ItemStack value ) =>
		$"{ItemDefinition.Find( value.Definition )?.Title}{(value.Count > 1 ? $"x{value.Count}" : string.Empty)}{(value.Frequency is { } frequency ? $"~{frequency}" : string.Empty)}";
}
