#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Application;

public static class CharacterRules
{
	public const int MaximumNameLength = 64;
	public const int MinimumNameLength = 3;
	public const int MaximumDescriptionLength = 512;
	public const int MinimumDescriptionLength = 16;

	/// <summary>
	/// Hard per-account slot cap. Slots are dense in 0..MaximumSlots-1 (commit-time
	/// invariant), which lets account listings run as bounded keyed slot probes
	/// instead of scanning every character ever created.
	/// </summary>
	public const int MaximumSlots = 64;

	/// <summary>
	/// How close two character names may be before the second is refused. Operator-tunable
	/// because the cost of getting it wrong falls on players: folding is deliberately lossy and
	/// will sometimes judge two genuinely different names too similar.
	/// <para>
	/// This governs NEW characters only. What makes a name well-formed at all — no control or
	/// format characters, one writing system, canonical encoding — is a persistence invariant and
	/// is deliberately NOT tunable: if it were, tightening the setting would make an existing
	/// store fail its own startup validation, and with no rename path that is a host that cannot
	/// boot.
	/// </para>
	/// </summary>
	public enum NameUniqueness
	{
		/// <summary>Refuse a name whose confusable skeleton matches one already taken.</summary>
		Skeleton = 0,
		/// <summary>Refuse only a plain repeat, ignoring case, accents, spacing and punctuation.</summary>
		Exact = 1,
		/// <summary>Allow names to repeat freely.</summary>
		None = 2
	}

	public static OperationResult ValidateCreationRequest( CharacterCreationRequest request, IReadOnlySet<string> allowedFields )
	{
		var name = NormalizeName( request.Name );
		if ( name.Failed ) return OperationResult.Failure( name.Error!.Code, name.Error.Message );

		var description = NormalizeDescription( request.Description );
		if ( description.Failed ) return OperationResult.Failure( description.Error!.Code, description.Error.Message );

		foreach ( var field in request.Fields.Keys )
		{
			if ( !allowedFields.Contains( field ) )
				return OperationResult.Failure( ErrorCode.InvalidArgument, $"Unknown or non-creation field '{field}'." );
		}

		return OperationResult.Success();
	}

	/// <summary>
	/// Canonical form of a player-authored character name. A name is replicated to other
	/// players through recognition records and nameplates, so it is an identity boundary,
	/// not free text, and is held to an identifier-grade profile:
	/// <list type="bullet">
	/// <item>NFKC folding, so a name equals what it renders as — fullwidth and other
	/// compatibility look-alikes collapse onto the characters they imitate.</item>
	/// <item>No control characters and no Unicode format characters (zero-width joiners,
	/// bidirectional overrides and isolates).</item>
	/// <item>One writing system, per the Unicode "highly restrictive" identifier profile,
	/// so Cyrillic or Cherokee letters cannot be mixed into a Latin name to impersonate
	/// another player. See <see cref="NameScriptProfile"/>.</item>
	/// </list>
	/// Callers must persist this value rather than the raw request, or what was validated
	/// and what is stored can differ; <c>DomainInvariantValidator</c> enforces that at commit.
	/// </summary>
	public static OperationResult<string> NormalizeName( string value )
	{
		var normalized = NormalizeIdentityText(
			value, "Name", MinimumNameLength, MaximumNameLength,
			allowLineBreaks: false, foldCompatibility: true );
		return normalized.Failed ? normalized : NameScriptProfile.Validate( normalized.Value );
	}

	/// <summary>
	/// Canonical form of a player-authored character description, which replicates to every
	/// client. Same control- and format-character rules as <see cref="NormalizeName"/>, but a
	/// description is prose rather than identity: line breaks survive, compatibility
	/// characters are preserved (NFC, not NFKC), and no script restriction applies.
	/// </summary>
	public static OperationResult<string> NormalizeDescription( string value ) =>
		NormalizeIdentityText(
			value, "Description", MinimumDescriptionLength, MaximumDescriptionLength,
			allowLineBreaks: true, foldCompatibility: false );

