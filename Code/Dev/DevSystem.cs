#if DEBUG
#nullable enable

using Sandbox;

namespace Hexagon.Dev;

/// <summary>
/// Where the tests reach into a running game, so the game's own code knows nothing of them. It is one
/// of the engine's scene systems, and only in a build made for development: a published game is
/// compiled for release, without it. While a test drives this client it holds the keyboard and mouse
/// still, each frame before anything reads them; it lets the server read the test's inbox; and it can
/// hold the camera somewhere, as the last of the engine's camera modifiers.
/// </summary>
public sealed class DevSystem : GameObjectSystem<DevSystem>, ISceneStartup, ICameraModifier
{
	private Player? _announced;

	public DevSystem( Scene scene ) : base( scene )
	{
		// Before the engine's own work at each stage, so nothing has read the input yet.
		Listen( Stage.StartFixedUpdate, -1000, Suppress, "Dev input" );
		Listen( Stage.StartUpdate, -1000, Frame, "Dev input" );
		Listen( Stage.FinishFixedUpdate, 0, DevCommands.PollInbox, "Dev inbox" );
	}

	/// <summary>Statics outlive a session in the editor, so each one played starts undriven.</summary>
	void ISceneStartup.OnHostInitialize() => DevCommands.Reset();

	void ISceneStartup.OnClientInitialize() => DevCommands.Reset();

	private void Frame()
	{
		Suppress();
		// A client says which test client it is once its player exists.
		if ( Player.Local is { } local && local != _announced )
		{
			_announced = local;
			DevCommands.Announce();
		}
	}

	/// <summary>
	/// A driven client acts as though nobody is at the keyboard, but still stands, falls and is
	/// pushed. The engine clears this as it reads the devices, so it is set again every frame.
	/// </summary>
	private static void Suppress()
	{
		if ( DevCommands.Driven ) Input.Suppressed = true;
	}

	/// <summary>After every other modifier, the player's own view included.</summary>
	int ICameraModifier.CameraOrder => 100000;

	void ICameraModifier.ModifyCamera( CameraComponent camera, ref CameraView view )
	{
		if ( DevCommands.HeldCamera is not { } held || camera != Scene.Camera ) return;
		view.Position = held.Position;
		view.Rotation = held.Rotation;
	}
}
#endif
