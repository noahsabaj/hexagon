namespace Hexagon.Core;

/// <summary>
/// Extension methods providing CanAll and Reduce semantics on top of ISceneEvent.
/// These patterns are not built into s&box but are essential for permission hooks
/// and value-folding scenarios in the Hexagon framework.
/// </summary>
public static class SceneEventExtensions
{
	/// <summary>
	/// Permission-gate: fires a check on all listeners in the scene.
	/// All must return true for the action to proceed.
	/// If any listener returns false, the action is blocked.
	/// </summary>
	public static bool CanAll<T>( Func<T, bool> check ) where T : class
	{
		var scene = Game.ActiveScene;
		if ( scene == null ) return true;

		foreach ( var listener in scene.GetAll<T>() )
		{
			try
			{
				if ( !check( listener ) )
					return false;
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Exception in {typeof( T ).Name} can-hook: {ex}" );
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Value-fold: passes a value through each listener in sequence,
	/// allowing each to modify it before passing to the next.
	/// </summary>
	public static T Reduce<TListener, T>( T initial, Func<TListener, T, T> reducer ) where TListener : class
	{
		var scene = Game.ActiveScene;
		if ( scene == null ) return initial;

		var value = initial;

		foreach ( var listener in scene.GetAll<TListener>() )
		{
			try
			{
				value = reducer( listener, value );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Exception in {typeof( TListener ).Name} reducer: {ex}" );
			}
		}

		return value;
	}
}
