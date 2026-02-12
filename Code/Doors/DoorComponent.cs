namespace Hexagon.Doors;

/// <summary>
/// A world-placed door that supports ownership, locking, and access control.
/// Interacted with via the USE key (IPressable).
///
/// Place on any GameObject in the scene. Set DoorId in the editor or let it auto-generate.
/// Schema devs bind IsOpen to their door animation system.
///
/// Authorization hierarchy: admin flag ("a") > character owner > faction member > access list
/// </summary>
public sealed class DoorComponent : Component, Component.IPressable
{
	/// <summary>
	/// Unique identifier for this door. Auto-generated if empty on enable.
	/// </summary>
	[Property] public string DoorId { get; set; } = "";

	/// <summary>
	/// Display name shown in the tooltip.
	/// </summary>
	[Property] public string DoorName { get; set; } = "Door";

	/// <summary>
	/// Whether the door is currently locked. Synced to all players.
	/// </summary>
	[Sync] public bool IsLocked { get; set; }

	/// <summary>
	/// Whether the door is currently open. Schema binds this to door animation.
	/// </summary>
	[Sync] public bool IsOpen { get; set; }

	/// <summary>
	/// Display name of the current owner. Synced to all players for tooltip display.
	/// </summary>
	[Sync] public string OwnerDisplay { get; set; } = "";

	private DoorData _data;

	/// <summary>
	/// The underlying door data. Loaded from the database on enable.
	/// </summary>
	public DoorData Data => _data;

	protected override void OnEnabled()
	{
		if ( IsProxy ) return;

		if ( string.IsNullOrEmpty( DoorId ) )
		{
			DoorId = Persistence.DatabaseManager.NewId();
		}

		_data = DoorManager.LoadDoor( DoorId );

		if ( _data == null )
		{
			_data = new DoorData { DoorId = DoorId };
		}

		IsLocked = _data.IsLocked;
		UpdateOwnerDisplay();

		DoorManager.Register( this );
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;

		SaveData();
		DoorManager.Unregister( this );
	}

	// --- IPressable ---

	private HexPlayerComponent GetPlayer( Component.IPressable.Event e )
	{
		return e.Source?.GetComponentInParent<HexPlayerComponent>();
	}

	public bool CanPress( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player?.Character == null ) return false;

