namespace Hexagon.Characters;

/// <summary>
/// Lightweight DTO for character list sent to clients.
/// </summary>
public class CharacterListEntry
{
	public string Id { get; set; }
	public string Name { get; set; }
	public string Description { get; set; }
	public string Faction { get; set; }
	public string Class { get; set; }
	public DateTime LastPlayed { get; set; }
}

/// <summary>
/// Core player component. Holds networked character identity data visible to all players,
/// plus server-side references to the full character and connection.
///
/// Satellite components handle specific subsystems:
/// <list type="bullet">
///   <item><see cref="CharacterCrudComponent"/> — character list/create/load/delete RPCs</item>
///   <item><see cref="Interaction.ActionBarPlayerComponent"/> — action bar client state</item>
///   <item><see cref="RecognitionPlayerComponent"/> — recognition client state</item>
///   <item><see cref="IntroducePlayerComponent"/> — introduce mechanic RPC</item>
///   <item><see cref="UI.NotificationPlayerComponent"/> — notification RPC</item>
/// </list>
/// </summary>
public sealed class HexPlayerComponent : Component
{
	// --- Player Identity (synced to all) ---

	[Sync] public ulong SteamId { get; set; }
	[Sync] public string DisplayName { get; set; }

	// --- Public Character Data (synced to all) ---

	[Sync] public string CharacterName { get; set; } = "";
	[Sync] public string CharacterModel { get; set; } = "";
	[Sync] public string CharacterDescription { get; set; } = "";
	[Sync] public string FactionId { get; set; } = "";
	[Sync] public string ClassId { get; set; } = "";
	[Sync] public string CharacterId { get; set; } = "";
	[Sync] public bool HasActiveCharacter { get; set; }
	[Sync] public bool IsDead { get; set; }

	// --- Server-Side Only (not networked) ---

	/// <summary>
	/// The active character for this player. Only valid on the server.
	/// </summary>
	public HexCharacter Character { get; internal set; }

	/// <summary>
	/// The network connection for this player.
	/// </summary>
	public Connection Connection { get; internal set; }

	// --- Private Sync ---

	/// <summary>
	/// The private data for this player's active character, sent directly
	/// from the server via RPC. Only populated on the owner client.
	/// </summary>
	public HexCharacterData ClientPrivateData { get; private set; }

	/// <summary>
	/// Push the current character's public data to the [Sync] properties.
	/// Called when character data changes.
	/// </summary>
	internal void SyncPublicData()
	{
		if ( IsProxy ) return;
		if ( Character?.Data == null )
		{
			HasActiveCharacter = false;
			return;
		}

		HasActiveCharacter = true;
		CharacterId = Character.Id;
		FactionId = Character.Data.Faction ?? "";
		ClassId = Character.Data.Class ?? "";

		foreach ( var varInfo in CharacterManager.GetPublicCharVars() )
		{
			if ( varInfo.Attribute.SyncTarget == CharSyncTarget.None ) continue;

			var value = varInfo.GetValue( Character.Data )?.ToString() ?? "";

			switch ( varInfo.Attribute.SyncTarget )
			{
				case CharSyncTarget.CharacterName:
					CharacterName = value;
					break;
				case CharSyncTarget.CharacterDescription:
					CharacterDescription = value;
					break;
				case CharSyncTarget.CharacterModel:
					CharacterModel = value;
					break;
			}
		}
	}

	/// <summary>
	/// Send all private character data to the owning player natively over RPC.
	/// </summary>
	internal void SyncPrivateData()
	{
		if ( IsProxy ) return;
		if ( Character?.Data == null ) return;

		ReceivePrivateData( Character.Data );
		RecognitionManager.SyncRecognitionToClient( this );
	}

	[Rpc.Owner]
	private void ReceivePrivateData( HexCharacterData data )
	{
		ClientPrivateData = data;
	}

	/// <summary>
	/// Client-side: get a private CharVar value that was synced from the server.
	/// </summary>
	public T GetPrivateVar<T>( string name, T defaultValue = default )
	{
		if ( ClientPrivateData == null ) return defaultValue;

		var varInfo = CharacterManager.GetCharVarInfo( name );
		if ( varInfo == null ) return defaultValue;

		var val = varInfo.GetValue( ClientPrivateData );
		
		try
		{
			if ( val is T typed ) return typed;
			if ( val == null ) return defaultValue;
			return (T)Convert.ChangeType( val, typeof( T ) );
		}
		catch
		{
			return defaultValue;
		}
	}

	// --- Lifecycle ---

	protected override void OnDestroy()
	{
		Interaction.ActionBarManager.RemovePlayer( this );

		if ( !IsProxy && Character != null )
		{
			Character.Save();
			Character.Player = null;
			Character = null;
		}
	}
}
