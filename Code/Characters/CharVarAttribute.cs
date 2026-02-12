namespace Hexagon.Characters;

/// <summary>
/// Marks a property on a HexCharacterData subclass as a character variable.
/// The framework handles persistence, networking, and validation automatically.
///
/// Usage:
///   [CharVar(Default = "John Doe")] public string Name { get; set; }
///   [CharVar(Local = true)] public int Money { get; set; }  // Owner-only
///   [CharVar(NoNetworking = true)] public Dictionary&lt;string, object&gt; ServerData { get; set; }
/// </summary>
[AttributeUsage( AttributeTargets.Property, AllowMultiple = false )]
public class CharVarAttribute : Attribute
{
	/// <summary>
	/// Default value for this variable when creating a new character.
	/// </summary>
	public object Default { get; set; }

	/// <summary>
	/// If true, this variable is only networked to the owning player (not broadcast).
	/// Use for private data like money, flags, attributes.
	/// </summary>
	public bool Local { get; set; }

	/// <summary>
	/// If true, this variable is never networked. Server-only data.
	/// </summary>
	public bool NoNetworking { get; set; }

	/// <summary>
	/// Minimum string length for validation. Only applies to string properties.
	/// </summary>
	public int MinLength { get; set; }

	/// <summary>
	/// Maximum string length for validation. Only applies to string properties.
	/// </summary>
	public int MaxLength { get; set; }

	/// <summary>
	/// If true, this variable cannot be modified after character creation.
	/// </summary>
	public bool ReadOnly { get; set; }

	/// <summary>
	/// Display order in character creation UI. Lower numbers appear first.
	/// </summary>
	public int Order { get; set; } = 100;

	/// <summary>
	/// If true, this variable is shown in character creation UI.
	/// </summary>
	public bool ShowInCreation { get; set; } = true;
}
