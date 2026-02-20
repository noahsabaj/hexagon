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
public sealed class PermissionManager : GameObjectSystem<PermissionManager>
{
	private static PermissionManager _instance;
	private static PermissionManager Instance => _instance;

	private readonly Dictionary<char, FlagInfo> _flags = new();

	public PermissionManager( Scene scene ) : base( scene )
	{
		_instance = this;
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
		if ( Instance == null ) return;
		Instance._flags[flag] = new FlagInfo { Flag = flag, Description = description };
	}

	/// <summary>
	/// Get info about a registered flag.
	/// </summary>
	public static FlagInfo GetFlagInfo( char flag )
	{
		return Instance?._flags.GetValueOrDefault( flag );
	}

	/// <summary>
	/// Get all registered flags.
	/// </summary>
	public static IReadOnlyDictionary<char, FlagInfo> GetAllFlags()
	{
		return Instance?._flags;
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
		if ( character.HasFlag( "s" ) )
			return true;

		if ( string.IsNullOrEmpty( requirement ) )
			return true;

		var flags = Instance?._flags;

		// Short requirement (1-2 chars) where all chars are registered flags → direct flag check
		if ( flags != null && requirement.Length <= 2 && requirement.All( c => flags.ContainsKey( c ) ) )
		{
			return character.HasFlags( requirement );
		}

		// Custom permission — fire hook
		if ( OnPermissionCheck == null ) return true;

		foreach ( Func<HexPlayerComponent, string, bool> handler in OnPermissionCheck.GetInvocationList() )
		{
			if ( !handler( player, requirement ) ) return false;
		}

		return true;
	}

	/// <summary>Called when checking a custom permission string.</summary>
	public static event Func<HexPlayerComponent, string, bool> OnPermissionCheck;
}
