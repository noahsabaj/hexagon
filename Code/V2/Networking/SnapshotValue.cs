#nullable enable

using System;

namespace Hexagon.V2.Networking;

public enum SnapshotValueKind
{
	String = 0,
	Integer = 1,
	Boolean = 2,
	Choice = 3
}

/// <summary>
/// Closed client-facing value union. Network snapshots never expose arbitrary
/// objects, untyped payload values, or server-owned records.
/// </summary>
public readonly record struct SnapshotValue
{
	private SnapshotValue(SnapshotValueKind kind, string stringValue, long integerValue, bool booleanValue)
	{
		Kind = kind;
		StringValue = stringValue;
		IntegerValue = integerValue;
		BooleanValue = booleanValue;
	}

	public SnapshotValueKind Kind { get; }
	public string StringValue { get; }
	public long IntegerValue { get; }
	public bool BooleanValue { get; }

	public static SnapshotValue String(string value) =>
		new(SnapshotValueKind.String, value ?? string.Empty, 0, false);

	public static SnapshotValue Integer(long value) =>
		new(SnapshotValueKind.Integer, string.Empty, value, false);

	public static SnapshotValue Boolean(bool value) =>
		new(SnapshotValueKind.Boolean, string.Empty, 0, value);

	public static SnapshotValue Choice(string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		return new SnapshotValue(SnapshotValueKind.Choice, value, 0, false);
	}
}
