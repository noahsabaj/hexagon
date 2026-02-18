namespace Hexagon.Items;

/// <summary>
/// Manages networked world item GameObjects — items that have been dropped from inventories
/// and exist as visible, interactable objects in the scene.
///
/// Spawning creates a networked GameObject visible to all connected clients.
/// Despawning destroys it for everyone.
/// </summary>
public sealed class WorldItemManager : GameObjectSystem<WorldItemManager>
{
	private static WorldItemManager _instance;
	private static WorldItemManager Instance => _instance;

	// Tracks spawned world items by ItemInstance ID for fast lookup/cleanup
	private readonly Dictionary<string, GameObject> _worldItems = new();

	public WorldItemManager( Scene scene ) : base( scene )
	{
		_instance = this;
	}

	/// <summary>
	/// Spawn a dropped item as a networked world GameObject.
	/// Must be called server-side. All clients will automatically see the result.
	/// </summary>
	/// <param name="item">The item instance being dropped.</param>
	/// <param name="position">World position to spawn at.</param>
	/// <param name="velocity">Initial velocity (e.g. forward throw from player).</param>
	/// <returns>The spawned GameObject, or null if the item has no WorldModel.</returns>
	public static GameObject SpawnWorldItem( ItemInstance item, Vector3 position, Vector3 velocity = default )
	{
		if ( item?.Definition == null ) return null;
		if ( Instance == null ) return null;

		var go = new GameObject( true, $"WorldItem_{item.Definition.DisplayName}" );
		go.WorldPosition = position;

		// World item component — carries synced item identity
		var worldItem = go.AddComponent<WorldItemComponent>();
		worldItem.ItemInstanceId = item.Id;
		worldItem.DefinitionId = item.DefinitionId;
		worldItem.DisplayName = item.Definition.DisplayName ?? item.DefinitionId;

		// Model renderer — shows the item in the world
		if ( item.Definition.WorldModel != null )
		{
			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = item.Definition.WorldModel;
		}

		// Physics — let it fall and tumble naturally
		var body = go.AddComponent<Rigidbody>();
		if ( velocity != default )
			body.Velocity = velocity;

		// Network spawn so all clients see it
		go.NetworkSpawn();

		Instance._worldItems[item.Id] = go;

		Log.Info( $"Hexagon: Spawned world item '{item.Definition.DisplayName}' at {position}" );

		return go;
	}

	/// <summary>
	/// Destroy a world item GameObject by ItemInstance ID.
	/// Must be called server-side. Destruction replicates to all clients.
	/// </summary>
	public static void DespawnWorldItem( string itemInstanceId )
	{
		if ( Instance == null ) return;

		if ( Instance._worldItems.TryGetValue( itemInstanceId, out var go ) )
		{
			Instance._worldItems.Remove( itemInstanceId );

			if ( go.IsValid() )
				go.Destroy();
		}
	}

	public override void Dispose()
	{
		foreach ( var go in _worldItems.Values )
		{
			if ( go.IsValid() )
				go.Destroy();
		}

		_worldItems.Clear();

		if ( _instance == this ) _instance = null;
		base.Dispose();
	}
}
