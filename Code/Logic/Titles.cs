#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Hexagon.Logic;

/// <summary>
/// The title to a building or a flat is the right to sell or rent it, and nothing more: it opens no
/// lock. A character holds it, or nobody does, and then it is its office's. An office is not a team
/// but a stamp: whoever carries a stamp that leaves the office's mark acts for it, to sell what it
/// holds or to take a title back for it, as a stolen stamp does too. The money for a sale is handed
/// over like anything else, into the office's till; the title and the money never move as one.
/// </summary>
public static class Titles
{
	public const string NotYours = "It is not yours to sell.";
	public const string NotYoursToChange = "It is not yours to change: only whoever holds its title fits a new lock.";
	public const string NothingToFit = "Only a lock is fitted to a door or a locker.";
	public const string NothingThere = "There is no door or locker in front of you to fit it to.";
	public const string DoorShut = "Open the door first: a lock is changed from its edge.";
	public const string LockerLocked = "You cannot get at its lock: it is locked, and you have no key that fits.";

	/// <summary>
	/// Whether a character acts for whoever holds a title: it is the owner, or nobody owns it and the
	/// character carries a stamp that leaves its office's mark.
	/// </summary>
	public static bool ActsFor( Guid? owner, string office, Guid character, IEnumerable<string> marks ) =>
		owner is { } held ? held == character : office.Length > 0 && marks.Any( mark => SameOffice( mark, office ) );

	/// <summary>Whether a stamp's mark is an office's: the same words, whatever their case.</summary>
	public static bool SameOffice( string mark, string office ) => string.Equals( mark.Trim(), office.Trim(), StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Why a lock cannot be fitted, or null when it can: it must be a lock, and in front of the fitter
	/// there must be a door or a locker of a building whose title they act for, which they can get
	/// at: a door standing open, or a locker they could open.
	/// </summary>
	public static string? ToFitLock( bool isLock, bool there, bool actsFor, bool isDoor, bool open ) =>
		!isLock ? NothingToFit : !there ? NothingThere : !actsFor ? NotYoursToChange : open ? null : isDoor ? DoorShut : LockerLocked;
}
