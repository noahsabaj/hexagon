#nullable enable

namespace Hexagon;

/// <summary>
/// Something whose lock can be changed for another, a door or a locker: it has the lock it was built
/// with until a new one is fitted, and whoever holds the title to its building fits it. The lock that
/// comes out is a lock like any other, cut as it was.
/// </summary>
public interface ILockable
{
	/// <summary>Host: what its lock is cut to now. Empty is no lock.</summary>
	string HostPattern { get; }

	/// <summary>
	/// Host: a lock cut to <paramref name="pattern"/> goes in, in place of the one in it. The world is
	/// changed but not saved: whoever fits it saves the world together with the hands the lock left.
	/// </summary>
	void HostFit( string pattern );
}