	private static OperationResult<string> NormalizeIdentityText(
		string value,
		string field,
		int minimumLength,
		int maximumLength,
		bool allowLineBreaks,
		bool foldCompatibility )
	{
		if ( value is null )
			return OperationResult<string>.Failure( ErrorCode.InvalidArgument, $"{field} is required." );

		// Normalize throws on unpaired surrogates, so well-formedness is proven first.
		for ( var index = 0; index < value.Length; index++ )
		{
			if ( char.IsHighSurrogate( value[index] ) )
			{
				if ( index + 1 >= value.Length || !char.IsLowSurrogate( value[index + 1] ) )
					return InvalidUnicode( field );
				index++;
			}
			else if ( char.IsLowSurrogate( value[index] ) )
			{
				return InvalidUnicode( field );
			}
		}

		var normalized = value
			.Normalize( foldCompatibility ? NormalizationForm.FormKC : NormalizationForm.FormC )
			.Trim();
		if ( normalized.Length < minimumLength || normalized.Length > maximumLength )
			return OperationResult<string>.Failure(
				ErrorCode.InvalidArgument, $"{field} must be {minimumLength}-{maximumLength} characters." );

		for ( var index = 0; index < normalized.Length; index++ )
		{
			var character = normalized[index];
			if ( char.IsControl( character ) && !(allowLineBreaks && character is '\n') )
				return OperationResult<string>.Failure(
					ErrorCode.InvalidArgument, $"{field} contains control characters." );
			// The category is read from the string rather than the char so a format
			// character encoded as a surrogate pair is classified as itself.
			if ( CharUnicodeInfo.GetUnicodeCategory( normalized, index ) == UnicodeCategory.Format )
				return OperationResult<string>.Failure(
					ErrorCode.InvalidArgument,
					$"{field} contains zero-width or bidirectional formatting characters." );
			if ( char.IsHighSurrogate( character ) ) index++;
		}

		return OperationResult<string>.Success( normalized );
	}

	private static OperationResult<string> InvalidUnicode( string field ) =>
		OperationResult<string>.Failure( ErrorCode.InvalidArgument, $"{field} contains invalid Unicode." );

	/// <summary>
	/// Lowest unoccupied slot for the account, or -1 when all
	/// <see cref="MaximumSlots"/> slots are occupied.
	/// </summary>
	public static int FindLowestFreeSlot( IEnumerable<CharacterRecord> characters )
	{
		var occupied = characters.Select( character => character.Slot ).Where( slot => slot >= 0 ).ToHashSet();
		var slot = 0;
		while ( occupied.Contains( slot ) ) slot++;
		return slot < MaximumSlots ? slot : -1;
	}
}


/// <summary>
/// The Unicode "highly restrictive" identifier profile (UTS #39), which is what stops a Cyrillic
/// letter from standing in for a Latin one: a name must draw its letters from a single writing
/// system, except for the three combinations real names genuinely need - Japanese, Chinese and
/// Korean, each of which may also carry Latin.
/// <para>
/// Only letters carry a script; digits, spaces, punctuation and combining marks are
/// script-neutral and never constrain a name. A script outside the enumerated set is identified
/// by its 128-code-point block rather than lumped into one "other" bucket, so two unenumerated
/// scripts are still seen as two scripts and cannot be mixed. Every script whose letters span
/// more than one block is enumerated explicitly, so that fallback never splits a real name.
/// </para>
/// </summary>
internal static class NameScriptProfile
{
	private const int Latin = 1;
	private const int Greek = 2;
	private const int Cyrillic = 3;
	private const int Armenian = 4;
	private const int Hebrew = 5;
	private const int Arabic = 6;
	private const int Cherokee = 7;
	private const int Han = 8;
	private const int Hiragana = 9;
	private const int Katakana = 10;
	private const int Hangul = 11;
	private const int Bopomofo = 12;
	private const int Devanagari = 13;
	private const int Bengali = 14;
	private const int Gurmukhi = 15;
	private const int Gujarati = 16;
	private const int Oriya = 17;
	private const int Tamil = 18;
	private const int Telugu = 19;
	private const int Kannada = 20;
	private const int Malayalam = 21;
	private const int Sinhala = 22;
	private const int Thai = 23;
	private const int Lao = 24;
	private const int Tibetan = 25;
	private const int Myanmar = 26;
	private const int Georgian = 27;
	private const int Ethiopic = 28;
	private const int Khmer = 29;
	private const int Mongolian = 30;
	private const int Thaana = 31;
	private const int Syriac = 32;

	/// <summary>An unenumerated script is identified by its 128-code-point block.</summary>
	private const int UnenumeratedBase = 1000;

	private static readonly int[][] AllowedCombinations =
	{
		new[] { Latin, Han, Hiragana, Katakana },
		new[] { Latin, Han, Bopomofo },
		new[] { Latin, Han, Hangul }
	};

	public static OperationResult<string> Validate( string name )
	{
		var scripts = Of( name );
		if ( scripts.Count <= 1 ) return OperationResult<string>.Success( name );
		foreach ( var combination in AllowedCombinations )
			if ( scripts.IsSubsetOf( combination ) ) return OperationResult<string>.Success( name );
		return OperationResult<string>.Failure(
			ErrorCode.InvalidArgument,
			"Name mixes writing systems that are not used together, which is how look-alike " +
			"letters impersonate another character's name." );
	}

