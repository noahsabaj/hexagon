namespace Hexagon.Interaction;

/// <summary>
/// Per-player component handling weapon raise/lower state.
/// Weapons default to lowered (cannot fire). Hold R for configurable duration to toggle.
/// When raised, there's a short delay before firing is allowed.
///
/// Schema weapon code should check CanFire before allowing shots.
/// Special weapons can set AlwaysRaised or FireWhenLowered on WeaponItemDef.
/// </summary>
public sealed class WeaponRaiseComponent : Component
{
	/// <summary>
	/// Whether the weapon is currently raised. Synced to all players for animations.
	/// </summary>
	[Sync] public bool IsWeaponRaised { get; set; } = false;

	/// <summary>
	/// Server-side: whether the player can fire right now.
	/// Only true when raised and the fire delay has elapsed.
	/// </summary>
	public bool CanFire { get; private set; } = false;

	// Client input tracking
	private TimeSince _holdStart;
	private bool _isHolding;

	// Server fire delay tracking
	private TimeSince _raisedAt;
	private bool _pendingFireDelay;

	/// <summary>
	/// Server RPC: client requests to toggle weapon raise state.
	/// </summary>
	[Rpc.Host]
	public void RequestToggleRaise()
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player.GameObject != GameObject ) return;

		// Check global override
		if ( Config.HexConfig.Get<bool>( "weapon.alwaysRaised", false ) )
			return;

		// Permission hook (only when raising)
		if ( !IsWeaponRaised && !HexEvents.CanAll<ICanRaiseWeaponListener>( x => x.CanRaiseWeapon( player ) ) )
			return;

		ToggleRaised();

		var p = player;
		var raised = IsWeaponRaised;
		HexEvents.Fire<IWeaponRaisedListener>( x => x.OnWeaponRaised( p, raised ) );
	}

	/// <summary>
	/// Server-side: explicitly set the raise state.
	/// </summary>
	public void SetRaised( bool raised )
	{
		if ( IsProxy ) return;

		IsWeaponRaised = raised;
		CanFire = false;

		if ( raised )
		{
			_raisedAt = 0;
			_pendingFireDelay = true;
		}
		else
		{
			_pendingFireDelay = false;
		}

		UpdateAnimation();
	}

	/// <summary>
	/// Server-side: toggle between raised and lowered.
	/// </summary>
	public void ToggleRaised()
	{
		SetRaised( !IsWeaponRaised );
	}

	protected override void OnUpdate()
	{
		// Server-side: tick fire delay
		if ( !IsProxy )
		{
			if ( _pendingFireDelay )
			{
				var fireDelay = Config.HexConfig.Get<float>( "weapon.fireDelay", 0.5f );
				if ( _raisedAt > fireDelay )
				{
					CanFire = true;
					_pendingFireDelay = false;
				}
			}

			// Global override: force raised
			if ( Config.HexConfig.Get<bool>( "weapon.alwaysRaised", false ) && !IsWeaponRaised )
			{
				IsWeaponRaised = true;
				CanFire = true;
			}
		}

		// Client-side: hold-R detection (only for our own player)
		if ( Network.IsOwner )
		{
			HandleInput();
		}

		// All clients: update animation
		UpdateAnimation();
	}

	private void HandleInput()
	{
		var raiseTime = Config.HexConfig.Get<float>( "weapon.raiseTime", 0.5f );

		if ( Input.Down( "Reload" ) )
		{
			if ( !_isHolding )
			{
				_isHolding = true;
				_holdStart = 0;
			}

			if ( _holdStart > raiseTime )
			{
				RequestToggleRaise();
				_isHolding = false;
			}
		}
		else
		{
			_isHolding = false;
		}
	}

	private void UpdateAnimation()
	{
		var controller = GameObject.GetComponent<PlayerController>();
		if ( controller?.Renderer == null ) return;

		// When lowered, set holdtype to 0 (passive/none)
		// When raised, don't override — let weapon code set the correct holdtype
		if ( !IsWeaponRaised )
		{
			controller.Renderer.Set( "holdtype", 0 );
		}
	}
}

/// <summary>
/// Permission hook: can this player raise their weapon? Return false to block.
/// </summary>
public interface ICanRaiseWeaponListener
{
	bool CanRaiseWeapon( HexPlayerComponent player );
}

/// <summary>
/// Fired when a player's weapon raise state changes.
/// </summary>
public interface IWeaponRaisedListener
{
	void OnWeaponRaised( HexPlayerComponent player, bool isRaised );
}

/// <summary>
/// Permission hook: can this player fire their weapon?
/// Schema weapon code should check both this hook and WeaponRaiseComponent.CanFire.
/// </summary>
public interface ICanFireWeaponListener
{
	bool CanFireWeapon( HexPlayerComponent player );
}
