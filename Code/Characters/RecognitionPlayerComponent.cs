namespace Hexagon.Characters;

/// <summary>
/// Client-side character recognition state.
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class RecognitionPlayerComponent : Component
{
	private HashSet<string> _recognizedIds = new();
	private bool _recognitionDataReceived;

	/// <summary>
	/// Server sends updated recognition data to the owning client.
	/// </summary>
	[Rpc.Owner]
	internal void ReceiveRecognitionData( string json )
	{
		try
		{
			_recognizedIds = Json.Deserialize<HashSet<string>>( json ) ?? new();
		}
		catch
		{
			_recognizedIds = new();
		}

		_recognitionDataReceived = true;
	}

	/// <summary>
	/// Client-side: check if this player recognizes a target player.
	/// </summary>
	public bool DoesRecognizeLocal( HexPlayerComponent target )
	{
		if ( target == null || !target.HasActiveCharacter ) return true;

		var self = GetComponent<HexPlayerComponent>();
		if ( target == self ) return true;

		// If recognition data was never synced, feature is likely disabled
		if ( !_recognitionDataReceived ) return true;

		// Check if target's faction is globally recognized
		if ( !string.IsNullOrEmpty( target.FactionId ) )
		{
			var faction = Factions.FactionManager.GetFaction( target.FactionId );
			if ( faction != null && faction.IsGloballyRecognized )
				return true;
		}

		return _recognizedIds.Contains( target.CharacterId );
	}
}
