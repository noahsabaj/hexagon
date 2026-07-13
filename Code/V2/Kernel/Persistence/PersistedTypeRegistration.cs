#nullable enable

using System;
using Hexagon.V2.Kernel.Definitions;

namespace Hexagon.V2.Kernel.Persistence;

/// <summary>
/// Schema metadata for a concrete persisted payload. Concrete codecs and
/// envelopes live in the persistence infrastructure layer.
/// </summary>
public sealed record PersistedTypeRegistration(
	string Id,
	Type ClrType,
	int Version = 1
) : IDefinition;
