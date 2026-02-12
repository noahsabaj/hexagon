namespace Hexagon.Core;

/// <summary>
/// Central event system for Hexagon. Provides two mechanisms:
/// 1. Interface-based: Components implement listener interfaces and are auto-discovered via Scene.
/// 2. Static events: Non-Component code subscribes to Action delegates.
/// </summary>
public static class HexEvents
{
	internal static Scene Scene => HexagonFramework.Instance?.Scene;

	/// <summary>
	/// Fires an event to all Components in the scene that implement interface T.
	/// </summary>
	public static void Fire<T>( Action<T> action ) where T : class
	{
		if ( Scene == null ) return;

		foreach ( var listener in Scene.GetAll<T>() )
		{
			try
			{
				action( listener );
			}
			catch ( Exception ex )
			{
				Log.Error( $"Hexagon: Exception in {typeof( T ).Name} listener: {ex}" );
			}
		}
	}

	/// <summary>
	/// Fires a Can-style permission hook. All listeners must return true for the action to proceed.
	/// If any listener returns false, the action is blocked.
	/// </summary>
	public static bool CanAll<T>( Func<T, bool> check ) where T : class
	{
		if ( Scene == null ) return true;

		foreach ( var listener in Scene.GetAll<T>() )
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
	/// Fires a hook that collects a value, passing it through each listener in sequence.
	/// Each listener can modify the value before passing it to the next.
	/// </summary>
	public static T Reduce<TListener, T>( T initial, Func<TListener, T, T> reducer ) where TListener : class
	{
		if ( Scene == null ) return initial;

		var value = initial;

		foreach ( var listener in Scene.GetAll<TListener>() )
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

// --- Framework lifecycle interfaces ---

/// <summary>
/// Called when the Hexagon framework has finished initializing all systems.
/// </summary>
public interface IFrameworkInitListener
{
	void OnFrameworkInit();
}

/// <summary>
/// Called when the Hexagon framework is shutting down.
/// </summary>
public interface IFrameworkShutdownListener
{
	void OnFrameworkShutdown();
}
