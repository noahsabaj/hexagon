namespace Hexagon.Commands;

/// <summary>
/// Registers all built-in admin and utility commands.
/// </summary>
internal static class BuiltInCommands
{
	internal static void Register()
	{
		// --- Character Management ---

		CommandManager.Register( new HexCommand
		{
			Name = "charsetname",
			Description = "Set a player's character name.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.String( "name", remainder: true )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var name = ctx.Get<string>( "name" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				target.Character.SetVar( "Name", name );
				return $"Set {target.DisplayName}'s character name to \"{name}\".";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "charsetdesc",
			Description = "Set a player's character description.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.String( "desc", remainder: true )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var desc = ctx.Get<string>( "desc" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				target.Character.SetVar( "Description", desc );
				return $"Set {target.DisplayName}'s character description.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "charsetmodel",
			Description = "Set a player's character model.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.String( "model" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var model = ctx.Get<string>( "model" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				target.Character.SetVar( "Model", model );
				return $"Set {target.DisplayName}'s model to \"{model}\".";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "charban",
			Description = "Ban a player's active character.",
			Permission = "a",
			Arguments = new[] { Arg.Player() },
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				target.Character.Ban();
				return $"Banned {target.DisplayName}'s character.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "charunban",
			Description = "Unban a player's active character.",
			Permission = "a",
			Arguments = new[] { Arg.Player() },
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				target.Character.Unban();
				return $"Unbanned {target.DisplayName}'s character.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "charkick",
			Description = "Unload a player's active character.",
			Permission = "a",
			Arguments = new[] { Arg.Player() },
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				CharacterManager.UnloadCharacter( target );
				return $"Kicked {target.DisplayName}'s character.";
			}
		} );

		// --- Currency ---

