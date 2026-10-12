#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hexagon.Logic;

/// <summary>
/// A holder that is not a character: a crate, a corpse, a thing lying on the ground.
/// They are all this one document, so everything that works on one works on the rest.
/// </summary>
public sealed class HolderData : IHolder
{
	public Guid Id { get; set; }
	/// <summary>What it is, for the journal and for deciding what to rebuild after a restart.</summary>
	public string Kind { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public InventoryData Inventory { get; set; } = new();
	/// <summary>Where it lies, for holders that are not part of the authored scene.</summary>
	public float[]? Position { get; set; }
	/// <summary>Which way it lies, in degrees about the vertical: a map laid out faces whoever laid it out.</summary>
	public float Yaw { get; set; }
	/// <summary>For a body: what the character looked like. See the engine side's Looks.</summary>
	public string? Look { get; set; }
	/// <summary>For a body: whose it was, so a character staff restore gets up here with what is still on it.</summary>
	public Guid? Character { get; set; }
	/// <summary>For a body: the truck bed it lies in, by the bed's id, going wherever the truck goes until it is taken out.</summary>
	public Guid? LyingIn { get; set; }

	[JsonIgnore] public Guid HolderId => Id;
	[JsonIgnore] public string HolderLabel => $"{Kind}:{Id:N}";
}

public static class HolderKinds
{
	public const string Container = "container";
	public const string Ground = "ground";
	public const string Corpse = "corpse";
	/// <summary>What a machine is loaded with, and what it has made.</summary>
	public const string Machine = "machine";
	/// <summary>What a train's wagon carries while it stands at the station.</summary>
	public const string Wagon = "wagon";
	/// <summary>The back of a truck, which goes where the truck does.</summary>
	public const string Bed = "bed";
}

/// <summary>Every holder that is not a character, in memory and written through, like the roster.</summary>
public sealed class HolderStore
{
	private readonly DocumentStore _store;
	private readonly Dictionary<Guid, HolderData> _holders = new();

	public HolderStore( DocumentStore store )
	{
		_store = store ?? throw new ArgumentNullException( nameof(store) );
		foreach ( var holder in _store.LoadAll<HolderData>( "holders" ) ) _holders[holder.Id] = holder;
	}

	public int Count => _holders.Count;
	public HolderData? Find( Guid id ) => _holders.TryGetValue( id, out var holder ) ? holder : null;
	public IReadOnlyList<HolderData> OfKind( string kind ) => _holders.Values.Where( value => value.Kind == kind ).ToArray();

	/// <summary>The body a character left, if it is still lying somewhere.</summary>
	public HolderData? BodyOf( Guid characterId ) =>
		_holders.Values.FirstOrDefault( value => value.Kind == HolderKinds.Corpse && value.Character == characterId );

	/// <summary>The holder with this id, made empty the first time it is asked for. A scene object passes its own id.</summary>
	public HolderData GetOrCreate( Guid id, string kind, string title, int width, int height )
	{
		if ( Find( id ) is { } existing ) return existing;
		var holder = new HolderData { Id = id, Kind = kind, Title = title, Inventory = new InventoryData { Width = width, Height = height } };
		_holders[id] = holder;
		Save( holder );
		return holder;
	}

	/// <summary>Whether this holder is still kept, and was not deleted once emptied.</summary>
	public bool Contains( HolderData holder ) => _holders.TryGetValue( holder.Id, out var held ) && ReferenceEquals( held, holder );

	/// <summary>A deleted holder is not written back, so an emptied body cannot return after a restart.</summary>
	public bool Save( HolderData holder )
	{
		if ( !Contains( holder ) ) return false;
		_store.Save( PathOf( holder.Id ), holder );
		return true;
	}

	/// <summary>Refused while anything is inside: a holder never takes its contents out of the world with it.</summary>
	public Result Delete( Guid id )
	{
		if ( Find( id ) is not { } holder ) return Result.Fail( ErrorCode.NotFound, "There is no such holder." );
		if ( holder.Inventory.Items.Count > 0 ) return Result.Fail( ErrorCode.Conflict, "It is not empty." );
		_store.Delete( PathOf( id ) );
		_holders.Remove( id );
		return Result.Success();
	}

	/// <summary>Where a holder's document is kept, for saving it together with others.</summary>
	public static string PathOf( Guid id ) => $"holders/{id:N}.json";
}
