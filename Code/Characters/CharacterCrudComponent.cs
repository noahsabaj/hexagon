namespace Hexagon.Characters;

/// <summary>
/// Handles character CRUD RPCs (list, create, load, delete).
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class CharacterCrudComponent : Component
{
	/// <summary>
	/// Client-side character list received from the server.
	/// </summary>
	public List<CharacterListEntry> ClientCharacterList { get; private set; } = new();

	private HexPlayerComponent Player => GetComponent<HexPlayerComponent>();

	// --- Server-bound RPCs (client calls these) ---

	/// <summary>
	/// Client requests their character list from the server.
	/// </summary>
	[Rpc.Host]
	public void RequestCharacterList()
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player != Player ) return;

		SendCharacterListToOwner();
	}

	/// <summary>
	/// Client requests to load a specific character.
	/// </summary>
	[Rpc.Host]
	public void RequestLoadCharacter( string characterId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player != Player ) return;

		if ( string.IsNullOrEmpty( characterId ) ) return;

		var success = CharacterManager.LoadCharacter( player, characterId );
		if ( !success )
		{
			ReceiveCharacterCreateResult( false, "Failed to load character." );
		}
	}

	/// <summary>
	/// Client requests to create a new character from JSON data.
	/// </summary>
	[Rpc.Host]
	public void RequestCreateCharacter( string json )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player != Player ) return;

		if ( string.IsNullOrEmpty( json ) )
		{
			ReceiveCharacterCreateResult( false, "Invalid character data." );
			return;
		}

		try
		{
			var data = CharacterManager.CreateDefaultData();

			var values = Json.Deserialize<Dictionary<string, object>>( json );
			if ( values == null )
			{
				ReceiveCharacterCreateResult( false, "Invalid character data." );
				return;
			}

			foreach ( var kvp in values )
			{
				if ( kvp.Key == "Faction" )
				{
					data.Faction = kvp.Value?.ToString();
					continue;
				}

				if ( kvp.Key == "Class" )
				{
					data.Class = kvp.Value?.ToString();
					continue;
				}

				var varInfo = CharacterManager.GetCharVarInfo( kvp.Key );
				if ( varInfo == null ) continue;
				if ( !varInfo.Attribute.ShowInCreation ) continue;

				if ( varInfo.PropertyType == typeof( string ) )
				{
					varInfo.SetValue( data, kvp.Value?.ToString() ?? "" );
				}
				else if ( varInfo.PropertyType == typeof( int ) )
				{
					if ( int.TryParse( kvp.Value?.ToString(), out var intVal ) )
						varInfo.SetValue( data, intVal );
				}
				else
				{
					varInfo.SetValue( data, kvp.Value );
				}
			}

			var character = CharacterManager.CreateCharacter( player, data );
			if ( character == null )
			{
				ReceiveCharacterCreateResult( false, "Character creation failed. Check server logs." );
				return;
			}

			ReceiveCharacterCreateResult( true, "Character created successfully." );
			SendCharacterListToOwner();
		}
		catch ( Exception ex )
		{
			Log.Error( $"Hexagon: RequestCreateCharacter error: {ex}" );
			ReceiveCharacterCreateResult( false, "An error occurred during character creation." );
		}
	}

	/// <summary>
	/// Client requests to delete a character.
	/// </summary>
	[Rpc.Host]
	public void RequestDeleteCharacter( string characterId )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player != Player ) return;

		if ( string.IsNullOrEmpty( characterId ) ) return;

		var success = CharacterManager.DeleteCharacter( player, characterId );
		if ( success )
		{
			SendCharacterListToOwner();
		}
	}

	// --- Client-bound RPCs (server calls these) ---

	[Rpc.Owner]
	private void ReceiveCharacterList( string json )
	{
		try
		{
			ClientCharacterList = Json.Deserialize<List<CharacterListEntry>>( json ) ?? new();
		}
		catch ( Exception ex )
		{
			Log.Error( $"Hexagon: Failed to deserialize character list from server: {ex.Message}" );
			ClientCharacterList = new();
		}

		IHexCrudEvent.Post( x => x.OnCharacterListReceived() );
	}

	[Rpc.Owner]
	private void ReceiveCharacterCreateResult( bool success, string message )
	{
		IHexCrudEvent.Post( x => x.OnCharacterCreateResult( success, message ) );
	}

	// --- Server-side helper ---

	/// <summary>
	/// Build and send the character list to the owning client.
	/// </summary>
	internal void SendCharacterListToOwner()
	{
		var player = Player;
		if ( player == null ) return;

		var characters = CharacterManager.GetCharacterList( player.SteamId );
		var entries = characters.Select( c =>
		{
			var nameInfo = CharacterManager.GetCharVarInfo( "Name" );
			var descInfo = CharacterManager.GetCharVarInfo( "Description" );

			return new CharacterListEntry
			{
				Id = c.Id,
				Name = nameInfo?.GetValue( c )?.ToString() ?? "Unknown",
				Description = descInfo?.GetValue( c )?.ToString() ?? "",
				Faction = c.Faction ?? "",
				Class = c.Class ?? "",
				LastPlayed = c.LastPlayedAt
			};
		} ).ToList();

		var json = Json.Serialize( entries );
		ReceiveCharacterList( json );
	}
}
