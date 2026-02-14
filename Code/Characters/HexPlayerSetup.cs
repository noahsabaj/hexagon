namespace Hexagon.Characters;

/// <summary>
/// Static helper that builds and tears down player bodies.
/// On connect, players are bare networked objects. When a character loads,
/// BuildPlayerBody() adds the full player (PlayerController, model, Dresser).
/// When a character unloads, StripPlayerBody() removes everything.
/// </summary>
public static class HexPlayerSetup
{
	/// <summary>
	/// Build the full player body on an existing networked GameObject.
	/// Called when a character is loaded for the first time (or after switching characters).
	/// </summary>
	public static void BuildPlayerBody( HexPlayerComponent player, GameObject prefab = null )
	{
		if ( prefab != null )
		{
			// Clone prefab as child of the existing networked GO
			var clone = prefab.Clone();
			clone.Parent = player.GameObject;
			clone.LocalPosition = Vector3.Zero;
		}
		else
		{
			BuildDefaultPlayer( player.GameObject );
		}
	}

	/// <summary>
	/// Strip the player body back to a bare networked object.
	/// Removes PlayerController, WeaponRaise, and all child objects (Body, etc.).
	/// </summary>
	public static void StripPlayerBody( GameObject playerGo )
	{
		var controller = playerGo.GetComponent<PlayerController>();
		if ( controller != null ) controller.Destroy();

		var weaponRaise = playerGo.GetComponent<Interaction.WeaponRaiseComponent>();
		if ( weaponRaise != null ) weaponRaise.Destroy();

		foreach ( var child in playerGo.Children.ToList() )
		{
			child.Destroy();
		}
	}

	/// <summary>
	/// Configure a bare GameObject as a fully functional first-person player.
	/// Adds PlayerController, SkinnedModelRenderer (citizen), and Dresser.
	/// </summary>
	public static void BuildDefaultPlayer( GameObject playerGo )
	{
		// Add s&box PlayerController — auto-creates Rigidbody, Colliders, MoveModeWalk
		var controller = playerGo.AddComponent<PlayerController>();
		controller.ThirdPerson = false;
		controller.UseInputControls = true;
		controller.UseLookControls = true;
		controller.UseCameraControls = true;
		controller.EnablePressing = true;
		controller.HideBodyInFirstPerson = true;

		// Apply speeds from config
		controller.WalkSpeed = Config.HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
		controller.RunSpeed = Config.HexConfig.Get<float>( "gameplay.runSpeed", 320f );

		// Create "Body" child with SkinnedModelRenderer (citizen model)
		var body = new GameObject( true, "Body" );
		body.Parent = playerGo;

		var renderer = body.AddComponent<SkinnedModelRenderer>();
		renderer.Model = Model.Load( "models/citizen/citizen.vmdl" );

		// Wire renderer to PlayerController for animations
		controller.Renderer = renderer;

		// Add Dresser to auto-dress from Steam avatar
		var dresser = body.AddComponent<Dresser>();
		dresser.Source = Dresser.ClothingSource.OwnerConnection;
		dresser.BodyTarget = renderer;

		// Add weapon raise/lower component
		playerGo.AddComponent<Interaction.WeaponRaiseComponent>();
	}

	/// <summary>
	/// Apply character-specific data to an existing player (model, speeds).
	/// Called when a character loads or changes.
	/// </summary>
	public static void ApplyCharacterToPlayer( HexPlayerComponent player, HexCharacter character )
	{
		var controller = player.GetComponent<PlayerController>();
		if ( controller == null ) return;

		// Update speeds from config
		controller.WalkSpeed = Config.HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
		controller.RunSpeed = Config.HexConfig.Get<float>( "gameplay.runSpeed", 320f );

		// Apply character model if set
		var modelPath = player.CharacterModel;
		if ( !string.IsNullOrWhiteSpace( modelPath ) )
		{
			if ( controller.Renderer.IsValid() )
			{
				controller.Renderer.Model = Model.Load( modelPath );
			}
		}
	}
}
