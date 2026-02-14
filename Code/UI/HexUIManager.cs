namespace Hexagon.UI;

/// <summary>
/// UI state machine states.
/// </summary>
public enum UIState
{
	Loading,
	CharacterSelect,
	CharacterCreate,
	Gameplay,
	Dead
}

/// <summary>
/// Central UI coordinator. Manages panel visibility, input dispatch, cursor state,
/// and the UI state machine. Lives on the ScreenPanel GameObject.
///
/// Schema devs can replace individual panels by disabling the defaults and adding
/// their own IHexPanel implementations. HexUIManager discovers panels via
/// Scene.GetAll&lt;IHexPanel&gt;().
/// </summary>
public sealed class HexUIManager : Component, ICharacterLoadedListener, ICharacterUnloadedListener
{
	public static HexUIManager Instance { get; private set; }

	/// <summary>
	/// Current UI state.
	/// </summary>
	public UIState State { get; private set; } = UIState.Loading;

	private readonly List<IHexPanel> _openPanels = new();
	private bool _tabHeld;

	protected override void OnStart()
	{
		if ( Instance != null && Instance != this )
		{
			Destroy();
			return;
		}

		Instance = this;

		// Disable framework defaults that schema panels override
		DisableOverriddenDefaults();

		// Start in character select state
		SetState( UIState.CharacterSelect );
	}

	/// <summary>
	/// Scan for schema panels that share a PanelName with framework defaults.
	/// When found, the framework default is disabled so the schema panel takes over.
	/// </summary>
	private void DisableOverriddenDefaults()
	{
		var byName = new Dictionary<string, List<IHexPanel>>();

		foreach ( var panel in Scene.GetAll<IHexPanel>() )
		{
			if ( !byName.TryGetValue( panel.PanelName, out var list ) )
			{
				list = new();
				byName[panel.PanelName] = list;
			}
			list.Add( panel );
		}

		foreach ( var (name, group) in byName )
		{
			if ( group.Count <= 1 ) continue;

			bool hasSchemaOverride = group.Any( p =>
				p is Component c && c.GameObject != HexUISetup.UIObject );

			if ( !hasSchemaOverride ) continue;

			foreach ( var panel in group )
			{
				if ( panel is Component comp && comp.GameObject == HexUISetup.UIObject )
				{
					comp.Enabled = false;
					Log.Info( $"Hexagon: Default '{name}' panel overridden by schema" );
				}
			}
		}
	}

	protected override void OnDestroy()
	{
		if ( Instance == this )
			Instance = null;
	}

	protected override void OnUpdate()
	{
		HandleInput();
		UpdateCursor();
		CheckDeathState();
	}

	// --- State Machine ---

	/// <summary>
	/// Transition to a new UI state. Hides/shows panels appropriate to that state.
	/// </summary>
	public void SetState( UIState newState )
	{
		if ( State == newState ) return;

		var oldState = State;
		State = newState;

		// Close all managed panels
		foreach ( var panel in _openPanels.ToList() )
		{
			panel.Close();
		}
		_openPanels.Clear();

		// Open panels for new state
		switch ( newState )
		{
			case UIState.CharacterSelect:
				OpenPanel( "CharacterSelect" );
				ClosePanel( "CharacterCreate" );
				ClosePanel( "HUD" );
				ClosePanel( "Chat" );
				break;

			case UIState.CharacterCreate:
				OpenPanel( "CharacterCreate" );
				ClosePanel( "CharacterSelect" );
				ClosePanel( "HUD" );
				ClosePanel( "Chat" );
				break;

			case UIState.Gameplay:
				OpenPanel( "HUD" );
				OpenPanel( "Chat" );
				ClosePanel( "CharacterSelect" );
				ClosePanel( "CharacterCreate" );
				ClosePanel( "DeathScreen" );
				break;

			case UIState.Dead:
				OpenPanel( "DeathScreen" );
				OpenPanel( "Chat" );
				ClosePanel( "HUD" );
				break;
		}
	}

	// --- Input Dispatch ---

	private void HandleInput()
	{
		if ( State != UIState.Gameplay && State != UIState.Dead )
			return;

		// TAB — Scoreboard toggle
		if ( Input.Down( "Score" ) && !_tabHeld )
		{
			_tabHeld = true;
			OpenPanel( "Scoreboard" );
		}
		else if ( !Input.Down( "Score" ) && _tabHeld )
		{
			_tabHeld = false;
			ClosePanel( "Scoreboard" );
		}

		// I — Toggle inventory
		if ( Input.Pressed( "Inventory" ) && State == UIState.Gameplay )
		{
			TogglePanel( "Inventory" );
		}

		// ENTER / Y — Focus chat input
		if ( Input.Pressed( "Chat" ) )
		{
			var chat = FindPanel( "Chat" );
			if ( chat != null && !chat.IsOpen )
				chat.Open();

			// The ChatPanel itself handles focusing the input
			HexEvents.Fire<IChatFocusRequestListener>( x => x.OnChatFocusRequested() );
		}

		// F3 — Toggle introduce menu
		if ( Input.Pressed( "Slot3" ) && State == UIState.Gameplay )
		{
			TogglePanel( "IntroduceMenu" );
		}

		// ESC — Close topmost panel
		if ( Input.Pressed( "Menu" ) )
		{
			CloseTopmostPanel();
		}
	}

