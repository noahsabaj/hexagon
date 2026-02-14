namespace Hexagon.Attributes;

/// <summary>
/// Manages attribute definitions and per-character attribute values + boosts.
///
/// Base values stored in character data as "hex_attr_{id}".
/// Boosts stored as "hex_boosts" JSON list.
/// </summary>
public static class AttributeManager
{
	private static readonly Dictionary<string, AttributeDefinition> _definitions = new();
	private const string BoostDataKey = "hex_boosts";

	internal static void Initialize()
	{
		Log.Info( $"Hexagon: AttributeManager initialized with {_definitions.Count} attribute(s)." );
	}

	/// <summary>
	/// Register an attribute definition (called by AttributeDefinition.PostLoad).
	/// </summary>
	public static void RegisterDefinition( AttributeDefinition definition )
	{
		_definitions[definition.UniqueId] = definition;
	}

	/// <summary>
	/// Get an attribute definition by ID.
	/// </summary>
	public static AttributeDefinition GetDefinition( string id )
	{
		return _definitions.GetValueOrDefault( id );
	}

	/// <summary>
	/// Get all registered attribute definitions.
	/// </summary>
	public static IReadOnlyDictionary<string, AttributeDefinition> GetAllDefinitions() => _definitions;

	/// <summary>
	/// Get the effective attribute value for a character (base + boosts, clamped).
	/// </summary>
	public static float GetAttribute( HexCharacter character, string attributeId )
	{
		var def = GetDefinition( attributeId );
		if ( def == null ) return 0f;

		var baseValue = character.GetData<float>( $"hex_attr_{attributeId}", def.StartValue );
		var boostSum = GetBoostSum( character, attributeId );

		return Math.Clamp( baseValue + boostSum, def.MinValue, def.MaxValue );
	}

	/// <summary>
	/// Get the base attribute value (without boosts).
	/// </summary>
	public static float GetBaseAttribute( HexCharacter character, string attributeId )
	{
		var def = GetDefinition( attributeId );
		if ( def == null ) return 0f;

		return character.GetData<float>( $"hex_attr_{attributeId}", def.StartValue );
	}

	/// <summary>
	/// Set the base attribute value. Fires IAttributeChangedListener.
	/// </summary>
	public static void SetAttribute( HexCharacter character, string attributeId, float value )
	{
		var def = GetDefinition( attributeId );
		if ( def == null ) return;

		var clamped = Math.Clamp( value, def.MinValue, def.MaxValue );
		var oldValue = GetAttribute( character, attributeId );

		character.SetData( $"hex_attr_{attributeId}", clamped );

		FireIfChanged( character, attributeId, oldValue );
	}

	/// <summary>
	/// Add to the base attribute value.
	/// </summary>
	public static void AddAttribute( HexCharacter character, string attributeId, float amount )
	{
		var current = GetBaseAttribute( character, attributeId );
		SetAttribute( character, attributeId, current + amount );
	}

	/// <summary>
	/// Add a boost to a character's attribute.
	/// </summary>
	public static void AddBoost( HexCharacter character, string attributeId, float amount,
		TimeSpan? duration = null, string boostId = null )
	{
		var maxBoosts = Config.HexConfig.Get<int>( "attributes.boostMax", 50 );
		var boosts = GetBoosts( character );

		// Clean expired
		boosts.RemoveAll( b => b.IsExpired );

		if ( boosts.Count >= maxBoosts )
			return;

		var oldValue = GetAttribute( character, attributeId );

		var boost = new AttributeBoost
		{
			Id = boostId ?? Guid.NewGuid().ToString( "N" ),
			AttributeId = attributeId,
			Amount = amount,
			ExpiresAt = duration.HasValue ? DateTime.UtcNow + duration.Value : null
		};

		boosts.Add( boost );
		SaveBoosts( character, boosts );

		FireIfChanged( character, attributeId, oldValue );
	}

	/// <summary>
	/// Remove a specific boost by ID.
	/// </summary>
	public static bool RemoveBoost( HexCharacter character, string boostId )
	{
		var boosts = GetBoosts( character );
		var boost = boosts.Find( b => b.Id == boostId );

		if ( boost == null )
			return false;

		var oldValue = GetAttribute( character, boost.AttributeId );
		boosts.Remove( boost );
		SaveBoosts( character, boosts );

		FireIfChanged( character, boost.AttributeId, oldValue );
		return true;
	}

	/// <summary>
	/// Remove all boosts for a specific attribute.
	/// </summary>
	public static void ClearBoosts( HexCharacter character, string attributeId )
	{
		var boosts = GetBoosts( character );
		var oldValue = GetAttribute( character, attributeId );

		boosts.RemoveAll( b => b.AttributeId == attributeId );
		SaveBoosts( character, boosts );

		FireIfChanged( character, attributeId, oldValue );
	}

	/// <summary>
	/// Get all active (non-expired) boosts for a character.
	/// </summary>
	public static List<AttributeBoost> GetBoosts( HexCharacter character )
	{
		var json = character.GetData<string>( BoostDataKey, "" );

		if ( string.IsNullOrEmpty( json ) )
			return new List<AttributeBoost>();

		try
		{
			return Json.Deserialize<List<AttributeBoost>>( json ) ?? new List<AttributeBoost>();
		}
		catch
		{
			return new List<AttributeBoost>();
		}
	}

	/// <summary>
	/// Initialize all registered attributes on a character with their start values.
	/// Call this when a character is first created.
	/// </summary>
	public static void InitializeCharacter( HexCharacter character )
	{
		foreach ( var def in _definitions.Values )
		{
			var key = $"hex_attr_{def.UniqueId}";
			var existing = character.GetData<float>( key, float.MinValue );

			// Only set if not already initialized
			if ( Math.Abs( existing - float.MinValue ) < 0.001f )
			{
				character.SetData( key, def.StartValue );
			}
		}
	}

	private static void FireIfChanged( HexCharacter character, string attributeId, float oldValue )
	{
		var newValue = GetAttribute( character, attributeId );
		if ( Math.Abs( oldValue - newValue ) > 0.001f )
		{
			HexEvents.Fire<IAttributeChangedListener>(
				x => x.OnAttributeChanged( character, attributeId, oldValue, newValue ) );
		}
	}

	private static float GetBoostSum( HexCharacter character, string attributeId )
	{
		var boosts = GetBoosts( character );
		var sum = 0f;

		foreach ( var boost in boosts )
		{
			if ( boost.AttributeId == attributeId && !boost.IsExpired )
				sum += boost.Amount;
		}

		return sum;
	}

	private static void SaveBoosts( HexCharacter character, List<AttributeBoost> boosts )
	{
		// Clean expired before saving
		boosts.RemoveAll( b => b.IsExpired );
		character.SetData( BoostDataKey, Json.Serialize( boosts ) );
	}
}

/// <summary>
/// Fired when a character's effective attribute value changes (base or boost change).
/// </summary>
public interface IAttributeChangedListener
{
	void OnAttributeChanged( HexCharacter character, string attributeId, float oldValue, float newValue );
}
