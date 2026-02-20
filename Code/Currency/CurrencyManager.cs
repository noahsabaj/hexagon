namespace Hexagon.Currency;

/// <summary>
/// Manages character currency. Uses the "Money" CharVar on HexCharacterData for storage,
/// which triggers existing dirty tracking and networking automatically.
/// </summary>
public static class CurrencyManager
{
	public static event Func<Characters.HexCharacter, int, int, string, bool> OnCanMoneyChange;

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
		TryChangeMoney( character, current, current + amount, reason );
	}

	/// <summary>
	/// Take money from a character. Returns false if they can't afford it.
	/// </summary>
	public static bool TakeMoney( HexCharacter character, int amount, string reason = null )
	{
		if ( amount <= 0 ) return false;

		var current = GetMoney( character );
		if ( current < amount ) return false;

		return TryChangeMoney( character, current, current - amount, reason );
	}

	/// <summary>
	/// Set a character's money to an exact amount.
	/// </summary>
	public static void SetMoney( HexCharacter character, int amount, string reason = null )
	{
		var current = GetMoney( character );
		if ( current == amount ) return;

		TryChangeMoney( character, current, amount, reason );
	}

	private static bool TryChangeMoney( HexCharacter character, int current, int newAmount, string reason )
	{
		if ( OnCanMoneyChange != null )
		{
			foreach ( Func<Characters.HexCharacter, int, int, string, bool> handler in OnCanMoneyChange.GetInvocationList() )
			{
				if ( !handler( character, current, newAmount, reason ) ) return false;
			}
		}

		character.SetVar( "Money", newAmount );
		IHexCurrencyEvent.Post(
			x => x.OnMoneyChanged( character, current, newAmount, reason ) );
		return true;
	}

	/// <summary>
	/// Check if a character can afford a given amount.
	/// </summary>
	public static bool CanAfford( HexCharacter character, int amount )
	{
		return GetMoney( character ) >= amount;
	}
}
