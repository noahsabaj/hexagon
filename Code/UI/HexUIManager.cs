namespace Hexagon.UI;

/// <summary>
/// Central UI coordinator. Manages panel visibility, input dispatch, cursor state,
/// and the UI state machine. Scene-level singleton (GameObjectSystem).
///
/// Schema devs can replace individual panels by adding their own IHexPanel
/// implementations anywhere in the scene. HexUIManager discovers all panels via
/// Scene.GetAll and automatically disables framework defaults that share a PanelName.
/// </summary>
public sealed class HexUIManager : GameObjectSystem<HexUIManager>
{
	private static HexUIManager _instance;

	/// <summary>
	/// Active HexUIManager instance.
	/// </summary>
	public static HexUIManager Instance => _instance;

	/// <summary>
	/// Current UI state.
	/// </summary>
	public string State { get; private set; } = HexUIStates.Loading;

	private readonly Dictionary<string, UIStateConfig> _states = new();

	private readonly List<IHexPanel> _openPanels = new();
	private List<IHexPanel> _panels = new();
	private bool _tabHeld;
	private bool _initialized;

	public HexUIManager( Scene scene ) : base( scene )
	{
		_instance = this;
		Listen( Stage.StartUpdate, 0, OnTick, "HexUIManager.Tick" );
		RegisterDefaultStates();
	}

	public override void Dispose()
	{
		if ( _instance == this )
			_instance = null;
		base.Dispose();
	}

	public void RegisterState( string name, UIStateConfig config )
	{
		_states[name] = config;
	}

	private void RegisterDefaultStates()
	{
		RegisterState( HexUIStates.Intro, new UIStateConfig
		{
			OnEnter = () => OpenPanel( "Intro" )
		} );

		RegisterState( HexUIStates.CharacterSelect, new UIStateConfig
		{
			OnEnter = () => OpenPanel( "CharacterSelect" ),
			ForceCursorVisible = true
		} );

		RegisterState( HexUIStates.CharacterCreate, new UIStateConfig
		{
			OnEnter = () => OpenPanel( "CharacterCreate" ),
			ForceCursorVisible = true
		} );

		RegisterState( HexUIStates.Gameplay, new UIStateConfig
		{
			OnEnter = () => { OpenPanel( "HUD" ); OpenPanel( "Chat" ); },
			AllowGameplayInput = true
		} );

		RegisterState( HexUIStates.Dead, new UIStateConfig
		{
			OnEnter = () => { OpenPanel( "DeathScreen" ); OpenPanel( "Chat" ); },
			ForceCursorVisible = true,
			AllowGameplayInput = true
		} );
	}

	private void OnTick()
	{
		if ( Application.IsHeadless ) return;

		// Deferred init: wait for HexagonSystem to finish before building the panel cache.
		// EnsureUI() is called from HexagonSystem.OnHostInitialize(), so all panel GOs
		// exist by the time IsInitialized is true.
		if ( !_initialized && Core.HexagonSystem.IsInitialized )
		{
			_initialized = true;
			DisableOverriddenDefaults();
			_panels = Scene.GetAll<IHexPanel>().ToList();
			SetState( HexUIStates.Intro );
		}

		if ( !_initialized ) return;

		HandleInput();
		UpdateCursor();
		CheckDeathState();
	}

	// --- Character Event Handlers (called from HexUIManagerBridge) ---

	internal void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
	{
		if ( player.IsProxy ) return;
		SetState( HexUIStates.Gameplay );
	}

	internal void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
	{
		if ( player.IsProxy ) return;
		HexPlayerSetup.StripPlayerBody( player.GameObject );
		SetState( HexUIStates.CharacterSelect );
	}

	// --- Schema Override Detection ---

