#nullable enable

using System;
using System.Linq;
using System.Text.Json;
using Hexagon.Logic;
using Sandbox;

namespace Hexagon;

/// <summary>A sheet as the one reading it sees it: what is on it, and who is holding it up for them, when it is not theirs.</summary>
public sealed record PaperSight( Guid ItemId, string Definition, PaperData Paper, string ShownBy );

// Papers, from the side of whoever holds them. What is on a sheet does not travel with the inventory:
// it is read a sheet at a time, by whoever carries it or has it held up for them. Writing takes
// something to write with, and stays; a stamp takes the stamp. Every line, blank and stamp is on
// record with whoever put it there, which the paper itself does not say.
public sealed partial class Player
{
	/// <summary>Owner-side: the sheet this character is reading, its own or one held up for it, or null.</summary>
	public PaperSight? Paper { get; private set; }

	// Host-side: who is holding a sheet up for this character to read, and which sheet.
	private Player? _shownBy;
	private Guid _shownItem;

	/// <summary>Takes out a sheet this character carries, to read it, and to write on it with what it has.</summary>
	[Rpc.Host]
	public void RequestRead( Guid itemId )
	{
		if ( !Authorize( nameof( RequestRead ), out var caller, out _ ) || HostItem( nameof( RequestRead ), itemId ) is not { } stack ) return;
		if ( ItemDefinition.Find( stack.Definition ) is not { IsPaper: true } )
		{
			HostInvalid( nameof( RequestRead ), "item", itemId.ToString( "N" ) );
			return;
		}
		if ( IsIncapable )
		{
			Chat.Tell( caller, "You cannot do that now." );
			return;
		}
		HostStopShown();
		HostSendPaper( this, stack );
	}

	/// <summary>Writes a line below whatever is on a sheet. Taken from a pile of blank ones, one sheet comes off it.</summary>
	[Rpc.Host]
	public void RequestWrite( Guid itemId, string text )
	{
		if ( !Authorize( nameof( RequestWrite ), out var caller, out var game ) || HostItem( nameof( RequestWrite ), itemId ) is not { } stack ) return;
		var definition = ItemDefinition.Find( stack.Definition );
		var line = Papers.Clean( text );
		HostMark( caller, game, stack, JournalKinds.PaperWrite, ("text", line.Length <= Papers.LineLength ? line : Clip( line )),
			definition is not { Lines: > 0 } ? Result.Fail( ErrorCode.Invalid, "There is nothing to write on." )
			: !HostCan( Capability.Write ) ? Result.Fail( ErrorCode.Denied, Papers.NothingToWriteWith ) : null,
			sheet => Papers.Write( sheet, definition!.Lines, line ) );
	}

	/// <summary>Fills in a blank on a printed form.</summary>
	[Rpc.Host]
	public void RequestFill( Guid itemId, string field, string text )
	{
		if ( !Authorize( nameof( RequestFill ), out var caller, out var game ) || HostItem( nameof( RequestFill ), itemId ) is not { } stack ) return;
		var definition = ItemDefinition.Find( stack.Definition );
		var value = Papers.Clean( text );
		HostMark( caller, game, stack, JournalKinds.PaperFill, ("text", value.Length <= Papers.FieldLength ? value : Clip( value )),
			definition is not { Fields.Count: > 0 } ? Result.Fail( ErrorCode.Invalid, "There is nothing to fill in on it." )
			: !HostCan( Capability.Write ) ? Result.Fail( ErrorCode.Denied, Papers.NothingToWriteWith ) : null,
			sheet => Papers.Fill( sheet, definition!.Fields, field, value ), ("field", Clip( field ?? string.Empty )) );
	}

	/// <summary>Stamps a sheet with a stamp this character carries, on top of whatever is written on it by then.</summary>
	[Rpc.Host]
	public void RequestStamp( Guid itemId, Guid stampId )
	{
		if ( !Authorize( nameof( RequestStamp ), out var caller, out var game ) || HostItem( nameof( RequestStamp ), itemId ) is not { } stack
			|| HostItem( nameof( RequestStamp ), stampId ) is not { } stamp ) return;
		var definition = ItemDefinition.Find( stack.Definition );
		HostMark( caller, game, stack, JournalKinds.PaperStamp, ("stamp", stamp.Definition),
			definition is not { IsPaper: true } ? Result.Fail( ErrorCode.Invalid, "There is nothing to stamp." )
			: ItemDefinition.Find( stamp.Definition ) is not { IsStamp: true } ? Result.Fail( ErrorCode.Invalid, "That is not a stamp." ) : null,
			sheet => Papers.Stamp( sheet, stamp.Definition ) );
	}

	/// <summary>
	/// Host: puts something on a sheet, one sheet of a pile at a time, and tells whoever is reading it.
	/// A refusal is on record like the mark itself.
	/// </summary>
	private void HostMark( Connection caller, HexagonServices game, ItemStack stack, string kind, (string Key, string Value) what, Result? refusal,
		Func<ItemStack, Result> mark, params (string Key, string Value)[] more )
	{
		var character = _character!;
		var sheet = stack;
		var outcome = IsIncapable ? Result.Fail( ErrorCode.Denied, "You cannot do that now." ) : refusal ?? Result.Success();
		// Written on, a pile of blank sheets gives up one, which is then a sheet of its own. The mark is
		// tried on a blank sheet first, so a pile gives up nothing for one that does not go on.
		if ( outcome.Ok && stack.Count > 1 ) outcome = mark( new ItemStack { Definition = stack.Definition, Count = 1, Max = stack.Max } );
		if ( outcome.Ok && stack.Count > 1 )
		{
			var taken = game.Transfers!.Split( character, stack.Id, 1, Actor.Of( character ) );
			if ( taken.Ok ) sheet = taken.Value;
			else outcome = Result.Fail( ErrorCode.Conflict, "There is no room to take a sheet off the pile." );
		}
		if ( outcome.Ok ) outcome = mark( sheet );
		HostRecord( kind, $"item:{sheet.Id:N}", outcome.Ok, new[] { ("definition", stack.Definition), what }.Concat( more ).Append( ("reason", outcome.Code.ToString()) ).ToArray() );
		if ( !outcome.Ok )
		{
			Chat.Tell( caller, outcome.Message );
			return;
		}
		game.Roster!.Save( character );
		SendPrivateState();
		HostSendPaper( this, sheet );
		foreach ( var reader in Scene.GetAllComponents<Player>().Where( value => value._shownBy == this && value._shownItem == sheet.Id ) )
			HostSendPaper( reader, sheet );
	}