		CommandManager.Register( new HexCommand
		{
			Name = "givemoney",
			Description = "Give money to a player.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.Int( "amount" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var amount = ctx.Get<int>( "amount" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				if ( amount <= 0 )
					return "Amount must be positive.";

				Currency.CurrencyManager.GiveMoney( target.Character, amount, "admin" );
				return $"Gave {Currency.CurrencyManager.Format( amount )} to {target.DisplayName}.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "takemoney",
			Description = "Take money from a player.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.Int( "amount" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var amount = ctx.Get<int>( "amount" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				if ( amount <= 0 )
					return "Amount must be positive.";

				if ( !Currency.CurrencyManager.TakeMoney( target.Character, amount, "admin" ) )
					return $"{target.DisplayName} doesn't have {Currency.CurrencyManager.Format( amount )}.";

				return $"Took {Currency.CurrencyManager.Format( amount )} from {target.DisplayName}.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "setmoney",
			Description = "Set a player's money.",
			Permission = "a",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.Int( "amount" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var amount = ctx.Get<int>( "amount" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				Currency.CurrencyManager.SetMoney( target.Character, amount, "admin" );
				return $"Set {target.DisplayName}'s money to {Currency.CurrencyManager.Format( amount )}.";
			}
		} );

		// --- Flags ---

		CommandManager.Register( new HexCommand
		{
			Name = "flaggive",
			Description = "Grant permission flags to a player.",
			Permission = "s",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.String( "flags" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var flags = ctx.Get<string>( "flags" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				foreach ( var flag in flags )
				{
					target.Character.GiveFlag( flag );
				}

				return $"Gave flags '{flags}' to {target.DisplayName}.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "flagtake",
			Description = "Remove permission flags from a player.",
			Permission = "s",
			Arguments = new[]
			{
				Arg.Player(),
				Arg.String( "flags" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );
				var flags = ctx.Get<string>( "flags" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				foreach ( var flag in flags )
				{
					target.Character.TakeFlag( flag );
				}

				return $"Removed flags '{flags}' from {target.DisplayName}.";
			}
		} );

		// --- Doors ---

		CommandManager.Register( new HexCommand
		{
			Name = "doorown",
			Description = "Give door ownership to a player (look at a door).",
			Permission = "a",
			Arguments = new[] { Arg.Player() },
			OnRun = ( caller, ctx ) =>
			{
				var target = ctx.Get<HexPlayerComponent>( "player" );

				if ( target.Character == null )
					return $"{target.DisplayName} has no active character.";

				var door = GetLookedAtComponent<DoorComponent>( caller );
				if ( door == null )
					return "You must be looking at a door.";

				door.SetOwnerCharacter( target.Character.Id, target.CharacterName );
				HexLog.Add( LogType.Admin, caller, $"Set door \"{door.DoorName}\" ({door.DoorId}) owner to {target.DisplayName}" );
				return $"Gave door \"{door.DoorName}\" to {target.DisplayName}.";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "doorunown",
			Description = "Remove door ownership (look at a door).",
			Permission = "a",
			Arguments = Array.Empty<CommandArg>(),
			OnRun = ( caller, ctx ) =>
			{
				var door = GetLookedAtComponent<DoorComponent>( caller );
				if ( door == null )
					return "You must be looking at a door.";

				door.ClearOwner();
				HexLog.Add( LogType.Admin, caller, $"Cleared door \"{door.DoorName}\" ({door.DoorId}) ownership" );
				return $"Removed ownership from door \"{door.DoorName}\".";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "doorfactionown",
			Description = "Give door ownership to a faction (look at a door).",
			Permission = "a",
			Arguments = new[] { Arg.String( "faction" ) },
			OnRun = ( caller, ctx ) =>
			{
				var factionId = ctx.Get<string>( "faction" );

				var faction = FactionManager.GetFaction( factionId );
				if ( faction == null )
					return $"Faction '{factionId}' not found.";

				var door = GetLookedAtComponent<DoorComponent>( caller );
				if ( door == null )
					return "You must be looking at a door.";

				if ( !door.SetOwnerFaction( factionId ) )
					return "Faction door ownership is disabled.";

				HexLog.Add( LogType.Admin, caller, $"Set door \"{door.DoorName}\" ({door.DoorId}) faction owner to {faction.Name}" );
				return $"Gave door \"{door.DoorName}\" to faction \"{faction.Name}\".";
			}
		} );

		// --- Introduce (player command, no permission required) ---

		CommandManager.Register( new HexCommand
		{
			Name = "introduce",
			Description = "Introduce yourself to nearby players.",
			Permission = "",
			Arguments = new[] { Arg.Optional( Arg.String( "range" ), "" ) },
			OnRun = ( caller, ctx ) =>
			{
				if ( !caller.HasActiveCharacter )
					return "You have no active character.";

				var range = ctx.Get<string>( "range" )?.ToLower() ?? "";

				switch ( range )
				{
					case "whisper":
					{
						var r = Config.HexConfig.Get<float>( "chat.whisperRange", 100f );
						var count = RecognitionManager.IntroduceToRange( caller, r );
						return $"You introduced yourself to {count} nearby people.";
					}
					case "nearby":
					case "talk":
					case "":
					{
						// Default: talk range if text provided, look-at if truly empty
						if ( string.IsNullOrEmpty( range ) )
						{
							// Look-at target
							var pc = caller.GameObject.GetComponent<PlayerController>();
							if ( pc == null ) return "No player controller found.";

							var from = pc.EyePosition;
							var to = from + pc.EyeAngles.Forward * 200f;
							var tr = caller.Scene.Trace.Ray( from, to )
								.IgnoreGameObjectHierarchy( caller.GameObject )
								.Run();

							if ( !tr.Hit || tr.GameObject == null )
								return "You must be looking at a player.";

							var target = tr.GameObject.GetComponent<HexPlayerComponent>();
							if ( target == null || target.Character == null )
								return "You must be looking at a player.";

							if ( RecognitionManager.IntroduceToTarget( caller, target ) )
								return "You introduced yourself.";
							else
								return "They already know who you are.";
						}
						else
						{
							var r = Config.HexConfig.Get<float>( "chat.icRange", 300f );
							var count = RecognitionManager.IntroduceToRange( caller, r );
							return $"You introduced yourself to {count} nearby people.";
						}
					}
					case "yell":
					{
						var r = Config.HexConfig.Get<float>( "chat.yellRange", 600f );
						var count = RecognitionManager.IntroduceToRange( caller, r );
						return $"You introduced yourself to {count} nearby people.";
					}
					default:
						return "Usage: /introduce [whisper|nearby|yell]";
				}
			}
		} );

		// --- Vendors ---

		CommandManager.Register( new HexCommand
		{
			Name = "vendoradd",
			Description = "Add an item to a vendor (look at a vendor).",
			Permission = "a",
			Arguments = new[]
			{
				Arg.String( "itemId" ),
				Arg.Int( "buyPrice" ),
				Arg.Int( "sellPrice" )
			},
			OnRun = ( caller, ctx ) =>
			{
				var itemId = ctx.Get<string>( "itemId" );
				var buyPrice = ctx.Get<int>( "buyPrice" );
				var sellPrice = ctx.Get<int>( "sellPrice" );

				var definition = ItemManager.GetDefinition( itemId );
				if ( definition == null )
					return $"Item definition '{itemId}' not found.";

				var vendor = GetLookedAtComponent<VendorComponent>( caller );
				if ( vendor == null )
					return "You must be looking at a vendor.";

				vendor.AddItem( itemId, buyPrice, sellPrice );
				HexLog.Add( LogType.Admin, caller, $"Added \"{definition.DisplayName}\" to vendor \"{vendor.VendorName}\" (buy:{buyPrice}, sell:{sellPrice})" );
				return $"Added \"{definition.DisplayName}\" to vendor \"{vendor.VendorName}\" (buy: {CurrencyManager.Format( buyPrice )}, sell: {CurrencyManager.Format( sellPrice )}).";
			}
		} );

		CommandManager.Register( new HexCommand
		{
			Name = "vendorremove",
			Description = "Remove an item from a vendor (look at a vendor).",
			Permission = "a",
			Arguments = new[] { Arg.String( "itemId" ) },
			OnRun = ( caller, ctx ) =>
			{
				var itemId = ctx.Get<string>( "itemId" );

				var vendor = GetLookedAtComponent<VendorComponent>( caller );
				if ( vendor == null )
					return "You must be looking at a vendor.";

				if ( !vendor.RemoveItem( itemId ) )
					return $"Item '{itemId}' not found in this vendor.";

				HexLog.Add( LogType.Admin, caller, $"Removed \"{itemId}\" from vendor \"{vendor.VendorName}\"" );
				return $"Removed \"{itemId}\" from vendor \"{vendor.VendorName}\".";
			}
		} );
	}

	/// <summary>
	/// Raycast from a player's eyes to find a component of type T on a world object.
	/// Used by admin commands that target doors, vendors, etc.
	/// </summary>
	private static T GetLookedAtComponent<T>( HexPlayerComponent caller ) where T : Component
	{
		var pc = caller.GameObject.GetComponent<PlayerController>();
		if ( pc == null ) return null;

		var from = pc.EyePosition;
		var to = from + pc.EyeAngles.Forward * 200f;

		var tr = caller.Scene.Trace.Ray( from, to )
			.IgnoreGameObjectHierarchy( caller.GameObject )
			.Run();

		if ( !tr.Hit || tr.GameObject == null )
			return null;

		return tr.GameObject.GetComponent<T>();
	}
}
