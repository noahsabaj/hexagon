namespace Hexagon.Persistence;

/// <summary>
/// Periodically auto-saves all dirty characters and inventories.
/// Replaces the manual tick that was previously in CharacterManager.Update().
///
/// The save interval is read from config: "framework.saveInterval" (default 300 seconds).
/// </summary>
public sealed class AutoSaveSystem : GameObjectSystem<AutoSaveSystem>
{
	private TimeUntil _nextAutoSave;

	public AutoSaveSystem( Scene scene ) : base( scene )
	{
		_nextAutoSave = Config.HexConfig.Get<float>( "framework.saveInterval", 300f );
		Listen( Stage.StartUpdate, 0, OnTick, "AutoSave tick" );
	}

	private void OnTick()
	{
		if ( !Core.HexagonSystem.IsInitialized )
			return;

		if ( _nextAutoSave <= 0 )
		{
			Characters.CharacterManager.SaveAll();
			Inventory.InventoryManager.SaveAll();

			_nextAutoSave = Config.HexConfig.Get<float>( "framework.saveInterval", 300f );
		}
	}
}
