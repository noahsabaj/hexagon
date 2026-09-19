#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hexagon.Logic;

/// <summary>
/// A holder that is not a character: a crate, a vendor, a corpse, a thing lying on the ground.
/// They are all this one document, so everything that works on one works on the rest.
/// </summary>
public sealed class HolderData : IHolder
{
	public Guid Id { get; set; }
	/// <summary>What it is, for the journal and for deciding what to rebuild after a restart.</summary>
	public string Kind { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public InventoryData Inventory { get; set; } = new();
	public long Tokens { get; set; }
	/// <summary>Where it lies, for holders that are not part of the authored scene.</summary>
	public float[]? Position { get; set; }

	[JsonIgnore] public Guid HolderId => Id;
	[JsonIgnore] public string HolderLabel => $"{Kind}:{Id:N}";
}

public static class HolderKinds
{
	public const string Container = "container";
	public const string Ground = "ground";
	public const string Corpse = "corpse";
	public const string Vendor = "vendor";
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

	/// <summary>The holder with this id, made empty the first time it is asked for. A scene object passes its own id.</summary>
	public HolderData GetOrCreate( Guid id, string kind, string title, int width, int height )
	{
		if ( Find( id ) is { } existing ) return existing;
		var holder = new HolderData { Id = id, Kind = kind, Title = title, Inventory = new InventoryData { Width = width, Height = height } };
		_holders[id] = holder;
		Save( holder );
		return holder;
	}

	public void Save( HolderData holder ) => _store.Save( Path( holder.Id ), holder );

	/// <summary>Refused while anything is inside: a holder never takes its contents out of the world with it.</summary>
	public Result Delete( Guid id )
	{
		if ( Find( id ) is not { } holder ) return Result.Fail( ErrorCode.NotFound, "There is no such holder." );
		if ( holder.Inventory.Items.Count > 0 || holder.Tokens > 0 ) return Result.Fail( ErrorCode.Conflict, "It is not empty." );
		_store.Delete( Path( id ) );
		_holders.Remove( id );
		return Result.Success();
	}

	private static string Path( Guid id ) => $"holders/{id:N}.json";
}
