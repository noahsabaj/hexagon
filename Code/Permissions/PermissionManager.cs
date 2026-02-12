namespace Hexagon.Permissions;

/// <summary>
/// Info about a registered permission flag.
/// </summary>
public class FlagInfo
{
	public char Flag { get; set; }
	public string Description { get; set; }
}

/// <summary>
/// Manages permission flags and provides permission checks.
/// Flags are single characters assigned to characters (e.g. 'a' = Admin, 's' = Super Admin).
/// The 's' flag bypasses all permission checks.
/// </summary>
public static class PermissionManager
{
	private static readonly Dictionary<char, FlagInfo> _flags = new();

	internal static void Initialize()
	{
		_flags.Clear();

		// Default flags
		RegisterFlag( 'p', "Physgun access" );
		RegisterFlag( 't', "Toolgun access" );
		RegisterFlag( 'e', "Entity spawn access" );
		RegisterFlag( 'o', "Door ownership" );
		RegisterFlag( 'v', "Vendor management" );
		RegisterFlag( 'a', "Admin" );
		RegisterFlag( 's', "Super Admin" );

		Log.Info( $"Hexagon: PermissionManager initialized with {_flags.Count} flags." );
	}

	/// <summary>
	/// Register a permission flag with a description.
	/// </summary>
	public static void RegisterFlag( char flag, string description )
	{
		_flags[flag] = new FlagInfo { Flag = flag, Description = description };
	}

	/// <summary>
	/// Get info about a registered flag.
	/// </summary>
	public static FlagInfo GetFlagInfo( char flag )
	{
		return _flags.GetValueOrDefault( flag );
	}

	/// <summary>
	/// Get all registered flags.
	/// </summary>
	public static IReadOnlyDictionary<char, FlagInfo> GetAllFlags()
	{
		return _flags;
	}

	/// <summary>
	/// Check if a player has permission for a given requirement.
	///
	/// If requirement is 1-2 characters and all are registered flags, checks character flags directly.
	/// Otherwise fires IPermissionCheckListener for schema-defined permissions.
	///
	/// The 's' (Super Admin) flag bypasses all checks.
	/// </summary>
	public static bool HasPermission( HexPlayerComponent player, string requirement )
	{
		if ( player?.Character == null )
			return false;

		var character = player.Character;

		// Super admin bypasses all
		if ( character.HasFlag( 's' ) )
			return true;

		if ( string.IsNullOrEmpty( requirement ) )
			return true;

		// Short requirement (1-2 chars) where all chars are registered flags → direct flag check
		if ( requirement.Length <= 2 && requirement.All( c => _flags.ContainsKey( c ) ) )
		{
			return character.HasFlags( requirement );
		}

		// Custom permission — fire hook
		return HexEvents.CanAll<IPermissionCheckListener>(
			x => x.OnPermissionCheck( player, requirement )
		);
	}
}

/// <summary>
/// Hook for schema-defined permission checks beyond simple flags.
/// Return false to deny the permission.
/// </summary>
public interface IPermissionCheckListener
{
	bool OnPermissionCheck( HexPlayerComponent player, string permission );
}
