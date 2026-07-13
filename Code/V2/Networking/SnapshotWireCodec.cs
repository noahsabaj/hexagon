#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Networking;

/// <summary>
/// Versioned JSON-over-bytes transport for immutable presentation snapshots.
/// Mutable wire objects are private and every decode reconstructs the public
/// snapshot graph through its validating, defensive-copying constructors.
/// </summary>
public static class SnapshotWireCodec
{
	public const int CurrentWireVersion = 1;
	public const int MaximumPayloadBytes = 1024 * 1024;
	public const int MaximumJsonDepth = 32;

	private const int MaximumGraphNodes = 65_536;
	private const int MaximumArrayEntries = 8_192;
	private const int MaximumMapEntries = 512;
	private const int MaximumStringCharacters = 65_536;
	private const int MaximumCharacters = 1_024;
	private const int MaximumChatMessages = 4_096;
	private const int MaximumRosterRows = 1_024;
	private const int MaximumSchemaViews = 256;
	private const int MaximumSchemaRows = 4_096;
	private const int MaximumInventories = 128;
	private const int MaximumInventoryItems = 4_096;
	private const int MaximumItemActions = 256;

	private const string CharacterListKind = "character-list";
	private const string ClientStateKind = "client-state";
	private const string ChatKind = "chat";

	private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

	public static OperationResult<byte[]> EncodeCharacterList( CharacterListSnapshot snapshot ) =>
		Encode( CharacterListKind, snapshot, ToWire, FromWire );

	public static OperationResult<CharacterListSnapshot> DecodeCharacterList( byte[] payload ) =>
		Decode<CharacterListWire, CharacterListSnapshot>( payload, CharacterListKind, FromWire );

	public static OperationResult<byte[]> EncodeClientState( ClientStateSnapshot snapshot ) =>
		Encode( ClientStateKind, snapshot, ToWire, FromWire );

	public static OperationResult<ClientStateSnapshot> DecodeClientState( byte[] payload ) =>
		Decode<ClientStateWire, ClientStateSnapshot>( payload, ClientStateKind, FromWire );

	public static OperationResult<byte[]> EncodeChat( ChatSnapshot snapshot ) =>
		Encode( ChatKind, snapshot, ToWire, FromWire );

	public static OperationResult<ChatSnapshot> DecodeChat( byte[] payload ) =>
		Decode<ChatWire, ChatSnapshot>( payload, ChatKind, FromWire );

	private static OperationResult<byte[]> Encode<TSnapshot, TWire>(
		string kind,
		TSnapshot snapshot,
		Func<TSnapshot, TWire> convert,
		Func<TWire, TSnapshot> validate )
		where TSnapshot : class
		where TWire : class
	{
		if ( snapshot is null )
			return Failure<byte[]>( "A snapshot is required." );

		try
		{
			var wire = convert( snapshot );
			_ = validate( wire );
			var envelope = new WireEnvelope<TWire>
			{
				Version = CurrentWireVersion,
				Kind = kind,
				Payload = wire
			};
			var encoded = JsonSerializer.SerializeToUtf8Bytes( envelope, JsonOptions );
			if ( encoded.Length > MaximumPayloadBytes )
				return Failure<byte[]>( $"Snapshot wire payload exceeds {MaximumPayloadBytes} bytes." );
			// Keep encode/decode acceptance symmetric. This also rejects invalid
			// Unicode and graph limits before bytes can enter an RPC.
			ValidateJsonGraph( encoded );
			return OperationResult<byte[]>.Success( encoded );
		}
		catch ( Exception exception ) when ( IsCodecFailure( exception ) )
		{
			return Failure<byte[]>( "Snapshot cannot be represented by the wire format." );
		}
	}

