#nullable enable

using System;

namespace Hexagon.V2.Application;

public interface IHexClock
{
	DateTimeOffset UtcNow { get; }
}

public sealed class SystemHexClock : IHexClock
{
	public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