	/// <summary>Holds up a sheet this character carries for someone within reach to read. It is not handed over: it stays in hand.</summary>
	[Rpc.Host]
	public void RequestShow( Player reader, Guid itemId )
	{
		if ( !Authorize( nameof( RequestShow ), out var caller, out _ ) || HostItem( nameof( RequestShow ), itemId ) is not { } stack ) return;
		if ( ItemDefinition.Find( stack.Definition ) is not { IsPaper: true } definition )
		{
			HostInvalid( nameof( RequestShow ), "item", itemId.ToString( "N" ) );
			return;
		}
		var there = reader.IsValid() && reader != this && reader._character is not null;
		var refusal = Papers.ToShow( IsIncapable, there, there ? reader.HostPosition.Distance( HostPosition ) : 0f, PersonReach, there && reader.IsDown );
		HostRecord( JournalKinds.PaperShow, there ? reader._character!.HolderLabel : null, refusal is null,
			refusal is null ? new[] { ("item", itemId.ToString( "N" )), ("definition", stack.Definition) } : new[] { ("item", itemId.ToString( "N" )), ("reason", refusal) } );
		if ( refusal is not null )
		{
			Chat.Tell( caller, refusal );
			return;
		}
		reader.HostStopShown();
		reader._shownBy = this;
		reader._shownItem = stack.Id;
		HostSendPaper( reader, stack );
		Chat.Tell( caller, $"You hold up the {definition.InSentence} for them to read." );
		if ( reader.Network.Owner is { } told ) Chat.Tell( told, $"{Recognition.Label( reader._character!, _character! )} holds up the {definition.InSentence} for you to read." );
	}

	/// <summary>Owner: puts the sheet away, or looks away from one held up.</summary>
	public void ClosePaper()
	{
		Paper = null;
		PrivateVersion++;
		RequestClosePaper();
	}

	[Rpc.Host]
	private void RequestClosePaper()
	{
		if ( Authorize( nameof( RequestClosePaper ), out _, out _, needsCharacter: false ) ) HostStopShown();
	}

	/// <summary>Host: a sheet held up stays readable while it is held up: while its holder still has it, is able, and is close.</summary>
	private void HostPaperTick()
	{
		if ( _shownBy is not { } holder ) return;
		var still = holder.IsValid() && holder._character is { } shower && !holder.IsIncapable && !IsDown
			&& shower.Inventory.Items.Any( value => value.Id == _shownItem ) && holder.HostPosition.Distance( HostPosition ) <= PersonReach;
		if ( !still ) HostStopShown( close: true );
	}

	/// <summary>Host: nobody is holding a sheet up for this character any more. Closed, the reader stops seeing it.</summary>
	private void HostStopShown( bool close = false )
	{
		if ( _shownBy is null ) return;
		_shownBy = null;
		_shownItem = Guid.Empty;
		if ( !close || Network.Owner is not { } owner ) return;
		using ( Rpc.FilterInclude( owner ) ) ReceivePaper( string.Empty );
	}

	/// <summary>Host: shows a reader what is on a sheet, theirs, or held up for them by whoever this character is.</summary>
	private void HostSendPaper( Player reader, ItemStack sheet )
	{
		if ( reader.Network.Owner is not { } owner || reader._character is not { } seeing || _character is not { } holding ) return;
		var shownBy = reader == this ? string.Empty : Recognition.Label( seeing, holding );
		var sight = new PaperSight( sheet.Id, sheet.Definition, sheet.Paper ?? new PaperData(), shownBy );
		using ( Rpc.FilterInclude( owner ) ) reader.ReceivePaper( JsonSerializer.Serialize( sight ) );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ReceivePaper( string json )
	{
		Paper = json.Length == 0 ? null : JsonSerializer.Deserialize<PaperSight>( json );
		PrivateVersion++;
	}

	/// <summary>
	/// Host: the papers a new character arrives with, as it arrives in its clothes: the game's form, its
	/// bearer's blank filled in with the character's name, and the issuing office's stamp on it.
	/// </summary>
	internal static void HostIssueArrivalPapers( HexagonServices game, CharacterData character )
	{
		if ( game.ArrivalPapers is not { IsPaper: true } form ) return;
		var issued = form.IssueTo( game.Transfers!, Sources.Arrival, character, Actor.Of( character ) );
		if ( !issued.Ok )
		{
			Log.Warning( $"[create] {character.HolderLabel} arrives without the {form.Title}: {issued.Message}" );
			return;
		}
		if ( form.BearerField.Length > 0 && Papers.Fill( issued.Value, form.Fields, form.BearerField, character.Name ) is { Ok: false } unfilled )
			Log.Warning( $"[create] {form.Title} has no blank for its bearer: {unfilled.Message}" );
		if ( game.ArrivalStamp is { IsStamp: true } stamp ) Papers.Stamp( issued.Value, stamp.ResourcePath );
	}
}
