namespace Hexagon.Characters;

/// <summary>
/// Handles the introduce mechanic RPC.
/// Satellite component on the player GameObject alongside HexPlayerComponent.
/// </summary>
public sealed class IntroducePlayerComponent : Component
{
	private HexPlayerComponent Player => GetComponent<HexPlayerComponent>();

	/// <summary>
	/// Client requests to introduce themselves. Level: 0=look-at, 1=whisper, 2=talk, 3=yell.
	/// </summary>
	[Rpc.Host]
	public void RequestIntroduce( int level )
	{
		var player = Core.RpcHelper.GetCallingPlayer();
		if ( player == null || player != Player ) return;

		if ( player.Character == null )
		{
			UI.NotificationManager.Send( player, "You have no active character." );
			return;
		}

		switch ( level )
		{
			case 0:
			{
				var pc = player.GameObject.GetComponent<PlayerController>();
				if ( pc == null ) break;

				var from = pc.EyePosition;
				var to = from + pc.EyeAngles.Forward * 200f;
				var tr = player.Scene.Trace.Ray( from, to )
					.IgnoreGameObjectHierarchy( player.GameObject )
					.Run();

				if ( tr.Hit && tr.GameObject != null )
				{
					var targetPlayer = tr.GameObject.GetComponent<HexPlayerComponent>();
					if ( targetPlayer != null && targetPlayer.Character != null )
					{
						if ( RecognitionManager.IntroduceToTarget( player, targetPlayer ) )
							UI.NotificationManager.Send( player, "You introduced yourself." );
						else
							UI.NotificationManager.Send( player, "They already know who you are." );
					}
					else
					{
						UI.NotificationManager.Send( player, "You must be looking at a player." );
					}
				}
				else
				{
					UI.NotificationManager.Send( player, "You must be looking at a player." );
				}
				break;
			}
			case 1:
			{
				var range = Config.HexConfig.Get<float>( "chat.whisperRange", 100f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
			case 2:
			{
				var range = Config.HexConfig.Get<float>( "chat.icRange", 300f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
			case 3:
			{
				var range = Config.HexConfig.Get<float>( "chat.yellRange", 600f );
				var count = RecognitionManager.IntroduceToRange( player, range );
				UI.NotificationManager.Send( player, $"You introduced yourself to {count} nearby people." );
				break;
			}
		}
	}
}
