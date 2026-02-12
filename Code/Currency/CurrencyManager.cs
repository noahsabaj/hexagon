namespace Hexagon.Currency;

/// <summary>
/// Manages character currency. Uses the "Money" CharVar on HexCharacterData for storage,
/// which triggers existing dirty tracking and networking automatically.
/// </summary>
public static class CurrencyManager
{
	internal static void Initialize()
	{
		Log.Info( "Hexagon: CurrencyManager initialized." );
	}

	/// <summary>
	/// Format a money amount with the configured currency symbol (e.g. "$500").
	/// </summary>
	public static string Format( int amount )
	{
		var symbol = Config.HexConfig.Get<string>( "currency.symbol", "$" );
		return $"{symbol}{amount}";
	}

	/// <summary>
	/// Get a character's current money.
	/// </summary>
	public static int GetMoney( HexCharacter character )
	{
		return character.GetVar<int>( "Money" );
	}

	/// <summary>
	/// Give money to a character. Amount must be positive.
	/// </summary>
	public static void GiveMoney( HexCharacter character, int amount, string reason = null )
	{
		if ( amount <= 0 ) return;

		var current = GetMoney( character );
		var newAmount = current + amount;

		if ( !HexEvents.CanAll<ICanMoneyChangeListener>(
			x => x.CanMoneyChange( character, current, newAmount, reason ) ) )
			return;

		character.SetVar( "Money", newAmount );
		HexEvents.Fire<IMoneyChangedListener>(
			x => x.OnMoneyChanged( character, current, newAmount, reason ) );
	}

	/// <summary>
	/// Take money from a character. Returns false if they can't afford it.
	/// </summary>
	public static bool TakeMoney( HexCharacter character, int amount, string reason = null )
	{
		if ( amount <= 0 ) return false;

		var current = GetMoney( character );
		if ( current < amount ) return false;

		var newAmount = current - amount;

		if ( !HexEvents.CanAll<ICanMoneyChangeListener>(
			x => x.CanMoneyChange( character, current, newAmount, reason ) ) )
			return false;

		character.SetVar( "Money", newAmount );
		HexEvents.Fire<IMoneyChangedListener>(
			x => x.OnMoneyChanged( character, current, newAmount, reason ) );
		return true;
	}

	/// <summary>
	/// Set a character's money to an exact amount.
	/// </summary>
	public static void SetMoney( HexCharacter character, int amount, string reason = null )
	{
		var current = GetMoney( character );
		if ( current == amount ) return;

		if ( !HexEvents.CanAll<ICanMoneyChangeListener>(
			x => x.CanMoneyChange( character, current, amount, reason ) ) )
			return;

		character.SetVar( "Money", amount );
		HexEvents.Fire<IMoneyChangedListener>(
			x => x.OnMoneyChanged( character, current, amount, reason ) );
	}

	/// <summary>
	/// Check if a character can afford a given amount.
	/// </summary>
	public static bool CanAfford( HexCharacter character, int amount )
	{
		return GetMoney( character ) >= amount;
	}
}

/// <summary>
/// Permission hook: can a money change occur? Return false to block.
/// </summary>
public interface ICanMoneyChangeListener
{
	bool CanMoneyChange( HexCharacter character, int oldAmount, int newAmount, string reason );
}

/// <summary>
/// Fired after a money change has occurred.
/// </summary>
public interface IMoneyChangedListener
{
	void OnMoneyChanged( HexCharacter character, int oldAmount, int newAmount, string reason );
}
