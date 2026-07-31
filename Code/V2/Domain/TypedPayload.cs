#nullable enable

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hexagon.V2.Domain;

/// <summary>
/// An explicitly registered and versioned schema payload. The concrete codec is
/// selected by <see cref="TypeId"/>; no abstract runtime type is deserialized.
/// </summary>
public sealed record TypedPayload
{
	public required PersistedTypeId TypeId { get; init; }
	public required int TypeVersion { get; init; }
	public required JsonElement Data { get; init; }

	public TypedPayload DeepCopy() => this with { Data = Data.Clone() };
}

public enum CreationValueKind
{
	String,
	Integer,
	Boolean,
	Choice
}

/// <summary>
/// Closed creation-field value union used on the wire and during server validation.
/// </summary>
public readonly record struct CreationValue
{
	public CreationValueKind Kind { get; }
	public string StringValue { get; }
	public long IntegerValue { get; }
	public bool BooleanValue { get; }

	[JsonConstructor]
	public CreationValue( CreationValueKind kind, string stringValue, long integerValue, bool booleanValue )
	{
		if ( !Enum.IsDefined( kind ) ) throw new ArgumentOutOfRangeException( nameof(kind) );
		Kind = kind;
		StringValue = stringValue ?? "";
		IntegerValue = integerValue;
		BooleanValue = booleanValue;
	}

	public static CreationValue String( string value ) => new( CreationValueKind.String, value ?? "", 0, false );
	public static CreationValue Integer( long value ) => new( CreationValueKind.Integer, "", value, false );
	public static CreationValue Boolean( bool value ) => new( CreationValueKind.Boolean, "", 0, value );
	public static CreationValue Choice( string value ) => new( CreationValueKind.Choice, StableIdentifier.Require( value, nameof(value) ), 0, false );
}
