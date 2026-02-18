namespace Hexagon.Persistence;

/// <summary>
/// In-memory index for a DatabaseManager collection field.
///
/// Speeds up DatabaseManager.Select() for frequently queried fields (e.g., SteamId on
/// characters, OwnerId on inventories). Without an index, Select() does a full collection
/// scan. With an index, it does a dictionary lookup.
///
/// Register via DatabaseManager.RegisterIndex("characters", "SteamId").
/// The index is built lazily on first use and maintained on Save() / Delete() calls.
/// </summary>
public sealed class CollectionIndex<T>
{
	private readonly string _collection;
	private readonly string _fieldName;
	private readonly Func<T, string> _keySelector;

	// Maps field value -> list of document keys that have that value
	private Dictionary<string, List<string>> _index;

	public CollectionIndex( string collection, string fieldName, Func<T, string> keySelector )
	{
		_collection = collection;
		_fieldName = fieldName;
		_keySelector = keySelector;
	}

	/// <summary>
	/// Whether the index has been built yet.
	/// </summary>
	public bool IsBuilt => _index != null;

	/// <summary>
	/// Build the index by scanning all documents in the collection.
	/// Called lazily on first use or explicitly via RegisterIndex.
	/// </summary>
	public void Build( IEnumerable<(string key, T document)> documents )
	{
		_index = new Dictionary<string, List<string>>( StringComparer.Ordinal );

		foreach ( var (key, doc) in documents )
		{
			var fieldValue = _keySelector( doc );
			if ( fieldValue == null ) continue;

			if ( !_index.TryGetValue( fieldValue, out var keys ) )
			{
				keys = new List<string>();
				_index[fieldValue] = keys;
			}

			keys.Add( key );
		}
	}

	/// <summary>
	/// Invalidate the index so it will be rebuilt on next use.
	/// Called when the collection changes (Save/Delete).
	/// </summary>
	public void Invalidate() => _index = null;

	/// <summary>
	/// Look up document keys by field value.
	/// Returns null if the index has not been built.
	/// </summary>
	public List<string> Lookup( string fieldValue )
	{
		if ( _index == null ) return null;
		return _index.TryGetValue( fieldValue, out var keys ) ? keys : new List<string>();
	}

	/// <summary>
	/// Update the index after a document is saved.
	/// </summary>
	public void OnSave( string docKey, T document )
	{
		if ( _index == null ) return;

		// Remove from any previous entry
		OnDelete( docKey );

		var fieldValue = _keySelector( document );
		if ( fieldValue == null ) return;

		if ( !_index.TryGetValue( fieldValue, out var keys ) )
		{
			keys = new List<string>();
			_index[fieldValue] = keys;
		}

		if ( !keys.Contains( docKey ) )
			keys.Add( docKey );
	}

	/// <summary>
	/// Update the index after a document is deleted.
	/// </summary>
	public void OnDelete( string docKey )
	{
		if ( _index == null ) return;

		foreach ( var keys in _index.Values )
			keys.Remove( docKey );
	}
}