	private static OperationResult<TSnapshot> Decode<TWire, TSnapshot>(
		byte[] payload,
		string expectedKind,
		Func<TWire, TSnapshot> convert )
		where TWire : class
		where TSnapshot : class
	{
		if ( payload is null || payload.Length == 0 )
			return Failure<TSnapshot>( "A non-empty snapshot wire payload is required." );
		if ( payload.Length > MaximumPayloadBytes )
			return Failure<TSnapshot>( $"Snapshot wire payload exceeds {MaximumPayloadBytes} bytes." );

		try
		{
			ValidateJsonGraph( payload );
			var envelope = JsonSerializer.Deserialize<WireEnvelope<TWire>>( payload, JsonOptions )
				?? throw new JsonException( "Snapshot wire envelope is null." );
			if ( envelope.Version != CurrentWireVersion )
				throw new JsonException( "Unsupported snapshot wire version." );
			if ( !string.Equals( envelope.Kind, expectedKind, StringComparison.Ordinal ) )
				throw new JsonException( "Snapshot wire kind does not match the requested contract." );
			if ( envelope.Payload is null )
				throw new JsonException( "Snapshot wire payload is null." );
			return OperationResult<TSnapshot>.Success( convert( envelope.Payload ) );
		}
		catch ( Exception exception ) when ( IsCodecFailure( exception ) )
		{
			return Failure<TSnapshot>( "Snapshot wire payload is malformed or violates its contract." );
		}
	}

	private static bool IsCodecFailure( Exception exception ) =>
		exception is JsonException or NotSupportedException or ArgumentException or InvalidOperationException;

	private static OperationResult<T> Failure<T>( string message ) =>
		OperationResult<T>.Failure( ErrorCode.InvalidArgument, message );

