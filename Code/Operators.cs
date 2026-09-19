#nullable enable

using System;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// Server console commands. They run in the host's console, so there is no in-game operator
/// account to steal or spoof; whoever can type into the server console already owns the server.
/// Every one is journaled: staff are on the record like everyone else.
/// </summary>
public static class Operators
{
	[ConCmd( "hexagon_whitelist" )]
	public static void Whitelist( string steamId, string faction )
	{
		if ( !TryHost( out var game ) ) return;
		if ( !long.TryParse( steamId, out var id ) || id <= 0 )
		{
			Log.Warning( "Usage: hexagon_whitelist <steamid64> <faction>" );
			return;
		}
		if ( FindFaction( faction ) is not { } definition ) return;
		var account = game.Account( id );
		if ( !account.Whitelists.Contains( definition.ResourcePath ) ) account.Whitelists.Add( definition.ResourcePath );
		game.SaveAccount( account );
		game.Journal!.Record( "operator.whitelist", Actor.Console, $"account:{id}", data: ("faction", definition.ResourceName) );
		Log.Info( $"{id} may now create {definition.Title} characters." );
	}

	[ConCmd( "hexagon_unwhitelist" )]
	public static void Unwhitelist( string steamId, string faction )
	{
		if ( !TryHost( out var game ) || !long.TryParse( steamId, out var id ) ) return;
		if ( FindFaction( faction ) is not { } definition ) return;
		var account = game.Account( id );
		account.Whitelists.Remove( definition.ResourcePath );
		game.SaveAccount( account );
		game.Journal!.Record( "operator.unwhitelist", Actor.Console, $"account:{id}", data: ("faction", definition.ResourceName) );
		Log.Info( $"{id} may no longer create {definition.Title} characters." );
	}

	[ConCmd( "hexagon_give" )]
	public static void Give( string characterName, string item )
	{
		if ( !TryHost( out var game ) ) return;
		var target = game.Scene.GetAllComponents<Player>()
			.FirstOrDefault( value => value.HostCharacter?.Name.Equals( characterName, StringComparison.OrdinalIgnoreCase ) == true );
		var definition = ResourceLibrary.GetAll<ItemDefinition>()
			.FirstOrDefault( value => value.ResourceName.Equals( item, StringComparison.OrdinalIgnoreCase ) );
		if ( target is null || definition is null )
		{
			Log.Warning( "Usage: hexagon_give \"<character name>\" <item>. The character must be in the city." );
			return;
		}
		// An operator's gift is new matter entering the world, so it comes from a named source.
		var given = target.HostIssue( Sources.Operator, definition, Actor.Console );
		Log.Info( given.Ok ? $"Gave {definition.Title} to {target.HostCharacter!.Name}." : given.Message );
	}

	private static bool TryHost( out GameManager game )
	{
		game = GameManager.Instance!;
		if ( Networking.IsHost && game?.Roster is not null ) return true;
		Log.Warning( "This command only works on the host." );
		return false;
	}

	private static FactionDefinition? FindFaction( string name )
	{
		var definition = FactionDefinition.All.FirstOrDefault( value => value.ResourceName.Equals( name, StringComparison.OrdinalIgnoreCase ) );
		if ( definition is null ) Log.Warning( $"No faction named '{name}'. Known: {string.Join( ", ", FactionDefinition.All.Select( value => value.ResourceName ) )}" );
		return definition;
	}
}
