namespace Hexagon.Interaction;

/// <summary>
/// Manages timed actions with progress bars. Supports basic timed actions and
/// stared actions that cancel if the player looks away from the target.
///
/// Usage: ActionBarManager.SetAction(player, "Searching...", 3f, callback)
/// Stared: ActionBarManager.DoStaredAction(player, target, "Lockpicking...", 5f, callback, onCancel)
/// </summary>
public static class ActionBarManager
{
	private class ActiveAction
	{
		public string Text;
		public float StartTime;
		public float EndTime;
		public Action<HexPlayerComponent> Callback;
		public GameObject StareTarget;
		public Action OnCancel;
		public float MaxDistance;
	}

	private static readonly Dictionary<ulong, ActiveAction> _actions = new();

	internal static void Initialize()
	{
		_actions.Clear();
		Log.Info( "Hexagon: ActionBarManager initialized." );
	}

	/// <summary>
	/// Start a timed action with a progress bar for the given player.
	/// Any existing action is replaced.
	/// </summary>
	public static void SetAction( HexPlayerComponent player, string text, float time, Action<HexPlayerComponent> callback )
	{
		if ( player == null || time <= 0 ) return;

		// Permission hook
		if ( !HexEvents.CanAll<ICanStartActionListener>( x => x.CanStartAction( player, text ) ) )
			return;

		var action = new ActiveAction
		{
			Text = text,
			StartTime = Time.Now,
			EndTime = Time.Now + time,
			Callback = callback
		};

		_actions[player.SteamId] = action;

		// Notify client
		player.ReceiveActionBar( action.StartTime, action.EndTime, text );

		HexEvents.Fire<IActionBarUpdatedListener>( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>
	/// Start a stared action that cancels if the player looks away from the target
	/// or moves too far.
	/// </summary>
	public static void DoStaredAction( HexPlayerComponent player, GameObject target, string text, float time,
		Action<HexPlayerComponent> callback, Action onCancel = null, float maxDistance = 130f )
	{
		if ( player == null || target == null || time <= 0 ) return;

		// Permission hook
		if ( !HexEvents.CanAll<ICanStartActionListener>( x => x.CanStartAction( player, text ) ) )
			return;

		var action = new ActiveAction
		{
			Text = text,
			StartTime = Time.Now,
			EndTime = Time.Now + time,
			Callback = callback,
			StareTarget = target,
			OnCancel = onCancel,
			MaxDistance = maxDistance
		};

		_actions[player.SteamId] = action;

		player.ReceiveActionBar( action.StartTime, action.EndTime, text );

		HexEvents.Fire<IActionBarUpdatedListener>( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>
	/// Cancel the current action for a player. Fires the cancel callback if set.
	/// </summary>
	public static void CancelAction( HexPlayerComponent player )
	{
		if ( player == null ) return;

		if ( _actions.TryGetValue( player.SteamId, out var action ) )
		{
			_actions.Remove( player.SteamId );
			CancelAndNotify( player, action );
		}
	}

	/// <summary>
	/// Returns true if the player has an active timed action.
	/// </summary>
	public static bool HasAction( HexPlayerComponent player )
	{
		return player != null && _actions.ContainsKey( player.SteamId );
	}

	/// <summary>
	/// Called every frame by the framework to tick actions and check stare validity.
	/// </summary>
	internal static void Update()
	{
		var now = Time.Now;
		var toComplete = new List<ulong>();
		var toCancel = new List<ulong>();

		foreach ( var kvp in _actions )
		{
			var steamId = kvp.Key;
			var action = kvp.Value;

			// Check completion
			if ( now >= action.EndTime )
			{
				toComplete.Add( steamId );
				continue;
			}

			// Check stared action validity
			if ( action.StareTarget != null )
			{
				if ( !action.StareTarget.IsValid() )
				{
					toCancel.Add( steamId );
					continue;
				}

				var player = HexGameManager.GetPlayer( steamId );
				if ( player == null )
				{
					toCancel.Add( steamId );
					continue;
				}

				var controller = player.GameObject.GetComponent<PlayerController>();
				if ( controller == null )
				{
					toCancel.Add( steamId );
					continue;
				}

				// Distance check
				var dist = Vector3.DistanceBetween( player.WorldPosition, action.StareTarget.WorldPosition );
				if ( dist > action.MaxDistance )
				{
					toCancel.Add( steamId );
					continue;
				}

				// Stare check — raycast from eyes
				var from = controller.EyePosition;
				var to = from + controller.EyeAngles.Forward * action.MaxDistance;
				var tr = player.Scene.Trace.Ray( from, to )
					.IgnoreGameObjectHierarchy( player.GameObject )
					.Run();

				if ( !tr.Hit || tr.GameObject == null )
				{
					toCancel.Add( steamId );
					continue;
				}

				// Check if hit is the target or a child of the target
				var hitGo = tr.GameObject;
				if ( hitGo != action.StareTarget && hitGo.Parent != action.StareTarget )
				{
					toCancel.Add( steamId );
				}
			}
		}

		// Process cancellations
		foreach ( var steamId in toCancel )
		{
			if ( !_actions.TryGetValue( steamId, out var action ) ) continue;
			_actions.Remove( steamId );

			var player = HexGameManager.GetPlayer( steamId );
			if ( player != null )
				CancelAndNotify( player, action );
		}

		// Process completions
		foreach ( var steamId in toComplete )
		{
			if ( !_actions.TryGetValue( steamId, out var action ) ) continue;
			_actions.Remove( steamId );

			var player = HexGameManager.GetPlayer( steamId );
			if ( player == null ) continue;

			player.ReceiveActionBarReset();

			try
			{
				action.Callback?.Invoke( player );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Action callback error: {ex}" );
			}

			HexEvents.Fire<IActionCompletedListener>( x => x.OnActionCompleted( player, action.Text ) );
			HexEvents.Fire<IActionBarUpdatedListener>( x => x.OnActionBarUpdated( player ) );
		}
	}

	private static void CancelAndNotify( HexPlayerComponent player, ActiveAction action )
	{
		action.OnCancel?.Invoke();
		player.ReceiveActionBarReset();
		HexEvents.Fire<IActionCancelledListener>( x => x.OnActionCancelled( player, action.Text ) );
		HexEvents.Fire<IActionBarUpdatedListener>( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>
	/// Remove all actions for a player (e.g., on disconnect).
	/// </summary>
	internal static void RemovePlayer( ulong steamId )
	{
		_actions.Remove( steamId );
	}
}

/// <summary>
/// Permission hook: can this timed action start? Return false to block.
/// </summary>
public interface ICanStartActionListener
{
	bool CanStartAction( HexPlayerComponent player, string actionText );
}

/// <summary>
/// Fired when a timed action completes successfully.
/// </summary>
public interface IActionCompletedListener
{
	void OnActionCompleted( HexPlayerComponent player, string actionText );
}

/// <summary>
/// Fired when a timed action is cancelled (looked away, moved too far, or explicit cancel).
/// </summary>
public interface IActionCancelledListener
{
	void OnActionCancelled( HexPlayerComponent player, string actionText );
}

/// <summary>
/// Client-side: fired when action bar state changes (for UI updates).
/// </summary>
public interface IActionBarUpdatedListener
{
	void OnActionBarUpdated( HexPlayerComponent player );
}
