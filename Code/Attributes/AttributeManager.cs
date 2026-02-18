namespace Hexagon.Attributes;

/// <summary>
/// Manages attribute definitions and per-character attribute values + boosts.
///
/// Base values stored in HexCharacterData.AttributeValues keyed by attribute ID.
/// Boosts stored in HexCharacterData.Boosts as a typed list (no JSON double-serialization).
/// </summary>
public sealed class AttributeManager : GameObjectSystem<AttributeManager>
{
	private static readonly Dictionary<string, AttributeDefinition> _definitions = new();

	public AttributeManager( Scene scene ) : base( scene ) { }

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
	/// Logs a warning if the attribute ID is not registered (helps catch typos).
	/// </summary>
	public static float GetAttribute( HexCharacter character, string attributeId )
	{
		var def = GetDefinition( attributeId );
		if ( def == null )
		{
			Log.Warning( $"Hexagon: AttributeManager.GetAttribute — attribute '{attributeId}' is not registered." );
			return 0f;
		}

		var baseValue = character.Data.AttributeValues.GetValueOrDefault( attributeId, def.StartValue );
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

		return character.Data.AttributeValues.GetValueOrDefault( attributeId, def.StartValue );
	}

	/// <summary>
	/// Set the base attribute value. Fires IAttributeChangedListener.
	/// Logs a warning if the attribute ID is not registered (helps catch typos).
	/// </summary>
	public static void SetAttribute( HexCharacter character, string attributeId, float value )
	{
		var def = GetDefinition( attributeId );
		if ( def == null )
		{
			Log.Warning( $"Hexagon: AttributeManager.SetAttribute — attribute '{attributeId}' is not registered." );
			return;
		}

		var clamped = Math.Clamp( value, def.MinValue, def.MaxValue );
		var oldValue = GetAttribute( character, attributeId );

		character.Data.AttributeValues[attributeId] = clamped;
		character.MarkDirty( nameof( character.Data.AttributeValues ) );

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
		character.MarkDirty( nameof( character.Data.Boosts ) );

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
		character.MarkDirty( nameof( character.Data.Boosts ) );

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
		character.MarkDirty( nameof( character.Data.Boosts ) );

		FireIfChanged( character, attributeId, oldValue );
	}

	/// <summary>
	/// Get the boosts list for a character. The returned list is the live reference —
	/// mutating it does not automatically mark dirty; call MarkDirty("Boosts") after.
	/// </summary>
	public static List<AttributeBoost> GetBoosts( HexCharacter character )
	{
		return character.Data.Boosts ??= new List<AttributeBoost>();
	}

	/// <summary>
	/// Initialize all registered attributes on a character with their start values.
	/// Call this when a character is first created.
	/// </summary>
	public static void InitializeCharacter( HexCharacter character )
	{
		foreach ( var def in _definitions.Values )
		{
			if ( !character.Data.AttributeValues.ContainsKey( def.UniqueId ) )
			{
				character.Data.AttributeValues[def.UniqueId] = def.StartValue;
			}
		}
		character.MarkDirty( nameof( character.Data.AttributeValues ) );
	}

	private static void FireIfChanged( HexCharacter character, string attributeId, float oldValue )
	{
		var newValue = GetAttribute( character, attributeId );
		if ( Math.Abs( oldValue - newValue ) > 0.001f )
		{
			IHexAttributeEvent.Post(
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

}
