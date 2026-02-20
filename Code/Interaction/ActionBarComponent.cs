namespace Hexagon.Interaction;

/// <summary>
/// Per-player server-side action bar logic. Holds the active timed action,
/// performs per-frame stare/distance validation, and fires completion/cancellation.
/// Added to the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class ActionBarComponent : Component
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

	private ActiveAction _action;

	/// <summary>
	/// Returns true if this player has an active timed action.
	/// </summary>
	public bool HasAction => _action != null;

	/// <summary>
	/// Start a timed action with a progress bar.
	/// Any existing action is replaced.
	/// </summary>
	public void SetAction( string text, float time, Action<HexPlayerComponent> callback )
	{
		var player = GetComponent<HexPlayerComponent>();
		if ( player == null || time <= 0 ) return;

		if ( OnCanStartAction != null )
		{
			foreach ( Func<HexPlayerComponent, string, bool> handler in OnCanStartAction.GetInvocationList() )
			{
				if ( !handler( player, text ) ) return;
			}
		}

		_action = new ActiveAction
		{
			Text = text,
			StartTime = Time.Now,
			EndTime = Time.Now + time,
			Callback = callback
		};

		GetComponent<ActionBarPlayerComponent>()?.ReceiveActionBar( _action.StartTime, _action.EndTime, text );
		IHexActionEvent.Post( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>
	/// Start a stared action that cancels if the player looks away or moves too far.
	/// </summary>
	public void DoStaredAction( GameObject target, string text, float time,
		Action<HexPlayerComponent> callback, Action onCancel = null, float maxDistance = 130f )
	{
		var player = GetComponent<HexPlayerComponent>();
		if ( player == null || target == null || time <= 0 ) return;

		if ( OnCanStartAction != null )
		{
			foreach ( Func<HexPlayerComponent, string, bool> handler in OnCanStartAction.GetInvocationList() )
			{
				if ( !handler( player, text ) ) return;
			}
		}

		_action = new ActiveAction
		{
			Text = text,
			StartTime = Time.Now,
			EndTime = Time.Now + time,
			Callback = callback,
			StareTarget = target,
			OnCancel = onCancel,
			MaxDistance = maxDistance
		};

		GetComponent<ActionBarPlayerComponent>()?.ReceiveActionBar( _action.StartTime, _action.EndTime, text );
		IHexActionEvent.Post( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>
	/// Cancel the current action. Fires the cancel callback if set.
	/// </summary>
	public void CancelAction()
	{
		if ( _action == null ) return;

		var player = GetComponent<HexPlayerComponent>();
		var action = _action;
		_action = null;

		if ( player != null )
			CancelAndNotify( player, action );
	}

	/// <summary>
	/// Clear the action without firing any callbacks (e.g., on disconnect).
	/// </summary>
	internal void ClearAction()
	{
		_action = null;
	}

	protected override void OnUpdate()
	{
		if ( _action == null ) return;
		if ( IsProxy ) return;

		var now = Time.Now;
		var player = GetComponent<HexPlayerComponent>();
		if ( player == null )
		{
			_action = null;
			return;
		}

		// Check completion
		if ( now >= _action.EndTime )
		{
			var action = _action;
			_action = null;

			GetComponent<ActionBarPlayerComponent>()?.ReceiveActionBarReset();

			try
			{
				action.Callback?.Invoke( player );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Action callback error: {ex}" );
			}

			IHexActionEvent.Post( x => x.OnActionCompleted( player, action.Text ) );
			IHexActionEvent.Post( x => x.OnActionBarUpdated( player ) );
			return;
		}

		// Check stared action validity
		if ( _action.StareTarget != null )
		{
			if ( !IsStareValid( player ) )
			{
				var action = _action;
				_action = null;
				CancelAndNotify( player, action );
			}
		}
	}

	private bool IsStareValid( HexPlayerComponent player )
	{
		var action = _action;
		if ( action?.StareTarget == null ) return false;

		if ( !action.StareTarget.IsValid() )
			return false;

		var controller = player.GameObject.GetComponent<PlayerController>();
		if ( controller == null )
			return false;

		// Distance check
		var dist = Vector3.DistanceBetween( player.WorldPosition, action.StareTarget.WorldPosition );
		if ( dist > action.MaxDistance )
			return false;

		// Stare check — raycast from eyes
		var from = controller.EyePosition;
		var to = from + controller.EyeAngles.Forward * action.MaxDistance;
		var tr = Scene.Trace.Ray( from, to )
			.IgnoreGameObjectHierarchy( player.GameObject )
			.Run();

		if ( !tr.Hit || tr.GameObject == null )
			return false;

		// Check if hit is the target or a child of the target
		var hitGo = tr.GameObject;
		return hitGo == action.StareTarget || hitGo.Parent == action.StareTarget;
	}

	private void CancelAndNotify( HexPlayerComponent player, ActiveAction action )
	{
		action.OnCancel?.Invoke();
		GetComponent<ActionBarPlayerComponent>()?.ReceiveActionBarReset();
		IHexActionEvent.Post( x => x.OnActionCancelled( player, action.Text ) );
		IHexActionEvent.Post( x => x.OnActionBarUpdated( player ) );
	}

	/// <summary>Called to check if an action can be started.</summary>
	public static event Func<HexPlayerComponent, string, bool> OnCanStartAction;
}