	private static JsonSerializerOptions CreateJsonOptions()
	{
		var options = new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			PropertyNameCaseInsensitive = false,
			NumberHandling = JsonNumberHandling.Strict,
			MaxDepth = MaximumJsonDepth,
			WriteIndented = false
		};
		options.Converters.Add( new SnapshotValueJsonConverter() );
		return options;
	}

	private static void ValidateJsonGraph( byte[] payload )
	{
		using var document = JsonDocument.Parse( payload, new JsonDocumentOptions
		{
			AllowTrailingCommas = false,
			CommentHandling = JsonCommentHandling.Disallow,
			MaxDepth = MaximumJsonDepth
		} );
		var nodes = 0;
		ValidateJsonElement( document.RootElement, ref nodes );
	}

	private static void ValidateJsonElement( JsonElement element, ref int nodes )
	{
		if ( nodes >= MaximumGraphNodes ) throw new JsonException( "Snapshot wire graph is too large." );
		nodes++;

		switch ( element.ValueKind )
		{
			case JsonValueKind.Object:
			{
				var names = new HashSet<string>( StringComparer.Ordinal );
				var properties = 0;
				foreach ( var property in element.EnumerateObject() )
				{
					properties++;
					if ( properties > MaximumMapEntries ) throw new JsonException( "Snapshot wire object is too large." );
					if ( !names.Add( property.Name ) ) throw new JsonException( "Snapshot wire object has duplicate properties." );
					RequireWireString( property.Name, "JSON property name" );
					ValidateJsonElement( property.Value, ref nodes );
				}
				break;
			}
			case JsonValueKind.Array:
			{
				var entries = 0;
				foreach ( var child in element.EnumerateArray() )
				{
					entries++;
					if ( entries > MaximumArrayEntries ) throw new JsonException( "Snapshot wire array is too large." );
					ValidateJsonElement( child, ref nodes );
				}
				break;
			}
			case JsonValueKind.String:
				RequireWireString( element.GetString(), "JSON string" );
				break;
		}
	}

	private static string RequireWireString( string? value, string name )
	{
		if ( value is null ) throw new JsonException( $"{name} cannot be null." );
		if ( value.Length > MaximumStringCharacters ) throw new JsonException( $"{name} is too long." );
		for ( var index = 0; index < value.Length; index++ )
		{
			var character = value[index];
			if ( char.IsHighSurrogate( character ) )
			{
				if ( index + 1 >= value.Length || !char.IsLowSurrogate( value[index + 1] ) )
					throw new JsonException( $"{name} contains invalid Unicode." );
				index++;
			}
			else if ( char.IsLowSurrogate( character ) )
			{
				throw new JsonException( $"{name} contains invalid Unicode." );
			}
		}
		return value;
	}

	private static List<T> RequireList<T>( List<T>? values, int maximum, string name ) where T : class
	{
		if ( values is null ) throw new JsonException( $"{name} is required." );
		if ( values.Count > maximum ) throw new JsonException( $"{name} has too many entries." );
		if ( values.Any( value => value is null ) ) throw new JsonException( $"{name} contains null entries." );
		return values;
	}

	private static Dictionary<string, SnapshotValue> RequireMap(
		Dictionary<string, SnapshotValue>? values,
		string name )
	{
		if ( values is null ) throw new JsonException( $"{name} is required." );
		if ( values.Count > MaximumMapEntries ) throw new JsonException( $"{name} has too many entries." );
		return values;
	}

	private static Dictionary<string, SnapshotValue> CopyMap( IReadOnlyDictionary<string, SnapshotValue> values ) =>
		values.Count > MaximumMapEntries
			? throw new JsonException( "Snapshot value map has too many entries." )
			: values.OrderBy( pair => pair.Key, StringComparer.Ordinal )
				.ToDictionary( pair => pair.Key, pair => pair.Value, StringComparer.Ordinal );

	private static void RequireCount( int count, int maximum, string name )
	{
		if ( count > maximum ) throw new JsonException( $"{name} has too many entries." );
	}

	private static CharacterListWire ToWire( CharacterListSnapshot snapshot )
	{
		RequireCount( snapshot.Characters.Count, MaximumCharacters, "characters" );
		return new CharacterListWire
		{
			Revision = snapshot.Revision,
			Characters = snapshot.Characters.Select( character => new CharacterWire
			{
				CharacterId = character.CharacterId.Value,
				Slot = character.Slot,
				Name = character.Name,
				Description = character.Description,
				Model = character.Model.Value,
				Faction = character.Faction.Value,
				Class = character.Class?.Value,
				LastPlayedAt = character.LastPlayedAt,
				IsBanned = character.IsBanned
			} ).ToList()
		};
	}

	private static CharacterListSnapshot FromWire( CharacterListWire wire )
	{
		var input = RequireList( wire.Characters, MaximumCharacters, "characters" );
		var characters = new List<CharacterSummarySnapshot>( input.Count );
		foreach ( var character in input )
		{
			characters.Add( new CharacterSummarySnapshot(
				new CharacterId( character.CharacterId ),
				character.Slot,
				RequireWireString( character.Name, "character name" ),
				RequireWireString( character.Description, "character description" ),
				new DefinitionId( RequireWireString( character.Model, "character model" ) ),
				new FactionId( RequireWireString( character.Faction, "character faction" ) ),
				character.Class is null ? null : new ClassId( RequireWireString( character.Class, "character class" ) ),
				character.LastPlayedAt,
				character.IsBanned ) );
		}
		return new CharacterListSnapshot( wire.Revision, characters );
	}

	private static ChatWire ToWire( ChatSnapshot snapshot )
	{
		RequireCount( snapshot.Messages.Count, MaximumChatMessages, "chat messages" );
		return new ChatWire
		{
			Connection = snapshot.Epoch.Connection.Value,
			Character = snapshot.Epoch.Character,
			Revision = snapshot.Revision,
			Messages = snapshot.Messages.Select( message => new ChatMessageWire
			{
				MessageId = message.MessageId,
				ChannelId = message.ChannelId,
				AuthorCharacterId = message.AuthorCharacterId?.Value,
				AuthorName = message.AuthorName,
				Text = message.Text,
				SentAtUtc = message.SentAtUtc
			} ).ToList()
		};
	}

	private static ChatSnapshot FromWire( ChatWire wire )
	{
		var input = RequireList( wire.Messages, MaximumChatMessages, "chat messages" );
		var messages = new List<ChatMessageSnapshot>( input.Count );
		foreach ( var message in input )
		{
			messages.Add( new ChatMessageSnapshot(
				message.MessageId,
				RequireWireString( message.ChannelId, "chat channel" ),
				message.AuthorCharacterId is Guid author ? new CharacterId( author ) : null,
				RequireWireString( message.AuthorName, "chat author" ),
				RequireWireString( message.Text, "chat text" ),
				message.SentAtUtc ) );
		}
		return new ChatSnapshot(
			new ChatDeliveryEpoch( new ConnectionEpoch( wire.Connection ), wire.Character ),
			wire.Revision,
			messages );
	}

	private static ClientStateWire ToWire( ClientStateSnapshot snapshot )
	{
		RequireCount( snapshot.Roster.Rows.Count, MaximumRosterRows, "roster rows" );
		RequireCount( snapshot.SchemaViews.Count, MaximumSchemaViews, "schema views" );
		RequireCount( snapshot.Inventories.Count, MaximumInventories, "inventories" );
		return new ClientStateWire
		{
			Epoch = new ClientStateEpochWire
			{
				Connection = snapshot.Epoch.Connection.Value,
				Character = snapshot.Epoch.Character,
				Revision = snapshot.Epoch.Revision
			},
			Player = ToWire( snapshot.Player ),
			PrivatePlayer = snapshot.PrivatePlayer is null ? null : ToWire( snapshot.PrivatePlayer ),
			Roster = new RosterWire
			{
				Revision = snapshot.Roster.Revision,
				Rows = snapshot.Roster.Rows.Select( row => new RosterRowWire
				{
					ConnectionId = row.ConnectionId.Value,
					CharacterId = row.CharacterId?.Value,
					Fields = CopyMap( row.Fields )
				} ).ToList()
			},
			SchemaViews = snapshot.SchemaViews.Values.Select( ToWire ).ToList(),
			Inventories = snapshot.Inventories.Select( ToWire ).ToList(),
			ActiveAction = snapshot.ActiveAction is null ? null : ToWire( snapshot.ActiveAction )
		};
	}

	private static PlayerWire ToWire( PlayerPublicSnapshot snapshot ) => new()
	{
		ConnectionId = snapshot.ConnectionId.Value,
		PlatformAccountId = snapshot.PlatformAccountId,
		PlatformDisplayName = snapshot.PlatformDisplayName,
		CharacterId = snapshot.CharacterId?.Value,
		CharacterName = snapshot.CharacterName,
		ReplicatedCharacterName = snapshot.ReplicatedCharacterName,
		Description = snapshot.Description,
		Model = snapshot.Model?.Value,
		Faction = snapshot.Faction?.Value,
		Class = snapshot.Class?.Value,
		IsDead = snapshot.IsDead,
		IsWeaponRaised = snapshot.IsWeaponRaised
	};

	private static PrivatePlayerWire ToWire( PlayerPrivateSnapshot snapshot )
	{
		RequireCount( snapshot.Permissions.Count, MaximumMapEntries, "permissions" );
		return new PrivatePlayerWire
		{
			CharacterId = snapshot.CharacterId.Value,
			Balance = snapshot.Balance,
			MainInventoryId = snapshot.MainInventoryId?.Value,
			Values = CopyMap( snapshot.Values ),
			Permissions = snapshot.Permissions.ToList()
		};
	}

	private static SchemaViewWire ToWire( SchemaViewSnapshot snapshot )
	{
		RequireCount( snapshot.Rows.Count, MaximumSchemaRows, "schema rows" );
		return new SchemaViewWire
		{
			PanelId = snapshot.PanelId,
			Revision = snapshot.Revision,
			Fields = CopyMap( snapshot.Fields ),
			Rows = snapshot.Rows.Select( CopyMap ).ToList()
		};
	}

	private static InventoryWire ToWire( InventorySnapshot snapshot )
	{
		RequireCount( snapshot.Items.Count, MaximumInventoryItems, "inventory items" );
		return new InventoryWire
		{
			InventoryId = snapshot.InventoryId.Value,
			Revision = snapshot.Revision,
			Kind = (int)snapshot.Kind,
			Title = snapshot.Title,
			Width = snapshot.Width,
			Height = snapshot.Height,
			Items = snapshot.Items.Select( item =>
			{
				RequireCount( item.Actions.Count, MaximumItemActions, "item actions" );
				return new InventoryItemWire
				{
					ItemId = item.ItemId.Value,
					DefinitionId = item.DefinitionId.Value,
					DisplayName = item.DisplayName,
					Description = item.Description,
					Category = item.Category,
					X = item.X,
					Y = item.Y,
					Width = item.Width,
					Height = item.Height,
					Quantity = item.Quantity,
					Actions = item.Actions.Select( action => new ItemActionWire
					{
						ActionId = action.ActionId.Value,
						Label = action.Label,
						Enabled = action.Enabled,
						DisabledReason = action.DisabledReason,
						Invocation = (int)action.Invocation
					} ).ToList(),
					State = CopyMap( item.State ),
					CanDrop = item.CanDrop,
					DropDisabledReason = item.DropDisabledReason
				};
			} ).ToList()
		};
	}

	private static ActionProgressWire ToWire( ActionProgressSnapshot snapshot ) => new()
	{
		InstanceId = snapshot.InstanceId,
		ActionId = snapshot.ActionId.Value,
		Label = snapshot.Label,
		StartedAtUtc = snapshot.StartedAtUtc,
		DurationTicks = snapshot.Duration.Ticks,
		CanCancel = snapshot.CanCancel
	};

	private static ClientStateSnapshot FromWire( ClientStateWire wire )
	{
		if ( wire.Epoch is null || wire.Player is null || wire.Roster is null )
			throw new JsonException( "Client state is missing required objects." );

		var epoch = new ClientStateEpoch(
			new ConnectionEpoch( wire.Epoch.Connection ),
			wire.Epoch.Character,
			wire.Epoch.Revision );
		var player = FromWire( wire.Player );
		var privatePlayer = wire.PrivatePlayer is null ? null : FromWire( wire.PrivatePlayer );
		var rosterRowsInput = RequireList( wire.Roster.Rows, MaximumRosterRows, "roster rows" );
		var rosterRows = new List<PlayerRosterRowSnapshot>( rosterRowsInput.Count );
		foreach ( var row in rosterRowsInput )
		{
			rosterRows.Add( new PlayerRosterRowSnapshot(
				new ConnectionId( row.ConnectionId ),
				row.CharacterId is Guid characterId ? new CharacterId( characterId ) : null,
				RequireMap( row.Fields, "roster fields" ) ) );
		}

		var viewInput = RequireList( wire.SchemaViews, MaximumSchemaViews, "schema views" );
		var views = new List<SchemaViewSnapshot>( viewInput.Count );
		foreach ( var view in viewInput ) views.Add( FromWire( view ) );

		var inventoryInput = RequireList( wire.Inventories, MaximumInventories, "inventories" );
		var inventories = new List<InventorySnapshot>( inventoryInput.Count );
		foreach ( var inventory in inventoryInput ) inventories.Add( FromWire( inventory ) );

		return new ClientStateSnapshot(
			epoch,
			player,
			privatePlayer,
			new PlayerRosterSnapshot( wire.Roster.Revision, rosterRows ),
			views,
			inventories,
			wire.ActiveAction is null ? null : FromWire( wire.ActiveAction ) );
	}

	private static PlayerPublicSnapshot FromWire( PlayerWire wire ) => new(
		new ConnectionId( wire.ConnectionId ),
		wire.PlatformAccountId,
		RequireWireString( wire.PlatformDisplayName, "platform display name" ),
		wire.CharacterId is Guid characterId ? new CharacterId( characterId ) : null,
		RequireWireString( wire.CharacterName, "character name" ),
		RequireWireString( wire.Description, "player description" ),
		wire.Model is null ? null : new DefinitionId( RequireWireString( wire.Model, "player model" ) ),
		wire.Faction is null ? null : new FactionId( RequireWireString( wire.Faction, "player faction" ) ),
		wire.Class is null ? null : new ClassId( RequireWireString( wire.Class, "player class" ) ),
		wire.IsDead,
		wire.IsWeaponRaised,
		RequireWireString( wire.ReplicatedCharacterName, "replicated character name" ) );

	private static PlayerPrivateSnapshot FromWire( PrivatePlayerWire wire )
	{
		if ( wire.Permissions is null ) throw new JsonException( "permissions are required." );
		if ( wire.Permissions.Count > MaximumMapEntries ) throw new JsonException( "permissions has too many entries." );
		if ( wire.Permissions.Any( permission => permission is null ) ) throw new JsonException( "permissions contains null entries." );
		foreach ( var permission in wire.Permissions ) RequireWireString( permission, "permission" );
		return new PlayerPrivateSnapshot(
			new CharacterId( wire.CharacterId ),
			wire.Balance,
			wire.MainInventoryId is Guid inventoryId ? new InventoryId( inventoryId ) : null,
			RequireMap( wire.Values, "private values" ),
			wire.Permissions );
	}

	private static SchemaViewSnapshot FromWire( SchemaViewWire wire )
	{
		var rowInput = RequireList( wire.Rows, MaximumSchemaRows, "schema rows" );
		var rows = new List<IReadOnlyDictionary<string, SnapshotValue>>( rowInput.Count );
		foreach ( var row in rowInput ) rows.Add( RequireMap( row, "schema row" ) );
		return new SchemaViewSnapshot(
			RequireWireString( wire.PanelId, "panel ID" ),
			wire.Revision,
			RequireMap( wire.Fields, "schema fields" ),
			rows );
	}

	private static InventorySnapshot FromWire( InventoryWire wire )
	{
		if ( !Enum.IsDefined( typeof(InventoryViewKind), wire.Kind ) )
			throw new JsonException( "Unknown inventory view kind." );
		var itemInput = RequireList( wire.Items, MaximumInventoryItems, "inventory items" );
		var items = new List<InventoryItemSnapshot>( itemInput.Count );
		foreach ( var item in itemInput ) items.Add( FromWire( item ) );
		return new InventorySnapshot(
			new InventoryId( wire.InventoryId ),
			wire.Revision,
			(InventoryViewKind)wire.Kind,
			RequireWireString( wire.Title, "inventory title" ),
			wire.Width,
			wire.Height,
			items );
	}

	private static InventoryItemSnapshot FromWire( InventoryItemWire wire )
	{
		var actionInput = RequireList( wire.Actions, MaximumItemActions, "item actions" );
		var actions = new List<ItemActionSnapshot>( actionInput.Count );
		foreach ( var action in actionInput )
		{
			if ( !Enum.IsDefined( typeof(ItemActionInvocationKind), action.Invocation ) )
				throw new JsonException( "Unknown item action invocation kind." );
			actions.Add( new ItemActionSnapshot(
				new ActionId( RequireWireString( action.ActionId, "action ID" ) ),
				RequireWireString( action.Label, "action label" ),
				action.Enabled,
				action.DisabledReason is null ? null : RequireWireString( action.DisabledReason, "disabled reason" ),
				(ItemActionInvocationKind)action.Invocation ) );
		}
		return new InventoryItemSnapshot(
			new ItemId( wire.ItemId ),
			new DefinitionId( RequireWireString( wire.DefinitionId, "item definition" ) ),
			RequireWireString( wire.DisplayName, "item display name" ),
			RequireWireString( wire.Description, "item description" ),
			RequireWireString( wire.Category, "item category" ),
			wire.X,
			wire.Y,
			wire.Width,
			wire.Height,
			wire.Quantity,
			actions,
			RequireMap( wire.State, "item state" ),
			wire.CanDrop,
			wire.DropDisabledReason is null ? null : RequireWireString( wire.DropDisabledReason, "drop disabled reason" ) );
	}

	private static ActionProgressSnapshot FromWire( ActionProgressWire wire ) => new(
		wire.InstanceId,
		new ActionId( RequireWireString( wire.ActionId, "active action ID" ) ),
		RequireWireString( wire.Label, "active action label" ),
		wire.StartedAtUtc,
		TimeSpan.FromTicks( wire.DurationTicks ),
		wire.CanCancel );

	private sealed class SnapshotValueJsonConverter : JsonConverter<SnapshotValue>
	{
		public override SnapshotValue Read( ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options )
		{
			using var document = JsonDocument.ParseValue( ref reader );
			var root = document.RootElement;
			if ( root.ValueKind != JsonValueKind.Object ) throw new JsonException( "Snapshot value must be an object." );
			JsonElement kind = default;
			JsonElement value = default;
			var hasKind = false;
			var hasValue = false;
			foreach ( var property in root.EnumerateObject() )
			{
				switch ( property.Name )
				{
					case "kind" when !hasKind:
						kind = property.Value;
						hasKind = true;
						break;
					case "value" when !hasValue:
						value = property.Value;
						hasValue = true;
						break;
					default:
						throw new JsonException( "Snapshot value has duplicate or unknown properties." );
				}
			}
			if ( !hasKind || !hasValue || kind.ValueKind != JsonValueKind.String )
				throw new JsonException( "Snapshot value requires kind and value." );

			return kind.GetString() switch
			{
				"string" when value.ValueKind == JsonValueKind.String =>
					SnapshotValue.String( RequireWireString( value.GetString(), "snapshot string" ) ),
				"choice" when value.ValueKind == JsonValueKind.String =>
					SnapshotValue.Choice( RequireWireString( value.GetString(), "snapshot choice" ) ),
				"integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64( out var integer ) =>
					SnapshotValue.Integer( integer ),
				"boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
					SnapshotValue.Boolean( value.GetBoolean() ),
				_ => throw new JsonException( "Snapshot value kind or scalar type is invalid." )
			};
		}

		public override void Write( Utf8JsonWriter writer, SnapshotValue value, JsonSerializerOptions options )
		{
			writer.WriteStartObject();
			switch ( value.Kind )
			{
				case SnapshotValueKind.String:
					writer.WriteString( "kind", "string" );
					writer.WriteString( "value", RequireWireString( value.StringValue, "snapshot string" ) );
					break;
				case SnapshotValueKind.Choice:
					writer.WriteString( "kind", "choice" );
					writer.WriteString( "value", RequireWireString( value.StringValue, "snapshot choice" ) );
					break;
				case SnapshotValueKind.Integer:
					writer.WriteString( "kind", "integer" );
					writer.WriteNumber( "value", value.IntegerValue );
					break;
				case SnapshotValueKind.Boolean:
					writer.WriteString( "kind", "boolean" );
					writer.WriteBoolean( "value", value.BooleanValue );
					break;
				default:
					throw new JsonException( "Unknown snapshot value kind." );
			}
			writer.WriteEndObject();
		}
	}

	private abstract class WireObject : IJsonOnDeserialized
	{
		[JsonExtensionData]
		public Dictionary<string, JsonElement>? ExtensionData { get; set; }

		void IJsonOnDeserialized.OnDeserialized()
		{
			if ( ExtensionData is { Count: > 0 } )
				throw new JsonException( "Snapshot wire object has unknown properties." );
		}
	}

	private sealed class WireEnvelope<T> : WireObject where T : class
	{
		[JsonRequired] public int Version { get; set; }
		[JsonRequired] public string Kind { get; set; } = null!;
		[JsonRequired] public T Payload { get; set; } = null!;
	}

	private sealed class CharacterListWire : WireObject
	{
		[JsonRequired] public long Revision { get; set; }
		[JsonRequired] public List<CharacterWire> Characters { get; set; } = null!;
	}

	private sealed class CharacterWire : WireObject
	{
		[JsonRequired] public Guid CharacterId { get; set; }
		[JsonRequired] public int Slot { get; set; }
		[JsonRequired] public string Name { get; set; } = null!;
		[JsonRequired] public string Description { get; set; } = null!;
		[JsonRequired] public string Model { get; set; } = null!;
		[JsonRequired] public string Faction { get; set; } = null!;
		[JsonRequired] public string? Class { get; set; }
		[JsonRequired] public DateTimeOffset LastPlayedAt { get; set; }
		[JsonRequired] public bool IsBanned { get; set; }
	}

	private sealed class ChatWire : WireObject
	{
		[JsonRequired] public Guid Connection { get; set; }
		[JsonRequired] public long Character { get; set; }
		[JsonRequired] public long Revision { get; set; }
		[JsonRequired] public List<ChatMessageWire> Messages { get; set; } = null!;
	}

	private sealed class ChatMessageWire : WireObject
	{
		[JsonRequired] public Guid MessageId { get; set; }
		[JsonRequired] public string ChannelId { get; set; } = null!;
		[JsonRequired] public Guid? AuthorCharacterId { get; set; }
		[JsonRequired] public string AuthorName { get; set; } = null!;
		[JsonRequired] public string Text { get; set; } = null!;
		[JsonRequired] public DateTimeOffset SentAtUtc { get; set; }
	}

	private sealed class ClientStateWire : WireObject
	{
		[JsonRequired] public ClientStateEpochWire Epoch { get; set; } = null!;
		[JsonRequired] public PlayerWire Player { get; set; } = null!;
		[JsonRequired] public PrivatePlayerWire? PrivatePlayer { get; set; }
		[JsonRequired] public RosterWire Roster { get; set; } = null!;
		[JsonRequired] public List<SchemaViewWire> SchemaViews { get; set; } = null!;
		[JsonRequired] public List<InventoryWire> Inventories { get; set; } = null!;
		[JsonRequired] public ActionProgressWire? ActiveAction { get; set; }
	}

	private sealed class ClientStateEpochWire : WireObject
	{
		[JsonRequired] public Guid Connection { get; set; }
		[JsonRequired] public long Character { get; set; }
		[JsonRequired] public long Revision { get; set; }
	}

	private sealed class PlayerWire : WireObject
	{
		[JsonRequired] public Guid ConnectionId { get; set; }
		[JsonRequired] public ulong PlatformAccountId { get; set; }
		[JsonRequired] public string PlatformDisplayName { get; set; } = null!;
		[JsonRequired] public Guid? CharacterId { get; set; }
		[JsonRequired] public string CharacterName { get; set; } = null!;
		[JsonRequired] public string ReplicatedCharacterName { get; set; } = null!;
		[JsonRequired] public string Description { get; set; } = null!;
		[JsonRequired] public string? Model { get; set; }
		[JsonRequired] public string? Faction { get; set; }
		[JsonRequired] public string? Class { get; set; }
		[JsonRequired] public bool IsDead { get; set; }
		[JsonRequired] public bool IsWeaponRaised { get; set; }
	}

	private sealed class PrivatePlayerWire : WireObject
	{
		[JsonRequired] public Guid CharacterId { get; set; }
		[JsonRequired] public long Balance { get; set; }
		[JsonRequired] public Guid? MainInventoryId { get; set; }
		[JsonRequired] public Dictionary<string, SnapshotValue> Values { get; set; } = null!;
		[JsonRequired] public List<string> Permissions { get; set; } = null!;
	}

	private sealed class RosterWire : WireObject
	{
		[JsonRequired] public long Revision { get; set; }
		[JsonRequired] public List<RosterRowWire> Rows { get; set; } = null!;
	}

	private sealed class RosterRowWire : WireObject
	{
		[JsonRequired] public Guid ConnectionId { get; set; }
		[JsonRequired] public Guid? CharacterId { get; set; }
		[JsonRequired] public Dictionary<string, SnapshotValue> Fields { get; set; } = null!;
	}

	private sealed class SchemaViewWire : WireObject
	{
		[JsonRequired] public string PanelId { get; set; } = null!;
		[JsonRequired] public long Revision { get; set; }
		[JsonRequired] public Dictionary<string, SnapshotValue> Fields { get; set; } = null!;
		[JsonRequired] public List<Dictionary<string, SnapshotValue>> Rows { get; set; } = null!;
	}

	private sealed class InventoryWire : WireObject
	{
		[JsonRequired] public Guid InventoryId { get; set; }
		[JsonRequired] public long Revision { get; set; }
		[JsonRequired] public int Kind { get; set; }
		[JsonRequired] public string Title { get; set; } = null!;
		[JsonRequired] public int Width { get; set; }
		[JsonRequired] public int Height { get; set; }
		[JsonRequired] public List<InventoryItemWire> Items { get; set; } = null!;
	}

	private sealed class InventoryItemWire : WireObject
	{
		[JsonRequired] public Guid ItemId { get; set; }
		[JsonRequired] public string DefinitionId { get; set; } = null!;
		[JsonRequired] public string DisplayName { get; set; } = null!;
		[JsonRequired] public string Description { get; set; } = null!;
		[JsonRequired] public string Category { get; set; } = null!;
		[JsonRequired] public int X { get; set; }
		[JsonRequired] public int Y { get; set; }
		[JsonRequired] public int Width { get; set; }
		[JsonRequired] public int Height { get; set; }
		[JsonRequired] public long Quantity { get; set; }
		[JsonRequired] public List<ItemActionWire> Actions { get; set; } = null!;
		[JsonRequired] public Dictionary<string, SnapshotValue> State { get; set; } = null!;
		[JsonRequired] public bool CanDrop { get; set; }
		[JsonRequired] public string? DropDisabledReason { get; set; }
	}

	private sealed class ItemActionWire : WireObject
	{
		[JsonRequired] public string ActionId { get; set; } = null!;
		[JsonRequired] public string Label { get; set; } = null!;
		[JsonRequired] public bool Enabled { get; set; }
		[JsonRequired] public string? DisabledReason { get; set; }
		[JsonRequired] public int Invocation { get; set; }
	}

	private sealed class ActionProgressWire : WireObject
	{
		[JsonRequired] public Guid InstanceId { get; set; }
		[JsonRequired] public string ActionId { get; set; } = null!;
		[JsonRequired] public string Label { get; set; } = null!;
		[JsonRequired] public DateTimeOffset StartedAtUtc { get; set; }
		[JsonRequired] public long DurationTicks { get; set; }
		[JsonRequired] public bool CanCancel { get; set; }
	}
}
