#nullable enable

using System;
using System.Text.Json.Serialization;

namespace Hexagon.V2.Domain;

/// <summary>
/// Validation shared by every stable, user-authored identifier in Hexagon v2.
/// Stable identifiers are lowercase ASCII and may contain digits, '.', '-', and '_'.
/// </summary>
public static class StableIdentifier
{
	public const int MaxLength = 96;

	public static string Require( string value, string parameterName )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			throw new ArgumentException( "Identifier cannot be empty.", parameterName );

		value = value.Trim();
		if ( value.Length > MaxLength )
			throw new ArgumentOutOfRangeException( parameterName, $"Identifier cannot exceed {MaxLength} characters." );

		for ( var i = 0; i < value.Length; i++ )
		{
			var c = value[i];
			var valid = c is >= 'a' and <= 'z'
				or >= '0' and <= '9'
				or '.' or '-' or '_';

			if ( !valid )
				throw new ArgumentException( $"Identifier '{value}' contains invalid character '{c}'.", parameterName );
		}

		return value;
	}
}

public readonly record struct AccountId
{
	public ulong Value { get; }

	[JsonConstructor]
	public AccountId( ulong value )
	{
		if ( value == 0 ) throw new ArgumentOutOfRangeException( nameof(value), "Account ID cannot be zero." );
		Value = value;
	}

	public override string ToString() => Value.ToString();
}

public readonly record struct CharacterId
{
	public Guid Value { get; }
	[JsonConstructor]
	public CharacterId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static CharacterId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct InventoryId
{
	public Guid Value { get; }
	[JsonConstructor]
	public InventoryId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static InventoryId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct ItemId
{
	public Guid Value { get; }
	[JsonConstructor]
	public ItemId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static ItemId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct SceneEntityId
{
	public Guid Value { get; }
	[JsonConstructor]
	public SceneEntityId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static SceneEntityId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct InteractionSessionId
{
	public Guid Value { get; }
	[JsonConstructor]
	public InteractionSessionId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static InteractionSessionId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct ConnectionId
{
	public Guid Value { get; }
	[JsonConstructor]
	public ConnectionId( Guid value ) => Value = RequireGuid( value, nameof(value) );
	public static ConnectionId New() => new( Guid.NewGuid() );
	public override string ToString() => Value.ToString( "N" );
	private static Guid RequireGuid( Guid value, string name ) => value == Guid.Empty ? throw new ArgumentOutOfRangeException( name ) : value;
}

public readonly record struct FactionId
{
	public string Value { get; }
	[JsonConstructor]
	public FactionId( string value ) => Value = StableIdentifier.Require( value, nameof(value) );
	public override string ToString() => Value;
}

public readonly record struct ClassId
{
	public string Value { get; }
	[JsonConstructor]
	public ClassId( string value ) => Value = StableIdentifier.Require( value, nameof(value) );
	public override string ToString() => Value;
}

public readonly record struct DefinitionId
{
	public string Value { get; }
	[JsonConstructor]
	public DefinitionId( string value ) => Value = StableIdentifier.Require( value, nameof(value) );
	public override string ToString() => Value;
}

public readonly record struct ActionId
{
	public string Value { get; }
	[JsonConstructor]
	public ActionId( string value ) => Value = StableIdentifier.Require( value, nameof(value) );
	public override string ToString() => Value;
}

public readonly record struct PersistedTypeId
{
	public string Value { get; }
	[JsonConstructor]
	public PersistedTypeId( string value ) => Value = StableIdentifier.Require( value, nameof(value) );
	public override string ToString() => Value;
}