	public static HashSet<int> Of( string value )
	{
		var scripts = new HashSet<int>();
		for ( var index = 0; index < value.Length; index++ )
		{
			// Category and code point are both read from the string so a letter encoded as a
			// surrogate pair is classified as itself. Pairing is proven before this runs.
			var category = CharUnicodeInfo.GetUnicodeCategory( value, index );
			var codePoint = char.ConvertToUtf32( value, index );
			if ( char.IsHighSurrogate( value[index] ) ) index++;
			if ( category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
				or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
				or UnicodeCategory.OtherLetter )
				scripts.Add( ScriptOf( codePoint ) );
		}

		return scripts;
	}

	private static int ScriptOf( int codePoint ) => codePoint switch
	{
		>= 0x0041 and <= 0x005A => Latin,
		>= 0x0061 and <= 0x007A => Latin,
		>= 0x00C0 and <= 0x024F => Latin,
		>= 0x1E00 and <= 0x1EFF => Latin,
		>= 0x2C60 and <= 0x2C7F => Latin,
		>= 0xA720 and <= 0xA7FF => Latin,
		>= 0xFF21 and <= 0xFF3A => Latin,
		>= 0xFF41 and <= 0xFF5A => Latin,
		>= 0x0370 and <= 0x03FF => Greek,
		>= 0x1F00 and <= 0x1FFF => Greek,
		>= 0x0400 and <= 0x052F => Cyrillic,
		>= 0x2DE0 and <= 0x2DFF => Cyrillic,
		>= 0xA640 and <= 0xA69F => Cyrillic,
		>= 0x0530 and <= 0x058F => Armenian,
		>= 0x0590 and <= 0x05FF => Hebrew,
		>= 0x0600 and <= 0x06FF => Arabic,
		>= 0x0750 and <= 0x077F => Arabic,
		>= 0x08A0 and <= 0x08FF => Arabic,
		>= 0xFB50 and <= 0xFDFF => Arabic,
		>= 0xFE70 and <= 0xFEFF => Arabic,
		>= 0x0700 and <= 0x074F => Syriac,
		>= 0x0780 and <= 0x07BF => Thaana,
		>= 0x13A0 and <= 0x13FF => Cherokee,
		>= 0xAB70 and <= 0xABBF => Cherokee,
		>= 0x3040 and <= 0x309F => Hiragana,
		>= 0x30A0 and <= 0x30FF => Katakana,
		>= 0x31F0 and <= 0x31FF => Katakana,
		>= 0xFF66 and <= 0xFF9D => Katakana,
		>= 0x3100 and <= 0x312F => Bopomofo,
		>= 0x31A0 and <= 0x31BF => Bopomofo,
		>= 0x1100 and <= 0x11FF => Hangul,
		>= 0x3130 and <= 0x318F => Hangul,
		>= 0xA960 and <= 0xA97F => Hangul,
		>= 0xAC00 and <= 0xD7AF => Hangul,
		>= 0x2E80 and <= 0x2EFF => Han,
		>= 0x3400 and <= 0x4DBF => Han,
		>= 0x4E00 and <= 0x9FFF => Han,
		>= 0xF900 and <= 0xFAFF => Han,
		>= 0x20000 and <= 0x2FA1F => Han,
		>= 0x0900 and <= 0x097F => Devanagari,
		>= 0xA8E0 and <= 0xA8FF => Devanagari,
		>= 0x0980 and <= 0x09FF => Bengali,
		>= 0x0A00 and <= 0x0A7F => Gurmukhi,
		>= 0x0A80 and <= 0x0AFF => Gujarati,
		>= 0x0B00 and <= 0x0B7F => Oriya,
		>= 0x0B80 and <= 0x0BFF => Tamil,
		>= 0x0C00 and <= 0x0C7F => Telugu,
		>= 0x0C80 and <= 0x0CFF => Kannada,
		>= 0x0D00 and <= 0x0D7F => Malayalam,
		>= 0x0D80 and <= 0x0DFF => Sinhala,
		>= 0x0E00 and <= 0x0E7F => Thai,
		>= 0x0E80 and <= 0x0EFF => Lao,
		>= 0x0F00 and <= 0x0FFF => Tibetan,
		>= 0x1000 and <= 0x109F => Myanmar,
		>= 0xA9E0 and <= 0xA9FF => Myanmar,
		>= 0xAA60 and <= 0xAA7F => Myanmar,
		>= 0x10A0 and <= 0x10FF => Georgian,
		>= 0x1C90 and <= 0x1CBF => Georgian,
		>= 0x2D00 and <= 0x2D2F => Georgian,
		>= 0x1200 and <= 0x139F => Ethiopic,
		>= 0x2D80 and <= 0x2DDF => Ethiopic,
		>= 0xAB00 and <= 0xAB2F => Ethiopic,
		>= 0x1780 and <= 0x17FF => Khmer,
		>= 0x19E0 and <= 0x19FF => Khmer,
		>= 0x1800 and <= 0x18AF => Mongolian,
		_ => UnenumeratedBase + (codePoint >> 7)
	};
}