	/// <summary>
	/// Returns true if the panel belongs to the framework UI hierarchy (is UIObject or
	/// a descendant of UIObject). Used to distinguish defaults from schema overrides.
	/// </summary>
	internal static bool IsFrameworkPanel( IHexPanel panel )
	{
		if ( panel is not Component comp ) return false;
		var go = comp.GameObject;
		while ( go != null )
		{
			if ( go == HexUISetup.UIObject ) return true;
			go = go.Parent;
		}
		return false;
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

			bool hasSchemaOverride = group.Any( p => !IsFrameworkPanel( p ) );
			if ( !hasSchemaOverride ) continue;

			foreach ( var panel in group )
			{
				if ( IsFrameworkPanel( panel ) && panel is Component comp )
				{
					comp.Enabled = false;
					Log.Info( $"Hexagon: Default '{name}' panel overridden by schema" );
				}
			}
		}
	}

	// --- State Machine ---

	/// <summary>
	/// Transition to a new UI state. Closes all open panels, then opens those
	/// appropriate for the new state.
	/// </summary>
	public void SetState( string newState )
	{
		if ( State == newState ) return;

		State = newState;

		foreach ( var panel in _openPanels.ToList() )
			panel.Close();
		_openPanels.Clear();

		if ( _states.TryGetValue( newState, out var config ) )
		{
			config.OnEnter?.Invoke();
		}
		else
		{
			Log.Warning( $"Hexagon: Attempted to transition to unregistered UI state '{newState}'" );
		}
	}

	// --- Input Dispatch ---

	private void HandleInput()
	{
		if ( !_states.TryGetValue( State, out var config ) || !config.AllowGameplayInput )
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
		if ( Input.Pressed( "Inventory" ) && State == HexUIStates.Gameplay )
		{
			TogglePanel( "Inventory" );
		}

		// ENTER / Y — Focus chat input
		if ( Input.Pressed( "Chat" ) )
		{
			var chat = FindPanel( "Chat" );
			if ( chat != null && !chat.IsOpen )
				chat.Open();

			IHexChatEvent.Post( x => x.OnChatFocusRequested() );
		}

		// F3 — Toggle introduce menu
		if ( Input.Pressed( "Slot3" ) && State == HexUIStates.Gameplay )
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
		if ( _states.TryGetValue( State, out var config ) && config.ForceCursorVisible )
		{
			Mouse.Visibility = MouseVisibility.Visible;
			return;
		}

		Mouse.Visibility = HasOpenOverlayPanel() ? MouseVisibility.Visible : MouseVisibility.Auto;
	}

	private bool HasOpenOverlayPanel()
	{
		foreach ( var panel in _panels )
		{
			if ( !panel.IsOpen ) continue;

			var name = panel.PanelName;
			// HUD and Chat are always-visible, don't count as overlays
			if ( name == "HUD" || name == "Chat" )
				continue;

			return true;
		}

		return false;
	}

	// --- Panel Management ---

	/// <summary>
	/// Find a panel by name. Schema panels (non-framework) take priority over defaults.
	/// </summary>
	public IHexPanel FindPanel( string name )
	{
		IHexPanel fallback = null;

		foreach ( var panel in _panels )
		{
			if ( panel.PanelName != name ) continue;

			if ( !IsFrameworkPanel( panel ) )
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
			panel.Close();

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
	/// Close the topmost open overlay panel (ESC behavior).
	/// </summary>
	public void CloseTopmostPanel()
	{
		for ( int i = _openPanels.Count - 1; i >= 0; i-- )
		{
			var panel = _openPanels[i];
			var name = panel.PanelName;

			if ( name == "HUD" || name == "Chat" )
				continue;

			panel.Close();
			_openPanels.RemoveAt( i );
			return;
		}
	}

	// --- Death Check ---

	private void CheckDeathState()
	{
		if ( !_states.TryGetValue( State, out var config ) || !config.AllowGameplayInput )
			return;

		var localPlayer = GetLocalPlayer();
		if ( localPlayer == null ) return;

		if ( localPlayer.IsDead && State != HexUIStates.Dead )
			SetState( HexUIStates.Dead );
		else if ( !localPlayer.IsDead && State == HexUIStates.Dead )
			SetState( HexUIStates.Gameplay );
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
/// Thin bridge component that receives IHexCharacterEvent scene events and forwards
/// them to HexUIManager. Required because GameObjectSystem instances are not
/// discoverable by ISceneEvent dispatch, which only iterates Components.
/// </summary>
internal sealed class HexUIManagerBridge : Component, IHexCharacterEvent
{
	void IHexCharacterEvent.OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
		=> HexUIManager.Instance?.OnCharacterLoaded( player, character );

	void IHexCharacterEvent.OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character )
		=> HexUIManager.Instance?.OnCharacterUnloaded( player, character );
}

