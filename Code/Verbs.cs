#nullable enable

using System.Collections.Generic;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>
/// A component characters can act on. It only lists its verbs and carries them out. Who is asking,
/// whether they stand close enough, whether they are able, the rate limit and the journal entry
/// are all decided in one place, <see cref="Player.RequestAct"/>, so no target can forget one.
/// The Use key does the first verb, unless <see cref="UseOpensMenu"/>; the menu key lists them all.
/// </summary>
public interface IVerbTarget
{
	IReadOnlyList<Verb> Verbs { get; }

	/// <summary>How far a character may stand from the target's bounds and still act on it.</summary>
	float Reach => 150f;

	/// <summary>When true, the Use key lists the verbs instead of doing the first, as for a person: nothing is done to someone by accident.</summary>
	bool UseOpensMenu => false;

	/// <summary>How the journal names the target, or null for its kind and object id.</summary>
	string? JournalSubject => null;

	/// <summary>Host only, after every check has passed. A failure's message is shown to the actor.</summary>
	Result Perform( Player actor, Verb verb );
}
