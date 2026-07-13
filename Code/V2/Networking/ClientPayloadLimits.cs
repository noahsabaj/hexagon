#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

/// <summary>Framework-wide, allocation-bounding validation for all client commands.</summary>
public static class ClientPayloadLimits
{
	public const int MaximumMapEntries = 32;
	public const int MaximumIdentifierCharacters = 96;
	public const int MaximumStringCharacters = 4096;
	public const int MaximumUtf8Bytes = 16 * 1024;

	public static bool IsAdmissionSafeIdentifier( string? value ) =>
		value is { Length: >= 1 and <= MaximumIdentifierCharacters } && HasValidUnicode( value );

	public static OperationResult Validate( ClientCommand? command )
	{
		if ( command is null ) return Failure( "command", "Command payload is null." );
		var budget = new PayloadBudget();
		return command switch
		{
			CreateCharacterCommand { Input: null } => Failure( "input", "Character creation payload is null." ),
			CreateCharacterCommand create => ValidateCreation( create.Input, budget ),
			RunItemActionCommand { Arguments: null } => Failure( "arguments", "Action arguments are null." ),
			RunItemActionCommand action => Combine(
				ValidateIdentifier( action.ActionId.Value, "actionId", budget ),
				() => ValidateMap( action.Arguments, "arguments", budget ) ),
			SendChatCommand chat => Combine(
				ValidateIdentifier( chat.ChannelId, "channelId", budget ),
				() => ValidateString( chat.Text, "text", budget ) ),
			BeginInteractionCommand { Target: null } => Failure( "target", "Interaction target is null." ),
			ContinueInteractionCommand { Target: null } => Failure( "target", "Interaction target is null." ),
			RunSchemaCommandCommand { Arguments: null } => Failure( "arguments", "Schema command arguments are null." ),
			RunSchemaCommandCommand schema => Combine(
				ValidateIdentifier( schema.CommandId, "commandId", budget ),
				() => ValidateMap( schema.Arguments, "arguments", budget ) ),
			_ => OperationResult.Success()
		};
	}

	private static OperationResult ValidateCreation( CharacterCreationInput input, PayloadBudget budget ) =>
		Combine(
			ValidateString( input.Name, "name", budget ),
			() => Combine(
				ValidateString( input.Description, "description", budget ),
				() => Combine(
					ValidateIdentifier( input.Model.Value, "model", budget ),
					() => Combine(
						ValidateIdentifier( input.Faction.Value, "faction", budget ),
						() => Combine(
							input.Class is null
								? OperationResult.Success()
								: ValidateIdentifier( input.Class.Value.Value, "class", budget ),
							() => ValidateMap( input.Fields, "fields", budget ) ) ) ) ) );

	private static OperationResult ValidateMap(
		IReadOnlyDictionary<string, SnapshotValue> values,
		string path,
		PayloadBudget budget )
	{
		if ( values.Count > MaximumMapEntries )
			return Failure( path, $"Map contains more than {MaximumMapEntries} entries." );
		foreach ( var pair in values )
		{
			var key = ValidateIdentifier( pair.Key, $"{path}.key", budget );
			if ( key.Failed ) return key;
			if ( pair.Value.Kind is SnapshotValueKind.String or SnapshotValueKind.Choice )
			{
				var value = ValidateString( pair.Value.StringValue, $"{path}.{pair.Key}", budget );
				if ( value.Failed ) return value;
			}
		}
		return OperationResult.Success();
	}

	private static OperationResult ValidateIdentifier( string value, string path, PayloadBudget budget )
	{
		if ( !IsAdmissionSafeIdentifier( value ) )
		{
			if ( value is { Length: >= 1 and <= MaximumIdentifierCharacters } )
				return Failure( path, "Identifier contains invalid Unicode." );
			return Failure( path, $"Identifier must contain 1-{MaximumIdentifierCharacters} characters." );
		}
		return ValidateEncoded( value, path, budget );
	}

	private static OperationResult ValidateString( string value, string path, PayloadBudget budget )
	{
		if ( value is null || value.Length > MaximumStringCharacters )
			return Failure( path, $"String exceeds {MaximumStringCharacters} characters." );
		return ValidateEncoded( value, path, budget );
	}

	private static OperationResult ValidateEncoded( string value, string path, PayloadBudget budget )
	{
		if ( !HasValidUnicode( value ) ) return Failure( path, "String contains invalid Unicode." );
		budget.Bytes += Encoding.UTF8.GetByteCount( value );
		return budget.Bytes <= MaximumUtf8Bytes
			? OperationResult.Success()
			: Failure( path, $"Command exceeds {MaximumUtf8Bytes} UTF-8 bytes." );
	}

	private static bool HasValidUnicode( string value )
	{
		for ( var i = 0; i < value.Length; i++ )
		{
			var current = value[i];
			if ( char.IsHighSurrogate( current ) )
			{
				if ( i + 1 >= value.Length || !char.IsLowSurrogate( value[++i] ) ) return false;
			}
			else if ( char.IsLowSurrogate( current ) ) return false;
		}
		return true;
	}

	private static OperationResult Combine( OperationResult first, Func<OperationResult> next ) =>
		first.Failed ? first : next();

	private static OperationResult Failure( string path, string message ) =>
		OperationResult.Failure(
			ErrorCode.InvalidArgument,
			$"Invalid client payload at '{path}': {message}",
			new Dictionary<string, string>( StringComparer.Ordinal ) { ["path"] = path } );

	private sealed class PayloadBudget
	{
		public int Bytes { get; set; }
	}
}
