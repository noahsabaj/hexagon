namespace Hexagon.Characters;

/// <summary>
/// Static helper that builds a default player GameObject when no custom PlayerPrefab is assigned.
/// Adds PlayerController (movement, camera, interaction), citizen model, and Dresser.
/// </summary>
public static class HexPlayerSetup
{
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