		return HexEvents.CanAll<ICanUseDoorListener>(
			x => x.CanUseDoor( player, this ) );
	}

	public bool Press( Component.IPressable.Event e )
	{
		var player = GetPlayer( e );
		if ( player?.Character == null ) return false;

		if ( !_data.HasOwner )
		{
			// Unowned door — anyone can toggle
			ToggleOpen();
			HexEvents.Fire<IDoorUsedListener>( x => x.OnDoorUsed( player, this ) );
			HexLog.Add( LogType.Door, player, $"Toggled unowned door \"{DoorName}\" ({DoorId})" );
			return true;
		}

		var authorized = IsAuthorized( player );

		if ( IsLocked )
		{
			if ( authorized )
			{
				SetLocked( false );
				HexLog.Add( LogType.Door, player, $"Unlocked door \"{DoorName}\" ({DoorId})" );
				HexEvents.Fire<IDoorUsedListener>( x => x.OnDoorUsed( player, this ) );
				return true;
			}
			else
			{
				return false;
			}
		}
		else
		{
			// Unlocked — anyone can toggle open/close
			ToggleOpen();
			HexEvents.Fire<IDoorUsedListener>( x => x.OnDoorUsed( player, this ) );
			HexLog.Add( LogType.Door, player, $"Toggled door \"{DoorName}\" ({DoorId})" );
			return true;
		}
	}

	public Component.IPressable.Tooltip? GetTooltip( Component.IPressable.Event e )
	{
		var desc = "";

		if ( !string.IsNullOrEmpty( OwnerDisplay ) )
			desc = $"Owner: {OwnerDisplay}";

		if ( IsLocked )
			desc += string.IsNullOrEmpty( desc ) ? "Locked" : " | Locked";

		return new Component.IPressable.Tooltip( DoorName, "door_front", desc );
	}

	// --- Door Actions ---

	/// <summary>
	/// Toggle the door between open and closed.
	/// </summary>
	public void ToggleOpen()
	{
		IsOpen = !IsOpen;
	}

	/// <summary>
	/// Set the door's locked state.
	/// </summary>
	public void SetLocked( bool locked )
	{
		IsLocked = locked;
		_data.IsLocked = locked;
	}

	// --- Authorization ---

	/// <summary>
	/// Check if a player is authorized to use this door when locked.
	/// Hierarchy: admin flag > character owner > faction member > access list
	/// </summary>
	public bool IsAuthorized( HexPlayerComponent player )
	{
		if ( player?.Character == null ) return false;

		// Admin bypass
		if ( player.Character.HasFlag( 'a' ) )
			return true;

		// Character owner
		if ( !string.IsNullOrEmpty( _data.OwnerCharacterId ) &&
			 _data.OwnerCharacterId == player.Character.Id )
			return true;

		// Faction member
		if ( !string.IsNullOrEmpty( _data.OwnerFactionId ) &&
			 player.Character.Faction == _data.OwnerFactionId )
			return true;

		// Access list
		if ( _data.AccessList.Contains( player.Character.Id ) )
			return true;

		return false;
	}

	// --- Ownership API ---

	/// <summary>
	/// Set a character as the owner. Clears faction ownership and access list.
	/// </summary>
	public void SetOwnerCharacter( string characterId, string displayName )
	{
		var oldOwner = _data.OwnerCharacterId;
		_data.OwnerCharacterId = characterId;
		_data.OwnerFactionId = null;
		_data.AccessList.Clear();
		OwnerDisplay = displayName;
		SaveData();

		HexEvents.Fire<IDoorOwnerChangedListener>(
			x => x.OnDoorOwnerChanged( this, oldOwner, characterId, false ) );
	}

	/// <summary>
	/// Set a faction as the owner. Clears character ownership and access list.
	/// Returns false if faction ownership is disabled by config.
	/// </summary>
	public bool SetOwnerFaction( string factionId )
	{
		if ( !Config.HexConfig.Get<bool>( "door.allowFactionOwnership", true ) )
			return false;

		var oldOwner = _data.OwnerCharacterId ?? _data.OwnerFactionId;
		_data.OwnerCharacterId = null;
		_data.OwnerFactionId = factionId;
		_data.AccessList.Clear();

		var faction = FactionManager.GetFaction( factionId );
		OwnerDisplay = faction?.Name ?? factionId;
		SaveData();

		HexEvents.Fire<IDoorOwnerChangedListener>(
			x => x.OnDoorOwnerChanged( this, oldOwner, factionId, true ) );

		return true;
	}

	/// <summary>
	/// Remove all ownership and unlock the door.
	/// </summary>
	public void ClearOwner()
	{
		var oldOwner = _data.OwnerCharacterId ?? _data.OwnerFactionId;
		_data.OwnerCharacterId = null;
		_data.OwnerFactionId = null;
		_data.AccessList.Clear();
		_data.IsLocked = false;
		IsLocked = false;
		OwnerDisplay = "";
		SaveData();

		HexEvents.Fire<IDoorOwnerChangedListener>(
			x => x.OnDoorOwnerChanged( this, oldOwner, null, false ) );
	}

	/// <summary>
	/// Add a character to the access list.
	/// </summary>
	public void AddAccess( string characterId )
	{
		if ( !_data.AccessList.Contains( characterId ) )
		{
			_data.AccessList.Add( characterId );
			SaveData();
		}
	}

	/// <summary>
	/// Remove a character from the access list.
	/// </summary>
	public void RemoveAccess( string characterId )
	{
		if ( _data.AccessList.Remove( characterId ) )
		{
			SaveData();
		}
	}

	// --- Persistence ---

	internal void SaveData()
	{
		if ( _data == null ) return;
		DoorManager.SaveDoor( _data );
	}

	private void UpdateOwnerDisplay()
	{
		if ( !string.IsNullOrEmpty( _data.OwnerCharacterId ) )
		{
			var character = CharacterManager.GetActiveCharacter( _data.OwnerCharacterId );
			OwnerDisplay = character?.GetVar<string>( "Name" ) ?? _data.OwnerCharacterId;
		}
		else if ( !string.IsNullOrEmpty( _data.OwnerFactionId ) )
		{
			var faction = FactionManager.GetFaction( _data.OwnerFactionId );
			OwnerDisplay = faction?.Name ?? _data.OwnerFactionId;
		}
		else
		{
			OwnerDisplay = "";
		}
	}
}

/// <summary>
/// Permission hook: can a player use this door? Return false to block.
/// </summary>
public interface ICanUseDoorListener
{
	bool CanUseDoor( HexPlayerComponent player, DoorComponent door );
}

/// <summary>
/// Fired after a player uses a door (toggle, lock/unlock).
/// </summary>
public interface IDoorUsedListener
{
	void OnDoorUsed( HexPlayerComponent player, DoorComponent door );
}

/// <summary>
/// Fired when door ownership changes.
/// </summary>
public interface IDoorOwnerChangedListener
{
	void OnDoorOwnerChanged( DoorComponent door, string oldOwnerId, string newOwnerId, bool isFaction );
}
