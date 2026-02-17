namespace Hexagon.Storage;

/// <summary>
/// Storage container open/close permission and events.
/// </summary>
public interface IHexStorageEvent : ISceneEvent<IHexStorageEvent>
{
	bool CanOpenStorage( HexPlayerComponent player, StorageComponent storage ) => true;
	void OnStorageOpened( HexPlayerComponent player, StorageComponent storage ) { }
	void OnStorageClosed( HexPlayerComponent player, StorageComponent storage ) { }
}
