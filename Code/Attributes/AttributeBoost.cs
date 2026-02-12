namespace Hexagon.Attributes;

/// <summary>
/// A temporary or permanent modifier to a character's attribute.
/// Boosts stack additively and are evaluated at runtime.
/// </summary>
public class AttributeBoost
{
	/// <summary>
	/// Unique identifier for this boost (for removal).
	/// </summary>
	public string Id { get; set; }

	/// <summary>
	/// Which attribute this boost modifies (matches AttributeDefinition.UniqueId).
	/// </summary>
	public string AttributeId { get; set; }

	/// <summary>
	/// Flat amount to add (can be negative for debuffs).
	/// </summary>
	public float Amount { get; set; }

	/// <summary>
	/// When this boost expires. Null = permanent.
	/// </summary>
	public DateTime? ExpiresAt { get; set; }

	/// <summary>
	/// Whether this boost has expired.
	/// </summary>
	public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;
}
