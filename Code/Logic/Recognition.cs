#nullable enable

using System;

namespace Hexagon.Logic;

/// <summary>
/// A name is knowledge. A character knows its own, and another's only once that other has
/// introduced themselves; until then a stranger is what they look like. What each character knows
/// is part of its own document, so it survives restarts and is never sent to anyone else.
/// </summary>
public static class Recognition
{
	public const int MaximumLabel = 40;

	/// <summary>How <paramref name="listener"/> would refer to <paramref name="subject"/>.</summary>
	public static string Label( CharacterData listener, CharacterData subject )
	{
		if ( listener.Id == subject.Id ) return subject.Name;
		return listener.Known.TryGetValue( subject.Id, out var name ) ? name : Stranger( subject.Description );
	}

	/// <summary>A stranger is described, in brackets so nobody mistakes it for a name.</summary>
	public static string Stranger( string description )
	{
		var text = description.Trim();
		if ( text.Length > MaximumLabel ) text = text[..MaximumLabel].TrimEnd() + "...";
		return $"[{text}]";
	}

	/// <summary>The subject tells the listener their name. Telling again after a rename updates it.</summary>
	public static Result Introduce( CharacterData subject, CharacterData listener )
	{
		if ( subject.Id == listener.Id ) return Result.Fail( ErrorCode.Invalid, "You know who you are." );
		if ( listener.Known.TryGetValue( subject.Id, out var known ) && known == subject.Name )
			return Result.Fail( ErrorCode.Conflict, "They already know you." );
		listener.Known[subject.Id] = subject.Name;
		return Result.Success();
	}
}
