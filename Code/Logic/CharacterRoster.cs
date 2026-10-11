#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// How a game limits lives: how many characters an account may have alive at once, and how long a
/// new one waits to enter the city after the death before it.
/// </summary>
public sealed record LifeRules( int Living, TimeSpan Wait )
{
	/// <summary>One life at a time, and a day between them.</summary>
	public static LifeRules Default { get; } = new( 1, TimeSpan.FromHours( 24 ) );
}

/// <summary>
/// Every character on the server, living and dead, held in memory and written through to the store.
/// A roleplay server has thousands of characters at most, so the whole set is the index. The dead
/// are kept, names and all: nobody comes back as the same person, and staff can restore someone
/// killed against the rules.
/// </summary>
public sealed class CharacterRoster
{
	private readonly DocumentStore _store;
	private readonly Dictionary<Guid, CharacterData> _characters = new();
	// A name's key never changes, so it is computed once when the character enters the roster.
	private readonly Dictionary<string, Guid> _nameKeys = new( StringComparer.Ordinal );

	public CharacterRoster( DocumentStore store )
	{
		_store = store ?? throw new ArgumentNullException( nameof(store) );
		foreach ( var character in _store.LoadAll<CharacterData>( "characters" ) )
		{
			_characters[character.Id] = character;
			_nameKeys[NameRules.Key( character.Name )] = character.Id;
		}
	}

	public int Count => _characters.Count;
	public IReadOnlyCollection<CharacterData> All => _characters.Values;

	public CharacterData? Find( Guid id ) => _characters.TryGetValue( id, out var character ) ? character : null;

	/// <summary>The character with exactly this name, ignoring case, for staff who know who they mean.</summary>
	public CharacterData? Named( string name ) =>
		_characters.Values.FirstOrDefault( value => value.Name.Equals( name.Trim(), StringComparison.OrdinalIgnoreCase ) );

	/// <summary>Whether this is a character of the roster, and not one deleted since it was loaded.</summary>
	public bool Contains( CharacterData character ) => _characters.TryGetValue( character.Id, out var held ) && ReferenceEquals( held, character );

	public IReadOnlyList<CharacterData> OwnedBy( long steamId ) =>
		_characters.Values.Where( value => value.SteamId == steamId ).OrderBy( value => value.CreatedAt ).ToArray();

	/// <summary>
	/// A new character, made at once, but entering the city only once the wait after the account's
	/// last death has passed: dying and making someone new does not put a player back in the scene.
	/// </summary>
	public Result<CharacterData> Create( long steamId, string? name, string? description, string faction, DateTimeOffset now, LifeRules? rules = null )
	{
		rules ??= LifeRules.Default;
		var cleanName = NameRules.NormalizeName( name );
		if ( !cleanName.Ok ) return Result<CharacterData>.Fail( cleanName.Code, cleanName.Message );
		var cleanDescription = NameRules.NormalizeDescription( description );
		if ( !cleanDescription.Ok ) return Result<CharacterData>.Fail( cleanDescription.Code, cleanDescription.Message );
		var owned = OwnedBy( steamId );
		if ( owned.Count( value => !value.IsDead ) >= rules.Living )
			return Result<CharacterData>.Fail( ErrorCode.Conflict, rules.Living == 1 ? "You already have a living character." : $"You already have {rules.Living} living characters." );
		var key = NameRules.Key( cleanName.Value );
		if ( key.Length == 0 ) return Result<CharacterData>.Fail( ErrorCode.Invalid, "Name needs at least one letter or digit." );
		if ( _nameKeys.ContainsKey( key ) )
			return Result<CharacterData>.Fail( ErrorCode.Conflict, "Another character already uses this name or one that reads like it." );

		// However many characters are made and deleted meanwhile, the wait counts from the death.
		var arrives = owned.Max( value => value.DiedAt ) + rules.Wait;
		var character = new CharacterData
		{
			Id = Guid.NewGuid(),
			SteamId = steamId,
			Name = cleanName.Value,
			Description = cleanDescription.Value,
			Faction = faction,
			CreatedAt = now,
			ArrivesAt = arrives > now ? arrives : null
		};
		_store.Save( PathOf( character.Id ), character );
		_characters[character.Id] = character;
		_nameKeys[key] = character.Id;
		return Result<CharacterData>.Success( character );
	}

	/// <summary>
	/// Writes the character's current state. Call after every change the player should keep. A
	/// character deleted while something still held it is not written back: false, and nothing saved.
	/// </summary>
	public bool Save( CharacterData character )
	{
		if ( !Contains( character ) ) return false;
		_store.Save( PathOf( character.Id ), character );
		return true;
	}

	/// <summary>
	/// Staff undo a death that broke the rules. Refused while the account already has as many living
	/// characters as it may: the newer one is deleted, or retired, first. The character is changed in
	/// memory, and the caller saves it with the body it takes its things back from.
	/// </summary>
	public Result Restore( CharacterData character, LifeRules? rules = null )
	{
		rules ??= LifeRules.Default;
		if ( !Contains( character ) ) return Result.Fail( ErrorCode.NotFound, "Character was not found." );
		if ( !character.IsDead ) return Result.Fail( ErrorCode.Conflict, $"{character.Name} is not dead." );
		var living = OwnedBy( character.SteamId ).Where( value => !value.IsDead ).Select( value => value.Name ).ToArray();
		if ( living.Length >= rules.Living )
			return Result.Fail( ErrorCode.Conflict, $"Their account already has {string.Join( ", ", living )} alive. That character is deleted or retired first." );
		return Vitals.Restore( character );
	}

	/// <summary>
	/// For a character that never lived: one that has entered the city leaves it only by dying or
	/// retiring, and is kept. The caller empties it through <see cref="Transfers.DestroyAll"/> first.
	/// </summary>
	public Result Delete( long steamId, Guid id )
	{
		// Same answer for "not yours" and "does not exist", so ids cannot be probed.
		if ( Find( id ) is not { } character || character.SteamId != steamId )
			return Result.Fail( ErrorCode.NotFound, "Character was not found." );
		if ( character.FirstEntered is not null || character.IsDead )
			return Result.Fail( ErrorCode.Conflict, $"{character.Name} has lived in the city. A life is ended by retiring, not by deleting." );
		_store.Delete( PathOf( id ) );
		_characters.Remove( id );
		_nameKeys.Remove( NameRules.Key( character.Name ) );
		return Result.Success();
	}

	/// <summary>Where a character's document is kept, for saving it together with others.</summary>
	public static string PathOf( Guid id ) => $"characters/{id:N}.json";
}
