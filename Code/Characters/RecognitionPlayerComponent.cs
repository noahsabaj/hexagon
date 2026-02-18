namespace Hexagon.Characters;

/// <summary>
/// Client-side character recognition state.
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// Uses a [Sync] NetList for delta-compressed networking — no JSON, no RPC needed.
/// </summary>
public sealed class RecognitionPlayerComponent : Component
{
	/// <summary>
	/// Synced list of recognized character IDs. Server writes directly; client reads directly.
	/// NetList sends only deltas, so no full resync on each change.
	/// </summary>
	[Sync] public NetList<string> RecognizedIds { get; set; } = new();

	/// <summary>
	/// Server-side: replace the full recognition set with new IDs.
	/// Called by RecognitionManager after a character's recognition list changes.
	/// </summary>
	internal void SetRecognizedIds( IEnumerable<string> ids )
	{
		RecognizedIds.Clear();
		foreach ( var id in ids )
			RecognizedIds.Add( id );
	}

	/// <summary>
	/// Client-side: check if this player recognizes a target player.
	/// </summary>
	public bool DoesRecognizeLocal( HexPlayerComponent target )
	{
		if ( target == null || !target.HasActiveCharacter ) return true;

		var self = GetComponent<HexPlayerComponent>();
		if ( target == self ) return true;

		// Check if target's faction is globally recognized
		if ( !string.IsNullOrEmpty( target.FactionId ) )
		{
			var faction = Factions.FactionManager.GetFaction( target.FactionId );
			if ( faction != null && faction.IsGloballyRecognized )
				return true;
		}

		// RecognizedIds is empty until the server populates it.
		// If the list has never been populated (fresh spawn), treat as unrecognized.
		return RecognizedIds.Contains( target.CharacterId );
	}
}
