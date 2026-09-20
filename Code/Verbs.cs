#nullable enable

using System.Collections.Generic;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>Something a character can do to a target. <paramref name="Requires"/> names a capability, or is null when anyone may.</summary>
public sealed record Verb( string Id, string Title, string? Requires = null );

/// <summary>
/// A component characters can act on. It only lists its verbs and carries them out. Who is asking,
/// whether they stand close enough, whether they are able, the rate limit and the journal entry
/// are all decided in one place, <see cref="Player.RequestAct"/>, so no target can forget one.
/// The first verb answers the Use key and the second the Reload key.
/// </summary>
public interface IVerbTarget
{
	IReadOnlyList<Verb> Verbs { get; }

	/// <summary>How far a character may stand from the target's bounds and still act on it.</summary>
	float Reach => 150f;

	/// <summary>Host only, after every check has passed. A failure's message is shown to the actor.</summary>
	Result Perform( Player actor, Verb verb );
}
