namespace Hexagon.Characters;

/// <summary>
/// Manages the recognizable names system. Characters are "Unknown" to others
/// until formally introduced. Recognition is one-way and persists across sessions.
///
/// Factions with IsGloballyRecognized are always known (e.g., police uniforms).
/// </summary>
public static class RecognitionManager
{
	internal static void Initialize()
	{
		Log.Info( "Hexagon: RecognitionManager initialized." );
	}

	/// <summary>
	/// Server-side: check if observer recognizes target.
	/// </summary>
	public static bool DoesRecognize( HexCharacter observer, HexCharacter target )
	{
		if ( observer == null || target == null ) return false;

		// Always recognize yourself
		if ( observer.Id == target.Id ) return true;

		// Check faction IsGloballyRecognized
		if ( !string.IsNullOrEmpty( target.Faction ) )
		{
			var faction = Factions.FactionManager.GetFaction( target.Faction );
			if ( faction != null && faction.IsGloballyRecognized )
				return true;
		}

		// Hook: ICanRecognizeListener can block recognition
		if ( !HexEvents.CanAll<ICanRecognizeListener>( x => x.CanRecognize( observer, target ) ) )
			return false;

		// Check stored recognition data
		var ids = observer.GetRecognizedIds();
		return ids.Contains( target.Id );
	}

	/// <summary>
	/// Server-side: add target to observer's recognition list.
	/// Returns true if newly recognized, false if already known.
	/// </summary>
	public static bool Recognize( HexCharacter observer, string targetCharacterId )
	{
		if ( observer == null || string.IsNullOrEmpty( targetCharacterId ) ) return false;
		if ( observer.Id == targetCharacterId ) return false;

		var ids = observer.GetRecognizedIds();
		if ( ids.Contains( targetCharacterId ) ) return false;

		observer.AddRecognized( targetCharacterId );

		// Sync to client
		if ( observer.Player != null )
			SyncRecognitionToClient( observer.Player );

		return true;
	}

	/// <summary>
	/// Server-side: introduce a character to all players within range.
	/// Returns the number of players who newly recognized the introducer.
	/// </summary>
	public static int IntroduceToRange( HexPlayerComponent player, float range )
	{
		if ( player?.Character == null ) return 0;

		var count = 0;

		foreach ( var kvp in HexGameManager.Players )
		{
			var other = kvp.Value;
			if ( other == null || other == player || other.Character == null ) continue;

			var dist = Vector3.DistanceBetween( player.WorldPosition, other.WorldPosition );
			if ( dist > range ) continue;

			if ( Recognize( other.Character, player.Character.Id ) )
			{
				count++;

				// Fire event
				HexEvents.Fire<ICharacterRecognizedListener>(
					x => x.OnCharacterRecognized( player, other ) );
			}
		}

		return count;
	}

	/// <summary>
	/// Server-side: introduce to a specific player.
	/// Returns true if the target newly recognized the introducer.
	/// </summary>
	public static bool IntroduceToTarget( HexPlayerComponent player, HexPlayerComponent target )
	{
		if ( player?.Character == null || target?.Character == null ) return false;
		if ( player == target ) return false;

		var result = Recognize( target.Character, player.Character.Id );
		if ( result )
		{
			HexEvents.Fire<ICharacterRecognizedListener>(
				x => x.OnCharacterRecognized( player, target ) );
		}

		return result;
	}

	/// <summary>
	/// Server-side: get the display name for a target as seen by an observer.
	/// Returns "Unknown" if not recognized.
	/// </summary>
	public static string GetDisplayName( HexPlayerComponent observer, HexPlayerComponent target )
	{
		if ( !Config.HexConfig.Get<bool>( "recognition.enabled", true ) )
			return target?.CharacterName ?? "Unknown";

		if ( observer == null || target == null )
			return target?.CharacterName ?? "Unknown";

		// Always recognize self
		if ( observer == target ) return target.CharacterName;

		if ( observer.Character != null && target.Character != null )
		{
			if ( DoesRecognize( observer.Character, target.Character ) )
				return target.CharacterName;
		}

		return "Unknown";
	}

	/// <summary>
	/// Server-side: get a chat-friendly display name with truncated description for unknowns.
	/// </summary>
	public static string GetDisplayNameForChat( HexPlayerComponent observer, HexPlayerComponent target )
	{
		var name = GetDisplayName( observer, target );
		if ( name != "Unknown" ) return name;

		// Truncated description for IC chat context
		var desc = target.CharacterDescription;
		if ( !string.IsNullOrWhiteSpace( desc ) )
		{
			var truncated = desc.Length > 30 ? desc[..30] + "..." : desc;
			return truncated;
		}

		return "Unknown";
	}

	/// <summary>
	/// Server-side: format a pre-formatted message replacing the speaker's real name
	/// with the recognition-aware name for a specific listener.
	/// </summary>
	public static string FormatForListener( HexPlayerComponent observer, HexPlayerComponent speaker, string formatted )
	{
		if ( !Config.HexConfig.Get<bool>( "recognition.enabled", true ) )
			return formatted;

		var realName = speaker.CharacterName;
		if ( string.IsNullOrEmpty( realName ) ) return formatted;

		var displayName = GetDisplayNameForChat( observer, speaker );
		if ( displayName == realName ) return formatted;

		// Replace only the first occurrence of the real name
		var idx = formatted.IndexOf( realName );
		if ( idx >= 0 )
		{
			return formatted[..idx] + displayName + formatted[(idx + realName.Length)..];
		}

		return formatted;
	}

	/// <summary>
	/// Server-side: sync recognition data to the owning client.
	/// </summary>
	internal static void SyncRecognitionToClient( HexPlayerComponent player )
	{
		if ( player?.Character == null ) return;

		var ids = player.Character.GetRecognizedIds();
		var json = Json.Serialize( ids );
		player.ReceiveRecognitionData( json );
	}
}

/// <summary>
/// Permission hook: can a character be recognized? Return false to block (e.g., disguise system).
/// </summary>
public interface ICanRecognizeListener
{
	bool CanRecognize( HexCharacter observer, HexCharacter target );
}

/// <summary>
/// Fired after a character is recognized by another through introduction.
/// </summary>
public interface ICharacterRecognizedListener
{
	void OnCharacterRecognized( HexPlayerComponent introducer, HexPlayerComponent recognizer );
}
