#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Runtime;

/// <summary>
/// Carries an unfinished drain across a scene replacement. A new runtime for the
/// same physical data root must await the previous owner before opening storage.
/// </summary>
internal static class SceneShutdownBarrier
{
	private static readonly object Sync = new();
	private static readonly Dictionary<string, Task<OperationResult>> Drains =
		new( StringComparer.Ordinal );

	public static Task<OperationResult>? Previous( string root )
	{
		lock ( Sync ) return Drains.TryGetValue( root, out var drain ) ? drain : null;
	}

	public static void Publish( string root, Task<OperationResult> drain )
	{
		ArgumentException.ThrowIfNullOrWhiteSpace( root );
		ArgumentNullException.ThrowIfNull( drain );
		lock ( Sync ) Drains[root] = drain;
	}

	public static void Clear( string root, Task<OperationResult> drain )
	{
		lock ( Sync )
		{
			if ( Drains.TryGetValue( root, out var current ) && ReferenceEquals( current, drain ) )
				Drains.Remove( root );
		}
	}
}