	// --- Cursor Management ---

	private void UpdateCursor()
	{
		var anyOpen = HasOpenOverlayPanel();

		// In character select/create, always show cursor
		if ( State == UIState.CharacterSelect || State == UIState.CharacterCreate || State == UIState.Dead )
		{
			Mouse.Visibility = MouseVisibility.Visible;
			return;
		}

		Mouse.Visibility = anyOpen ? MouseVisibility.Visible : MouseVisibility.Auto;
	}

	private bool HasOpenOverlayPanel()
	{
		foreach ( var panel in Scene.GetAll<IHexPanel>() )
		{
			if ( !panel.IsOpen ) continue;

			var name = panel.PanelName;
			// HUD and Chat are always-visible, don't count as overlay
			if ( name == "HUD" || name == "Chat" )
				continue;

			return true;
		}

		return false;
	}

	// --- Panel Management ---

	/// <summary>
	/// Find a panel by name. Schema panels take priority over framework defaults.
	/// </summary>
	public IHexPanel FindPanel( string name )
	{
		IHexPanel fallback = null;

		foreach ( var panel in Scene.GetAll<IHexPanel>() )
		{
			if ( panel.PanelName != name ) continue;

			// Schema panel (not on the framework UI object) wins immediately
			if ( panel is Component comp && comp.GameObject != HexUISetup.UIObject )
				return panel;

			fallback ??= panel;
		}

		return fallback;
	}

	/// <summary>
	/// Open a panel by name.
	/// </summary>
	public void OpenPanel( string name )
	{
		var panel = FindPanel( name );
		if ( panel == null ) return;

		if ( !panel.IsOpen )
		{
			panel.Open();

			if ( !_openPanels.Contains( panel ) )
				_openPanels.Add( panel );
		}
	}

	/// <summary>
	/// Close a panel by name.
	/// </summary>
	public void ClosePanel( string name )
	{
		var panel = FindPanel( name );
		if ( panel == null ) return;

		if ( panel.IsOpen )
		{
			panel.Close();
		}

		_openPanels.Remove( panel );
	}

	/// <summary>
	/// Toggle a panel open/closed by name.
	/// </summary>
	public void TogglePanel( string name )
	{
		var panel = FindPanel( name );
		if ( panel == null ) return;

		if ( panel.IsOpen )
		{
			panel.Close();
			_openPanels.Remove( panel );
		}
		else
		{
			panel.Open();
			if ( !_openPanels.Contains( panel ) )
				_openPanels.Add( panel );
		}
	}

	/// <summary>
	/// Close the topmost open overlay panel.
	/// </summary>
	public void CloseTopmostPanel()
	{
		for ( int i = _openPanels.Count - 1; i >= 0; i-- )
		{
			var panel = _openPanels[i];
			var name = panel.PanelName;

			// Don't close HUD or Chat via ESC
			if ( name == "HUD" || name == "Chat" )
				continue;

			panel.Close();
			_openPanels.RemoveAt( i );
			return;
		}
	}

	// --- Event Listeners ---

	public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		// Only react to our local player
		if ( player.IsProxy ) return;

		SetState( UIState.Gameplay );
	}

	public void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
	{
		if ( player.IsProxy ) return;

		// Strip player body back to bare networking object
		HexPlayerSetup.StripPlayerBody( player.GameObject );

		SetState( UIState.CharacterSelect );
	}

	// --- Death Check ---

	private void CheckDeathState()
	{
		if ( State != UIState.Gameplay && State != UIState.Dead )
			return;

		// Find local player
		var localPlayer = GetLocalPlayer();
		if ( localPlayer == null ) return;

		if ( localPlayer.IsDead && State != UIState.Dead )
		{
			SetState( UIState.Dead );
		}
		else if ( !localPlayer.IsDead && State == UIState.Dead )
		{
			SetState( UIState.Gameplay );
		}
	}

	/// <summary>
	/// Get the local player's HexPlayerComponent.
	/// </summary>
	public static HexPlayerComponent GetLocalPlayer()
	{
		if ( Instance == null ) return null;

		foreach ( var player in Instance.Scene.GetAll<HexPlayerComponent>() )
		{
			if ( !player.IsProxy )
				return player;
		}

		return null;
	}
}

/// <summary>
/// Client-side: fired when the chat input should be focused (ENTER pressed).
/// </summary>
public interface IChatFocusRequestListener
{
	void OnChatFocusRequested();
}
