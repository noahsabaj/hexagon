#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Networking;

namespace Hexagon.V2.Tests.Networking;

[TestClass]
public sealed class ClientPayloadLimitsTests
{
	[TestMethod]
	public void NullReferencePayloadsFailClosedInsteadOfEscapingAdmissionCleanup()
	{
		Assert.AreEqual(
			ErrorCode.InvalidArgument,
			ClientPayloadLimits.Validate( new CreateCharacterCommand( null! ) ).Error!.Code );
		Assert.AreEqual(
			ErrorCode.InvalidArgument,
			ClientPayloadLimits.Validate( new BeginInteractionCommand( null! ) ).Error!.Code );
	}

	[TestMethod]
	public void MapEntryAndPerStringBoundariesAreEnforced()
	{
		var accepted = Enumerable.Range( 0, ClientPayloadLimits.MaximumMapEntries )
			.ToDictionary( index => $"k{index}", _ => SnapshotValue.String( "x" ), StringComparer.Ordinal );
		var rejected = new Dictionary<string, SnapshotValue>( accepted, StringComparer.Ordinal )
		{
			["overflow"] = SnapshotValue.String( "x" )
		};

		Assert.IsTrue( ClientPayloadLimits.Validate( new RunSchemaCommandCommand( "test", accepted ) ).Succeeded );
		Assert.AreEqual( ErrorCode.InvalidArgument,
			ClientPayloadLimits.Validate( new RunSchemaCommandCommand( "test", rejected ) ).Error!.Code );
		Assert.IsTrue( ClientPayloadLimits.Validate(
			new SendChatCommand( "ic", new string( 'a', ClientPayloadLimits.MaximumStringCharacters ) ) ).Succeeded );
		Assert.IsTrue( ClientPayloadLimits.Validate(
			new SendChatCommand( "ic", new string( 'a', ClientPayloadLimits.MaximumStringCharacters + 1 ) ) ).Failed );
	}

	[TestMethod]
	public void InvalidUnicodeAndAggregateUtf8OverflowAreRejected()
	{
		var invalidUnicode = new SendChatCommand( "ic", "\ud800" );
		var fields = new Dictionary<string, SnapshotValue>( StringComparer.Ordinal );
		for ( var index = 0; index < 5; index++ )
			fields[$"key{index}"] = SnapshotValue.String( new string( '\u0800', 4096 ) );

		Assert.IsTrue( ClientPayloadLimits.Validate( invalidUnicode ).Failed );
		Assert.IsTrue( ClientPayloadLimits.Validate(
			new RunSchemaCommandCommand( "expensive", fields ) ).Failed );
	}

	[TestMethod]
	public void CreationIdentifiersContributeToAggregateUtf8Budget()
	{
		var fields = Enumerable.Range( 0, 4 ).ToDictionary(
			index => $"field{index}",
			_ => SnapshotValue.String( new string( 'x', 4090 ) ),
			StringComparer.Ordinal );
		var input = new CharacterCreationInput(
			"name",
			"description",
			new DefinitionId( new string( 'm', 96 ) ),
			new FactionId( new string( 'f', 96 ) ),
			new ClassId( new string( 'c', 96 ) ),
			fields );

		Assert.IsTrue( ClientPayloadLimits.Validate( new CreateCharacterCommand( input ) ).Failed );
	}
}
