#nullable enable

using System;

namespace Hexagon.Logic;

/// <summary>
/// Whether a player may do something with one of their characters from the menu: enter the city with
/// it, retire it, or delete it. Each answer is null when allowed, or what to tell the player. "Not
/// yours" and "does not exist" get the same answer, so character ids cannot be probed.
/// </summary>
public static class Admission
{
	public const string NotFound = "Character was not found.";

	/// <summary>
	/// Entering the city. <paramref name="inCity"/>: the asking pawn already has a character in it.
	/// <paramref name="loadedElsewhere"/>: this character is in the city on some connection.
	/// <paramref name="whitelistedOut"/>: the faction is whitelisted and the account no longer is, with its title.
	/// </summary>
	public static string? ToEnter( CharacterData? character, long steamId, DateTimeOffset now, bool inCity, bool loadedElsewhere, string? whitelistedOut ) =>
		inCity ? "Leave the city first."
		: character is null || character.SteamId != steamId ? NotFound
		: character.IsDead ? $"{character.Name} is dead."
		// Dying and making someone new does not put a player back in the scene at once.
		: character.ArrivesAt is { } arrives && arrives > now ? $"{character.Name} arrives in the city {Until( arrives - now )}."
		: loadedElsewhere ? "That character is already in the city."
		// A whitelist taken away takes the faction's characters with it until it is given back.
		: whitelistedOut is not null ? $"You are no longer whitelisted for {whitelistedOut}."
		: null;

	/// <summary>Ending a life that has been lived. <paramref name="loaded"/>: it is in the city, on any connection.</summary>
	public static string? ToRetire( CharacterData? character, long steamId, bool loaded ) =>
		character is null || character.SteamId != steamId ? NotFound
		: character.IsDead ? $"{character.Name} is already dead."
		: character.FirstEntered is null ? $"{character.Name} never entered the city, so it is deleted, not retired."
		// Two copies of the game on one account are still one account.
		: loaded ? "Leave the city with that character before retiring it."
		// Someone lying in the street, or bound, is still part of a scene that is not over.
		: character.IsDown || character.IsRestrained ? "That character cannot be retired while it is down or bound."
		: null;

	/// <summary>Deleting a character that never lived, which frees its name. One that has lived is retired instead.</summary>
	public static string? ToDelete( CharacterData? character, long steamId ) =>
		character is null || character.SteamId != steamId ? NotFound
		: character.FirstEntered is not null || character.IsDead ? $"{character.Name} has lived in the city. A life is ended by retiring, not by deleting."
		: null;

	/// <summary>How long is left, roughly, as a player would say it.</summary>
	public static string Until( TimeSpan left )
	{
		if ( left.TotalHours > 1 ) return $"in {Math.Ceiling( left.TotalHours ):0} hours";
		if ( left.TotalMinutes > 1 ) return $"in {Math.Ceiling( left.TotalMinutes ):0} minutes";
		return "in a minute";
	}
}
