#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Hexagon.Logic;

/// <summary>One thing the host decided. Written once and never edited.</summary>
public sealed class JournalEntry
{
	public DateTimeOffset At { get; set; }
	/// <summary>What happened, as a dotted name such as <c>item.move</c> or <c>verb.door.lock</c>.</summary>
	public string Kind { get; set; } = string.Empty;
	/// <summary>False when the host refused. Refusals are kept: a run of them is how spoofing shows up.</summary>
	public bool Ok { get; set; } = true;
	/// <summary>The account that asked. Zero is the server console.</summary>
	public long Account { get; set; }
	public Guid? Actor { get; set; }
	public string? ActorName { get; set; }
	/// <summary>What it was done to: a holder, an item, a door.</summary>
	public string? Subject { get; set; }
	/// <summary>When it was done to a character, that character's name as it was then.</summary>
	public string? SubjectName { get; set; }
	public float[]? Where { get; set; }
	/// <summary>Characters close enough to have seen it.</summary>
	public List<Guid> Witnesses { get; set; } = new();
	/// <summary>Their names as they were then, in the same order. The ids stay the evidence; staff read these.</summary>
	public List<string> WitnessNames { get; set; } = new();
	public Dictionary<string, string> Data { get; set; } = new();

	/// <summary>One line a person can read.</summary>
	public string Describe()
	{
		var who = ActorName ?? (Account == 0 ? "console" : Account.ToString());
		var subject = Subject is null ? "" : SubjectName is null ? $" on {Subject}" : $" on {SubjectName} ({Subject})";
		var details = string.Join( " ", Data.Select( pair => $"{pair.Key}={pair.Value}" ) );
		var seen = Witnesses.Count == 0 ? ""
			: WitnessNames.Count == Witnesses.Count ? $" seen by {string.Join( ", ", WitnessNames )}"
			: $" seen by {Witnesses.Count}";
		return $"{At.UtcDateTime:HH:mm:ss} {Kind}{(Ok ? "" : " REFUSED")} by {who}{subject}{(details.Length == 0 ? "" : $" ({details})")}{seen}";
	}
}

/// <summary>Who is behind a recorded act.</summary>
public readonly record struct Actor( long Account, Guid? Character = null, string? Name = null )
{
	/// <summary>The server console, which has no account.</summary>
	public static Actor Console => new( 0 );
	public static Actor Of( CharacterData character ) => new( character.SteamId, character.Id, character.Name );
}

/// <summary>
/// The server's memory: an append-only file of JSON lines per day. It is evidence, not state. The
/// saved documents stay the truth, and nothing is ever rebuilt from the journal, so it can be read,
/// archived or lost without changing the city.
/// </summary>
public sealed class Journal
{
	private readonly IFileStore _files;
	private readonly Func<DateTimeOffset> _clock;
	private readonly Action<string> _warn;

	public Journal( IFileStore files, Func<DateTimeOffset>? clock = null, Action<string>? warn = null )
	{
		_files = files ?? throw new ArgumentNullException( nameof(files) );
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		_warn = warn ?? (_ => { });
	}

	/// <summary>
	/// A character's name by id, so an entry names who it was done to and who saw it, as they were
	/// called at the time. Staff cannot turn an id into a name in game, and a deleted character's
	/// name is free to be taken again. The game sets this once its roster is loaded.
	/// </summary>
	public Func<Guid, string?>? NameOf { get; set; }

	public static string PathFor( DateTimeOffset day ) => $"journal/{day.UtcDateTime:yyyy-MM-dd}.jsonl";

	/// <summary>Never throws: a full disk must not undo a decision the host has already made.</summary>
	public void Record( JournalEntry entry )
	{
		if ( entry.At == default ) entry.At = _clock();
		try
		{
			Name( entry );
			// The leading newline ends any line a crash left half-written, so it cannot swallow this one.
			_files.Append( PathFor( entry.At ), "\n" + JsonSerializer.Serialize( entry ) );
		}
		catch ( Exception exception )
		{
			_warn( $"Journal entry '{entry.Kind}' was not written: {exception.Message}" );
		}
	}

	public void Record( string kind, Actor by, string? subject = null, bool ok = true,
		float[]? where = null, IEnumerable<Guid>? witnesses = null, params (string Key, string Value)[] data )
	{
		var entry = new JournalEntry
		{
			Kind = kind, Ok = ok, Account = by.Account, Actor = by.Character, ActorName = by.Name,
			Subject = subject, Where = where
		};
		if ( witnesses is not null ) entry.Witnesses.AddRange( witnesses );
		foreach ( var (key, value) in data ) entry.Data[key] = value;
		Record( entry );
	}

	/// <summary>
	/// The latest entries whose readable line contains the text, oldest first, from the last
	/// <paramref name="days"/> days up to <paramref name="now"/>. A day is a UTC day, so an evening
	/// that crosses midnight there is two. Empty text matches all.
	/// </summary>
	public IReadOnlyList<JournalEntry> Search( DateTimeOffset now, string text, int maximum, int days = 1 )
	{
		var matches = new List<JournalEntry>();
		for ( var back = Math.Max( 1, days ) - 1; back >= 0; back-- )
			matches.AddRange( Read( now.AddDays( -back ) ).Where( entry => text.Length == 0 || entry.Describe().Contains( text, StringComparison.OrdinalIgnoreCase ) ) );
		return matches.Skip( Math.Max( 0, matches.Count - maximum ) ).ToArray();
	}

	private void Name( JournalEntry entry )
	{
		if ( NameOf is not { } nameOf ) return;
		if ( entry.SubjectName is null && CharacterIn( entry.Subject ) is { } subject ) entry.SubjectName = nameOf( subject );
		if ( entry.WitnessNames.Count == 0 && entry.Witnesses.Count > 0 )
			entry.WitnessNames.AddRange( entry.Witnesses.Select( witness => nameOf( witness ) ?? "someone" ) );
	}

	/// <summary>The character a subject such as <c>character:4f1c...</c> names, if it names one.</summary>
	private static Guid? CharacterIn( string? subject ) =>
		subject is not null && subject.StartsWith( "character:", StringComparison.Ordinal ) && Guid.TryParseExact( subject["character:".Length..], "N", out var id )
			? id
			: null;

	/// <summary>That day's entries, oldest first. A line torn by a crash is skipped.</summary>
	public IReadOnlyList<JournalEntry> Read( DateTimeOffset day )
	{
		var entries = new List<JournalEntry>();
		var path = PathFor( day );
		if ( !_files.Exists( path ) ) return entries;
		foreach ( var line in _files.Read( path ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
		{
			try
			{
				if ( JsonSerializer.Deserialize<JournalEntry>( line ) is { } entry ) entries.Add( entry );
			}
			catch ( JsonException )
			{
			}
		}
		return entries;
	}
}
