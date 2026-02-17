namespace Hexagon.Currency;

/// <summary>
/// Currency/money permission and change events.
/// </summary>
public interface IHexCurrencyEvent : ISceneEvent<IHexCurrencyEvent>
{
	bool CanMoneyChange( HexCharacter character, int oldAmount, int newAmount, string reason ) => true;
	void OnMoneyChanged( HexCharacter character, int oldAmount, int newAmount, string reason ) { }
}
