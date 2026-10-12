#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A building or a flat: the doors and lockers whose title passes together, the right to sell or rent
/// them. A character holds it, or nobody does and it is its office's: whoever carries a stamp that
/// leaves the office's mark sells it, and the money is handed over into the office's till like
/// anything else (see <see cref="Titles"/>). Holding it opens nothing, since a lock takes its key,
/// owner or not, but its holder may change its locks. Its title is kept with the world by the scene
/// object's id, and nobody but its holder and staff is told who holds it.
/// </summary>
[Title( "Hexagon Property" ), Category( "Hexagon" ), Icon( "home_work" )]
public sealed class Property : Component
{
	/// <summary>What it is called on its title, such as "Flat 2".</summary>
	[Property] public string Title { get; set; } = "Flat";
	/// <summary>The office that holds it while nobody owns it: the mark its stamp leaves, such as "Combine Civil Authority".</summary>
	[Property] public string Office { get; set; } = string.Empty;
	/// <summary>Its doors, whose locks its holder may change.</summary>
	[Property] public List<Door> Doors { get; set; } = new();
	/// <summary>Its lockers, whose locks its holder may change too.</summary>
	[Property] public List<Container> Lockers { get; set; } = new();

	/// <summary>Host: the character who holds its title, or null while its office does.</summary>
	public Guid? HostOwner => HexagonServices.Current is { } game && game.World.Properties.TryGetValue( GameObject.Id, out var data ) ? data.Owner : null;

	/// <summary>Whether a door or a locker is part of it.</summary>
	public bool Covers( Component thing ) => thing switch
	{
		Door door => Doors.Contains( door ),
		Container locker => Lockers.Contains( locker ),
		_ => false
	};

	/// <summary>The building a door or a locker is part of, if any.</summary>
	public static Property? Of( Component thing ) => thing.Scene.GetAllComponents<Property>().FirstOrDefault( value => value.Covers( thing ) );

	/// <summary>Host: the title passes to a character, bought from its office, handed over by its owner, or assigned by staff. The keys do not come with it.</summary>
	public void HostAssign( CharacterData to, Actor by )
	{
		if ( HexagonServices.Current is not { } game ) return;
		var was = HostOwner;
		game.World.Properties[GameObject.Id] = new PropertyData { Owner = to.Id };
		game.SaveWorld();
		var data = new List<(string, string)> { ("property", Title), ("owner", to.Name) };
		data.Add( ("from", was is { } before ? game.Roster?.Find( before )?.Name ?? $"character:{before:N}" : Office) );
		game.Journal?.Record( JournalKinds.PropertyAssign, by, Subject, data: data.ToArray() );
		HostShowTitles();
	}

	/// <summary>Host: it goes back to its office, taken back for it, or because whoever held it died or left for good.</summary>
	public void HostRelease( Actor by, string why )
	{
		if ( HostOwner is not { } owner || HexagonServices.Current is not { } game ) return;
		game.World.Properties[GameObject.Id] = new PropertyData();
		game.SaveWorld();
		var was = game.Roster?.Find( owner );
		game.Journal?.Record( JournalKinds.PropertyRelease, by, Subject,
			data: new[] { ("property", Title), ("owner", was?.Name ?? $"character:{owner:N}"), ("office", Office), ("reason", why) } );
		HostShowTitles();
	}

	/// <summary>Host: a character that leaves the world for good leaves its buildings to their offices.</summary>
	public static void HostReleaseOwnedBy( Guid characterId, Actor by, string why )
	{
		if ( HexagonServices.Current is not { } game ) return;
		foreach ( var property in game.Scene.GetAllComponents<Property>().Where( value => value.HostOwner == characterId ).ToArray() ) property.HostRelease( by, why );
		// A building no longer in the scene is cleared in the saved world instead.
		var saved = game.World.Properties.Values.Where( value => value.Owner == characterId ).ToArray();
		foreach ( var property in saved ) property.Owner = null;
		if ( saved.Length > 0 ) game.SaveWorld();
	}

	/// <summary>
	/// Host, once as the world is restored: a door held on its own, from before buildings had titles,
	/// passes its title to the building it is part of, unless that building's was already settled.
	/// </summary>
	internal static void HostAdopt( HexagonServices game )
	{
		var changed = false;
		foreach ( var property in game.Scene.GetAllComponents<Property>() )
			foreach ( var door in property.Doors.Where( value => value.IsValid() ) )
			{
				if ( !game.World.Doors.TryGetValue( door.GameObject.Id, out var saved ) || saved.Owner is not { } owner ) continue;
				if ( !game.World.Properties.ContainsKey( property.GameObject.Id ) ) game.World.Properties[property.GameObject.Id] = new PropertyData { Owner = owner };
				saved.Owner = null;
				changed = true;
			}
		if ( changed ) game.SaveWorld();
	}

	/// <summary>One line for staff: what it is called, who holds it, and what it takes in.</summary>
	internal string HostDescribe( Func<Guid, string?> nameOf ) =>
		$"{Title}: " + (HostOwner is { } owner ? $"owned by {nameOf( owner ) ?? $"character:{owner:N}"}" : $"held by {(Office.Length > 0 ? Office : "no office")}") +
		$"; doors {Listed( Doors.Where( value => value.IsValid() ).Select( value => value.GameObject.Name ) )}" +
		$", lockers {Listed( Lockers.Where( value => value.IsValid() ).Select( value => value.GameObject.Name ) )}";

	private static string Listed( IEnumerable<string> names ) => names.Any() ? string.Join( ", ", names ) : "none";

	private string Subject => $"Property:{GameObject.Id:N}";

	// Whoever holds a title, or carries its office's stamp, is shown the titles they act for.
	private static void HostShowTitles()
	{
		foreach ( var player in Game.ActiveScene.GetAllComponents<Player>() ) player.SendPrivateState();
	}
}
