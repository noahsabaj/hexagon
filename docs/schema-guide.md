# Hexagon Schema Developer Guide

A comprehensive guide for building roleplay gamemodes ("schemas") on the Hexagon framework for s&box (Source 2, C#).

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Getting Started](#2-getting-started)
3. [Character Data](#3-character-data)
4. [Factions & Classes](#4-factions--classes)
5. [Items](#5-items)
6. [Inventory](#6-inventory)
7. [Plugins](#7-plugins)
8. [Chat](#8-chat)
9. [Commands](#9-commands)
10. [Permissions](#10-permissions)
11. [Currency](#11-currency)
12. [Attributes](#12-attributes)
13. [World Interaction](#13-world-interaction)
14. [UI Customization](#14-ui-customization)
15. [Configuration](#15-configuration)
16. [Events & Hooks](#16-events--hooks)
17. [Logging](#17-logging)
18. [Complete Example](#18-complete-example)

---

## 1. Introduction

**Hexagon** is a roleplay framework library for s&box (Source 2, C#). It is the successor to NutScript and Helix from Garry's Mod, rebuilt from the ground up for s&box's Scene/Component architecture.

A **schema** is a separate s&box Game project that references the Hexagon library to create a specific RP gamemode. Hexagon provides the underlying systems; your schema defines the content, rules, and flavor of your server.

The framework handles:

- **Characters** -- multi-character system with custom data fields
- **Factions & Classes** -- organizational groups with per-faction classes
- **Items & Inventory** -- grid-based inventory with item definitions and instances
- **Chat** -- class-based chat routing (IC, OOC, yell, whisper, etc.)
- **Commands** -- typed argument parsing, permission-gated commands
- **Permissions** -- flag-based character permissions with extensible checks
- **Currency** -- money management with hooks and physical currency items
- **Attributes** -- character stats with a boost/debuff system
- **Doors** -- lockable doors with ownership hierarchies
- **Storage** -- world containers backed by persistent inventories
- **Vendors** -- buy/sell NPCs with item catalogs
- **UI** -- full default Razor panel set (all overridable)
- **Logging** -- date-partitioned server logs for admin review
- **Persistence** -- automatic database-backed saving (FileSystem.Data + in-memory cache)

---

## 2. Getting Started

### Project Setup

1. Create a new **s&box Game** project in the s&box editor.
2. Add **Hexagon** as a library reference in your project settings.
3. Set up your scene with the following components:

**Minimal scene setup:**

```
Scene
  +-- Hexagon (GameObject)
  |     +-- HexagonFramework (Component)
  |     +-- HexGameManager (Component)
  +-- NetworkHelper (Component) -- s&box built-in, creates lobby on Play
  +-- CameraComponent -- required by PlayerController
  +-- DirectionalLight
  +-- Floor/Ground -- something to stand on
```

That's it. No PlayerPrefab, no ScreenPanel, no manual panel placement needed.

The `HexagonFramework` component bootstraps all Hexagon systems on `OnStart()`. The `HexGameManager` handles player spawning and tracks connected players.

**What happens automatically:**

- **Player setup**: When no `PlayerPrefab` is assigned on HexGameManager, Hexagon creates a default first-person player with `PlayerController` (WASD movement, mouse look, USE key interaction), a citizen model, and a `Dresser` that auto-applies the player's Steam avatar clothing.
- **UI setup**: On initialization, Hexagon auto-creates a `ScreenPanel` root with `HexUIManager` and all 9 default panels (CharacterSelect, CharacterCreate, HUD, Chat, Inventory, Storage, Vendor, Scoreboard, DeathScreen).
- **Character model**: When a character loads, `HexModelHandler` applies any custom model and walk/run speeds from config.

**Custom player prefab (optional):**

If you want full control over the player GameObject, create a prefab with at least a `HexPlayerComponent` and assign it to `HexGameManager.PlayerPrefab`. When a prefab is assigned, the default player setup is skipped entirely.

**Custom UI (optional):**

If you place a `HexUIManager` in your scene manually, the auto-creation is skipped. You can also replace individual panels by disabling the defaults and adding your own `IHexPanel` implementations.

### Minimum Required Code

At minimum, your schema needs two things:

1. **A `HexCharacterData` subclass** -- defines what data your characters store.
2. **A `[HexPlugin]` class** (recommended) -- registers factions, items, config, and hooks.

```csharp
// MyCharacter.cs
namespace MySchema;

public class MyCharacter : HexCharacterData
{
    [CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
    public string Name { get; set; }

    [CharVar( Default = "A mysterious stranger.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
    public string Description { get; set; }

    [CharVar( Order = 3, ShowInCreation = true )]
    public string Model { get; set; }

    [CharVar( Local = true, Default = 0 )]
    public int Money { get; set; }
}
```

```csharp
// MyPlugin.cs
namespace MySchema;

[HexPlugin( "My Schema", Description = "My custom RP schema", Author = "Me", Version = "1.0", Priority = 50 )]
public class MyPlugin : IHexPlugin
{
    public void OnPluginLoaded()
    {
        // Register factions, items, config, attach hooks, etc.
    }

    public void OnPluginUnloaded() { }
}
```

Once your scene loads and `HexagonFramework` initializes, Hexagon discovers your `HexCharacterData` subclass via `TypeLibrary` and your `[HexPlugin]` class automatically.

---

## 3. Character Data

Characters are the core of any RP framework. In Hexagon, a character is not the same as a player -- a single player can own multiple characters.

### Defining Character Data

Subclass `HexCharacterData` and add `[CharVar]` properties to define the fields your characters store:

```csharp
public class MyCharacter : HexCharacterData
{
    [CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
    public string Name { get; set; }

    [CharVar( Default = "A mysterious stranger.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
    public string Description { get; set; }

    [CharVar( Order = 3, ShowInCreation = true )]
    public string Model { get; set; }

    [CharVar( Local = true, Default = 0 )]
    public int Money { get; set; }
}
```

### CharVar Attribute Options

| Option | Type | Description |
|---|---|---|
| `Default` | `object` | Default value assigned to new characters. |
| `MinLength` | `int` | Minimum string length (validated on creation). |
| `MaxLength` | `int` | Maximum string length (validated on creation). |
| `Local` | `bool` | Only synced to the owning player, not broadcast to others. Use for private data like money. |
| `NoNetworking` | `bool` | Server-only. Never sent to any client. Use for internal tracking data. |
| `ReadOnly` | `bool` | Cannot be changed via `SetVar()`. Only settable during creation. |
| `Order` | `int` | Display order in the character creation UI. |
| `ShowInCreation` | `bool` | Whether this field appears as an input in the character creation panel. |

### Base Class Fields

`HexCharacterData` already provides these fields -- you do not need to redefine them:

- `Id` -- unique character identifier (string)
- `SteamId` -- owning player's Steam ID (ulong)
- `Slot` -- character slot number (int)
- `Faction` -- faction ID the character belongs to (string)
- `Class` -- class ID within the faction (string)
- `Flags` -- permission flags string (string)
- `Data` -- generic dictionary for arbitrary key/value storage (Dictionary<string, object>)
- `CreatedAt` -- creation timestamp (DateTime)
- `LastPlayedAt` -- last played timestamp (DateTime)
- `IsBanned` -- whether the character is banned (bool)
- `BanExpiry` -- when the ban expires (DateTime?)

### Three-Layer Architecture

Hexagon uses three layers for characters:

1. **`HexCharacterData`** -- pure data class, serialized to the database.
2. **`HexCharacter`** -- runtime wrapper around `HexCharacterData`. Provides `GetVar`/`SetVar` and manages networking.
3. **`HexPlayerComponent`** -- the Component on the player's GameObject. Bridges the character to the scene. Contains the `Character` property.

### Runtime Access

```csharp
// Get a player's active character
HexCharacter character = player.Character;

// Read/write character variables
string name = character.GetVar<string>( "Name" );
character.SetVar( "Name", "Jane Doe" );

// Read/write the generic Data dictionary
character.SetData( "walkSpeed", 200f );
float speed = character.GetData<float>( "walkSpeed" );

// Access the underlying data object
HexCharacterData data = character.Data;
string faction = data.Faction;
```

---

## 4. Factions & Classes

Factions organize characters into groups. Classes are sub-roles within a faction.

### Two Registration Methods

**1. Asset files** -- Create `.faction` and `.class` asset files in the s&box editor. They are auto-registered when the framework initializes.

**2. Programmatic** -- Register in your plugin's `OnPluginLoaded()`:

```csharp
private void RegisterFactions()
{
    var citizens = TypeLibrary.Create<FactionDefinition>();
    citizens.UniqueId = "citizen";
    citizens.Name = "Citizens";
    citizens.Description = "Regular citizens of the city.";
    citizens.IsDefault = true;
    citizens.MaxPlayers = 0; // 0 = unlimited
    citizens.Color = new Color( 0.29f, 0.565f, 0.886f );
    citizens.StartingMoney = 100;
    citizens.Order = 1;
    FactionManager.Register( citizens );

    var police = TypeLibrary.Create<FactionDefinition>();
    police.UniqueId = "police";
    police.Name = "Civil Protection";
    police.Description = "Law enforcement officers.";
    police.IsDefault = false;
    police.MaxPlayers = 4;
    police.Color = new Color( 0.886f, 0.29f, 0.29f );
    police.StartingMoney = 200;
    police.Order = 2;
    FactionManager.Register( police );
}
```

### Registering Classes

```csharp
private void RegisterClasses()
{
    var civilian = TypeLibrary.Create<ClassDefinition>();
    civilian.UniqueId = "civilian";
    civilian.Name = "Civilian";
    civilian.Description = "An ordinary citizen.";
    civilian.FactionId = "citizen"; // links to the parent faction
    civilian.MaxPlayers = 0;
    civilian.Order = 1;
    FactionManager.RegisterClass( civilian );

    var officer = TypeLibrary.Create<ClassDefinition>();
    officer.UniqueId = "officer";
    officer.Name = "Officer";
    officer.Description = "A Civil Protection officer.";
    officer.FactionId = "police";
    officer.MaxPlayers = 0;
    officer.Order = 1;
    FactionManager.RegisterClass( officer );

    var chief = TypeLibrary.Create<ClassDefinition>();
    chief.UniqueId = "chief";
    chief.Name = "Chief";
    chief.Description = "The Chief of Civil Protection.";
    chief.FactionId = "police";
    chief.MaxPlayers = 1; // only one chief allowed
    chief.Order = 2;
    FactionManager.RegisterClass( chief );
}
```

### FactionDefinition Properties

| Property | Type | Description |
|---|---|---|
| `UniqueId` | `string` | Unique identifier (used in code and database). |
| `Name` | `string` | Display name. |
| `Description` | `string` | Description shown in UI. |
| `IsDefault` | `bool` | If true, new characters can select this faction by default. |
| `MaxPlayers` | `int` | Maximum concurrent players. 0 = unlimited. |
| `Color` | `Color` | Faction color for UI elements. |
| `StartingMoney` | `int` | Money given to new characters joining this faction. |
| `Order` | `int` | Sort order in UI. |

### Query APIs

```csharp
FactionDefinition faction = FactionManager.GetFaction( "citizen" );
List<FactionDefinition> all = FactionManager.GetAllFactions();
List<FactionDefinition> defaults = FactionManager.GetDefaultFactions();
List<ClassDefinition> classes = FactionManager.GetClassesForFaction( "police" );
bool canJoin = FactionManager.CanJoinFaction( "police" ); // checks MaxPlayers
```

---

## 5. Items

The item system has two distinct layers:

- **`ItemDefinition`** (template) -- defines an item type: its name, size, category, and behavior.
- **`ItemInstance`** (database-backed) -- a specific item owned by a character, created from a definition.

Think of `ItemDefinition` as a blueprint and `ItemInstance` as a manufactured copy.

### Built-In Item Types

Hexagon ships with several `ItemDefinition` subclasses for common RP item types:

| Class | Key Properties | Behavior |
|---|---|---|
| `WeaponItemDef` | `AmmoType`, `ClipSize`, `WeaponModel`, `TwoHanded` | Equip/unequip actions. |
| `BagItemDef` | `BagWidth`, `BagHeight` | Creates a nested inventory when used. Open action. |
| `OutfitItemDef` | `OutfitModel`, `Bodygroups`, `Slot` | Wear/take off actions. Applies clothing to the character model. |
| `CurrencyItemDef` | `DefaultAmount` | Adds to character money on use. Pick up action. |
| `AmmoItemDef` | `AmmoType`, `AmmoAmount` | Loads into compatible weapons. Use action. |

### Registering Items

```csharp
// Weapon
var pistol = TypeLibrary.Create<WeaponItemDef>();
pistol.UniqueId = "weapon_pistol";
pistol.DisplayName = "Pistol";
pistol.Description = "A standard 9mm pistol.";
pistol.Width = 1;
pistol.Height = 2;
pistol.Category = "Weapons";
pistol.ClipSize = 12;
pistol.AmmoType = "9mm";
ItemManager.Register( pistol );

// Bag
var backpack = TypeLibrary.Create<BagItemDef>();
backpack.UniqueId = "bag_backpack";
backpack.DisplayName = "Backpack";
backpack.Description = "A sturdy backpack for extra storage.";
backpack.Width = 1;
backpack.Height = 2;
backpack.Category = "Storage";
backpack.BagWidth = 6;
backpack.BagHeight = 4;
ItemManager.Register( backpack );

// Outfit
var uniform = TypeLibrary.Create<OutfitItemDef>();
uniform.UniqueId = "outfit_police";
uniform.DisplayName = "Police Uniform";
uniform.Description = "A standard Civil Protection uniform.";
uniform.Width = 1;
uniform.Height = 2;
uniform.Category = "Clothing";
uniform.Slot = "torso";
ItemManager.Register( uniform );

// Currency
var cash = TypeLibrary.Create<CurrencyItemDef>();
cash.UniqueId = "money_cash";
cash.DisplayName = "Cash";
cash.Description = "A stack of bills.";
cash.Width = 1;
cash.Height = 1;
cash.Category = "Currency";
cash.DefaultAmount = 100;
ItemManager.Register( cash );

// Ammo
var ammo = TypeLibrary.Create<AmmoItemDef>();
ammo.UniqueId = "ammo_9mm";
ammo.DisplayName = "9mm Ammo";
ammo.Description = "A box of 9mm rounds.";
ammo.Width = 1;
ammo.Height = 1;
ammo.Category = "Ammo";
ammo.AmmoType = "9mm";
ammo.AmmoAmount = 12;
ItemManager.Register( ammo );
```

**Note:** Use `DisplayName` (not `Name`) for the item's display name.

### Creating Item Instances

```csharp
ItemInstance item = ItemManager.CreateInstance( "weapon_pistol", characterId );
```

This creates a new database-backed instance of the "weapon_pistol" definition, owned by the specified character.

### Custom Item Behavior

To create items with custom behavior, subclass one of the built-in types (or `ItemDefinition` directly) and override the virtual methods:

```csharp
public class HealingItemDef : ItemDefinition
{
    public int HealAmount { get; set; } = 25;

    public override List<ItemAction> GetActions()
    {
        return new List<ItemAction>
        {
            new ItemAction
            {
                Name = "Use",
                Icon = "healing",
                OnCanRun = ( player, item ) => player.Character != null,
                OnRun = ( player, item ) =>
                {
                    // Heal the character
                    var health = AttributeManager.GetAttribute( player.Character, "health" );
                    AttributeManager.SetAttribute( player.Character, "health", health + HealAmount );
                    return true; // consume the item
                }
            }
        };
    }

    public override void OnInstanced( ItemInstance instance )
    {
        // Called when a new instance is created from this definition
    }

    public override void OnRemoved( ItemInstance instance )
    {
        // Called when an instance is deleted
    }
}
```

### Virtual Methods on ItemDefinition

| Method | Description |
|---|---|
| `GetActions()` | Returns the list of context-menu actions for this item type. |
| `OnUse( player, item )` | Called when the item is used. |
| `OnEquip( player, item )` | Called when the item is equipped. |
| `OnUnequip( player, item )` | Called when the item is unequipped. |
| `OnDrop( player, item )` | Called when the item is dropped into the world. |
| `OnPickup( player, item )` | Called when the item is picked up from the world. |
| `OnCanUse( player, item )` | Return false to prevent usage. |
| `OnInstanced( item )` | Called when a new instance is created from this definition. |
| `OnRemoved( item )` | Called when an instance is permanently deleted. |
| `OnTransferred( item, from, to )` | Called when an item moves between inventories. |

### ItemAction Structure

```csharp
new ItemAction
{
    Name = "Eat",                                          // Display name
    Icon = "food",                                         // Icon identifier
    OnRun = ( player, item ) => { /* do stuff */ return true; },  // Action logic; return true to consume
    OnCanRun = ( player, item ) => true                    // Visibility/availability check
}
```

---

## 6. Inventory

Hexagon uses a **grid-based inventory** system. Items occupy `Width x Height` cells in a 2D grid.

### Creating Inventories

```csharp
// Create a default-sized inventory for a character (uses config values)
var inv = InventoryManager.CreateDefault( characterId );

// Create a custom-sized inventory
var inv = InventoryManager.Create( 6, 4, characterId, "main" );
```

### Adding Items

```csharp
// Auto-find an empty slot that fits the item
inv.Add( item );

// Place at a specific grid position
inv.AddAt( item, 0, 0 );

// Find a slot before adding (useful for validation)
var slot = inv.FindEmptySlot( def.Width, def.Height );
if ( slot.HasValue )
{
    inv.AddAt( item, slot.Value.X, slot.Value.Y );
}
```

### Querying

```csharp
bool has = inv.HasItem( "weapon_pistol" );       // has at least one?
int count = inv.CountItem( "weapon_pistol" );     // how many?
ItemInstance found = inv.FindItem( "weapon_pistol" ); // get first match
bool full = inv.IsFull;                           // no empty space at all?
int total = inv.ItemCount;                        // total items in inventory
```

### Transferring Items

```csharp
inv.Transfer( itemId, targetInventory );
```

### Loading Character Inventories

```csharp
List<HexInventory> inventories = InventoryManager.LoadForCharacter( characterId );
```

### Receiver Networking

Inventories use a **receiver system** for selective networking. Only players added as receivers can see the inventory contents. This prevents leaking other players' inventory data.

```csharp
// Grant visibility (typically done in OnCharacterLoaded)
inv.AddReceiver( player.Connection );

// Revoke visibility (typically done in OnCharacterUnloaded or when closing storage)
inv.RemoveReceiver( player.Connection );
```

Dirty inventories are automatically flushed to receivers each frame by `HexInventoryComponent`.

---

## 7. Plugins

Plugins are the primary extension point for Hexagon. Your schema itself is a plugin.

### Defining a Plugin

```csharp
[HexPlugin( "My Plugin", Description = "Does stuff", Author = "Me", Version = "1.0", Priority = 50 )]
public class MyPlugin : IHexPlugin
{
    public void OnPluginLoaded()
    {
        // Register items, factions, commands, config, etc.
    }

    public void OnPluginUnloaded()
    {
        // Cleanup (optional)
    }
}
```

### Priority

The `Priority` value controls load order. **Lower numbers load first.**

| Priority | Use Case |
|---|---|
| 1-49 | Core/foundation plugins that other plugins depend on. |
| 50 | Recommended for schemas. |
| 100 | Default. Use for optional addon plugins. |
| 100+ | Plugins that need to override schema behavior. |

### Adding Event Hooks

Listener interfaces must live on **Components** so that `Scene.GetAll<T>()` can discover them. The standard pattern is to create a hooks Component and attach it to the `HexagonFramework` GameObject:

```csharp
[HexPlugin( "My Schema", Priority = 50 )]
public class MyPlugin : IHexPlugin
{
    public void OnPluginLoaded()
    {
        // Attach hooks component to the framework GameObject
        HexagonFramework.Instance.GameObject.GetOrAddComponent<MyHooks>();
    }

    public void OnPluginUnloaded() { }
}

public class MyHooks : Component, ICharacterLoadedListener, ICharacterCreatedListener
{
    public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
    {
        // Handle character load
    }

    public void OnCharacterCreated( HexPlayerComponent player, HexCharacter character )
    {
        // Handle character creation
    }
}
```

### Plugin vs Schema

There is no technical difference between a "plugin" and a "schema" -- both use `[HexPlugin]` and `IHexPlugin`. The distinction is conceptual:

- A **schema** is the main gamemode (priority 50, defines factions/items/rules).
- A **plugin** is an optional addon (priority 100+, adds features on top of the schema).

---

## 8. Chat

Chat in Hexagon is routed through **chat classes**. Each class defines a communication channel with its own prefix, range, formatting, and color.

### Defining a Chat Class

Implement the `IChatClass` interface:

```csharp
public class RadioChat : IChatClass
{
    public string Name => "Radio";
    public string Prefix => "/r";            // players type "/r Hello" to use this channel
    public float Range => 0;                 // 0 = global (heard by everyone)
    public Color Color => new Color( 0.4f, 0.8f, 0.4f );

    public bool CanHear( HexPlayerComponent listener, HexPlayerComponent speaker )
    {
        // Only police faction can hear the radio
        return listener.Character?.Data.Faction == "police";
    }

    public bool CanSay( HexPlayerComponent player )
    {
        // Only police faction can transmit on the radio
        return player.Character?.Data.Faction == "police";
    }

    public string Format( HexPlayerComponent player, string message )
    {
        var name = player.Character?.GetVar<string>( "Name" ) ?? "Unknown";
        return $"[RADIO] {name}: {message}";
    }
}
```

### Registering

```csharp
ChatManager.Register( new RadioChat() );
```

### Built-In Chat Classes

| Class | Prefix | Range | Description |
|---|---|---|---|
| IC | *(none)* | Config-based | In-character speech. Default channel. |
| OOC | `//` | Global | Out-of-character. |
| Yell | `!` | Config-based (extended) | Loud in-character speech. |
| Whisper | `/w` | Config-based (short) | Quiet in-character speech. |
| Local | `/l` | Config-based | Alias for IC range. |

Chat ranges are controlled by config keys: `chat.icRange`, `chat.yellRange`, `chat.whisperRange`.

---

## 9. Commands

Commands are text-based actions triggered from the chat input (e.g., `/setmoney Player1 500`).

### Defining a Command

```csharp
var cmd = new HexCommand
{
    Name = "setmoney",
    Description = "Set a player's money",
    Aliases = new[] { "sm" },
    Permission = "a", // requires admin flag
    Arguments = new[]
    {
        new CommandArg { Name = "target", Type = typeof( HexPlayerComponent ) },
        new CommandArg { Name = "amount", Type = typeof( int ) }
    },
    OnRun = ( caller, args ) =>
    {
        var target = (HexPlayerComponent)args[0];
        var amount = (int)args[1];
        CurrencyManager.SetMoney( target.Character, amount );
    }
};
CommandManager.Register( cmd );
```

### Supported Argument Types

| Type | Behavior |
|---|---|
| `HexPlayerComponent` | Player name lookup. Matches partial names. |
| `int` | Parsed as integer. |
| `float` | Parsed as floating-point number. |
| `string` | Single word. If this is the **last** argument, it captures the remainder of the input. |

### Permission Gating

The `Permission` field is a flag string. The caller must have **all** specified flags. Set to `""` or `null` for no permission requirement.

```csharp
Permission = "a"   // requires admin flag
Permission = "s"   // requires superadmin flag
Permission = ""    // anyone can run
Permission = null  // anyone can run
```

### HexCommand Properties

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Primary command name (e.g., `"setmoney"` for `/setmoney`). |
| `Description` | `string` | Help text. |
| `Aliases` | `string[]` | Alternative names (e.g., `"sm"` for `/sm`). |
| `Permission` | `string` | Required permission flags. |
| `Arguments` | `CommandArg[]` | Ordered argument definitions. |
| `OnRun` | `Action<HexPlayerComponent, object[]>` | Execution callback. |

---

## 10. Permissions

Hexagon uses a **flag-based** permission system. Each character has a `Flags` string where each character represents a permission.

### Default Flags

| Flag | Name | Description |
|---|---|---|
| `p` | Physgun | Can use the physics gun. |
| `t` | Toolgun | Can use the tool gun. |
| `e` | Entity | Can spawn entities. |
| `o` | Door | Can manage any door. |
| `v` | Vendor | Can manage any vendor. |
| `a` | Admin | Server administrator. |
| `s` | Superadmin | Bypasses all permission checks. |

### API

```csharp
// Check
bool isAdmin = character.HasFlag( 'a' );
bool canBuild = PermissionManager.HasPermission( character, "pet" ); // has p, e, AND t?

// Grant/Revoke
character.GiveFlag( 'a' );
character.TakeFlag( 'a' );
```

### Custom Permission Logic

For dynamic permission checks (e.g., VIP status, time-based access), implement `IPermissionCheckListener` on a Component:

```csharp
public class VipPermissions : Component, IPermissionCheckListener
{
    public bool CanPerformAction( HexCharacter character, string permission )
    {
        // VIP characters bypass certain checks
        if ( character.GetData<bool>( "isVip" ) && permission == "e" )
            return true;

        return character.HasFlag( permission[0] );
    }
}
```

---

## 11. Currency

The currency system wraps the character's `Money` CharVar with convenience methods and hooks.

### API

```csharp
int money = CurrencyManager.GetMoney( character );
CurrencyManager.GiveMoney( character, 100 );       // add 100
CurrencyManager.TakeMoney( character, 50 );         // subtract 50
CurrencyManager.SetMoney( character, 500 );          // set to exactly 500
bool canBuy = CurrencyManager.CanAfford( character, 200 ); // has >= 200?
string display = CurrencyManager.Format( 1500 );     // "$1,500"
```

### Hooks

Use listener interfaces to intercept or react to money changes:

```csharp
public class MoneyHooks : Component, ICanMoneyChangeListener, IMoneyChangedListener
{
    public bool CanMoneyChange( HexCharacter character, int oldAmount, int newAmount )
    {
        // Prevent going below 0
        if ( newAmount < 0 ) return false;
        return true;
    }

    public void OnMoneyChanged( HexCharacter character, int oldAmount, int newAmount )
    {
        Log.Info( $"{character.GetVar<string>( "Name" )} money: {oldAmount} -> {newAmount}" );
    }
}
```

### Physical Currency

Use `CurrencyItemDef` to create droppable money items. When a player uses the item, its `DefaultAmount` is added to their character's money and the item is consumed.

```csharp
var cash = TypeLibrary.Create<CurrencyItemDef>();
cash.UniqueId = "money_cash";
cash.DisplayName = "Cash";
cash.Width = 1;
cash.Height = 1;
cash.Category = "Currency";
cash.DefaultAmount = 100; // adds $100 when picked up
ItemManager.Register( cash );
```

---

## 12. Attributes

Attributes are named numerical stats on characters (e.g., hunger, health, strength). They support a **boost system** for temporary buffs and debuffs.

### Defining Attributes

Create `.attrib` asset files in the editor, or register programmatically:

```csharp
var hunger = TypeLibrary.Create<AttributeDefinition>();
hunger.UniqueId = "hunger";
hunger.DisplayName = "Hunger";
hunger.MinValue = 0;
hunger.MaxValue = 100;
hunger.StartValue = 100; // new characters start at 100
AttributeManager.Register( hunger );
```

### API

```csharp
// Get/Set base value
float val = AttributeManager.GetAttribute( character, "hunger" );
AttributeManager.SetAttribute( character, "hunger", 100 );
AttributeManager.AddAttribute( character, "hunger", -10 ); // subtract 10

// Boosts (named, stackable)
AttributeManager.AddBoost( character, "strength", "buff_potion", 15 );
AttributeManager.RemoveBoost( character, "strength", "buff_potion" );
```

### Boost System

Boosts are named bonuses that stack on top of the base value. The **effective value** of an attribute is:

```
effective = base + sum(all boosts)
```

Each boost has a unique key so it can be individually added and removed. This is useful for temporary effects like potions, equipment bonuses, or debuffs.

```csharp
// Base strength is 10
AttributeManager.SetAttribute( character, "strength", 10 );

// Add a potion boost (+15) and an armor boost (+5)
AttributeManager.AddBoost( character, "strength", "potion_of_might", 15 );
AttributeManager.AddBoost( character, "strength", "iron_armor", 5 );

// Effective strength is now 30 (10 + 15 + 5)
float effective = AttributeManager.GetAttribute( character, "strength" );

// Remove the potion boost when it expires
AttributeManager.RemoveBoost( character, "strength", "potion_of_might" );

// Effective strength is now 15 (10 + 5)
```

---

## 13. World Interaction

All world-interactive objects implement `IPressable`, the s&box interface for raycast-based interaction. The player's `PlayerController` automatically detects `IPressable` components via raycast and calls `Press()` when the player interacts.

### Built-In Interactables

#### DoorComponent

Lockable doors with an ownership hierarchy.

```
Auth hierarchy: superadmin > admin > owner > faction > access list
```

A player can use a door if any of the following are true (checked in order):
1. They have the `s` (superadmin) flag.
2. They have the `o` (door admin) flag.
3. They are the door's owner.
4. Their faction matches the door's faction.
5. Their character ID is in the door's access list.

**Hooks:** `ICanUseDoorListener`, `IDoorUsedListener`, `IDoorOwnerChangedListener`.

#### StorageComponent

World containers with grid inventories. Place this component on a prop in your scene.

| Property | Type | Description |
|---|---|---|
| `StorageName` | `string` | Display name for the storage container. |
| `Width` | `int` | Grid width. |
| `Height` | `int` | Grid height. |

When a player presses the storage, their client opens the `StoragePanel` and they are added as a receiver of the storage inventory.

**Hooks:** `ICanOpenStorageListener`, `IStorageOpenedListener`, `IStorageClosedListener`.

#### VendorComponent

Buy/sell NPCs with item catalogs. Place this component on an NPC model in your scene and configure its item catalog.

When a player presses the vendor, their client opens the `VendorPanel` showing available items.

**Hooks:** `ICanBuyItemListener`, `ICanSellItemListener`, `IItemBoughtListener`, `IItemSoldListener`, `IVendorOpenedListener`.

---

## 14. UI Customization

Hexagon ships with a full set of default Razor UI panels. All panels are overridable by schema developers.

### State Machine

The UI follows a state machine with these states:

```
Loading -> CharacterSelect -> CharacterCreate -> Gameplay -> Dead
```

Transitions:
- **Loading** -- shown while the framework initializes.
- **CharacterSelect** -- player picks from their existing characters (when `character.autoLoad` is false).
- **CharacterCreate** -- player creates a new character.
- **Gameplay** -- the main HUD with chat, inventory, etc.
- **Dead** -- death screen with respawn button.

### Built-In Panels

| Panel Name | State | Description |
|---|---|---|
| `CharacterSelect` | CharacterSelect | List of existing characters to load. |
| `CharacterCreate` | CharacterCreate | Form for creating a new character. |
| `HUD` | Gameplay | Main HUD overlay. |
| `Chat` | Gameplay | Chat input and message history. |
| `Inventory` | Gameplay | Grid inventory panel. |
| `Storage` | Gameplay | Storage container panel (opens alongside inventory). |
| `Vendor` | Gameplay | Vendor buy/sell panel. |
| `Scoreboard` | Gameplay | Player list. |
| `DeathScreen` | Dead | Death overlay with respawn button. |

### IHexPanel Interface

All panels implement `IHexPanel`:

```csharp
public interface IHexPanel
{
    string PanelName { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
}
```

### Auto-Setup

By default, `HexagonFramework.Initialize()` calls `HexUISetup.EnsureUI()` which creates a `ScreenPanel` root with `HexUIManager` and all 9 default panels. If you place a `HexUIManager` in your scene manually, the auto-creation is skipped.

### Replacing a Panel

To replace a built-in panel with your own:

1. Disable the default panel component on the auto-created UI (or place your own `HexUIManager` and panels manually).
2. Create your own Razor `PanelComponent` that implements `IHexPanel` with the same `PanelName`.
3. Add it to a GameObject with a `ScreenPanel` in your scene.

```csharp
public class MyCustomHud : PanelComponent, IHexPanel
{
    public string PanelName => "HUD";
    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        StateHasChanged();
    }

    public void Close()
    {
        IsOpen = false;
        StateHasChanged();
    }

    protected override int BuildHash() => HashCode.Combine( IsOpen );
}
```

### Controlling UI State

```csharp
// Switch state
HexUIManager.Instance.SetState( UIState.Gameplay );
HexUIManager.Instance.SetState( UIState.Dead );

// Open/close specific panels
HexUIManager.Instance.OpenPanel( "Inventory" );
HexUIManager.Instance.ClosePanel( "Inventory" );
```

### Input Bindings

| Key | Action |
|---|---|
| TAB | Toggle Scoreboard |
| I | Toggle Inventory |
| ENTER | Focus Chat input |
| ESC | Close topmost open panel |

### Accessing the Local Player

When writing client-side UI code, use the static helper instead of `Scene.Active`:

```csharp
HexPlayerComponent localPlayer = HexUIManager.GetLocalPlayer();
```

This uses `HexUIManager.Instance.Scene` internally, which is more reliable than `Scene.Active`.

---

## 15. Configuration

The `HexConfig` system provides persistent, admin-controlled server settings.

### Registering Config Values

Register your config keys in `OnPluginLoaded()`:

```csharp
public void OnPluginLoaded()
{
    HexConfig.Add( "gameplay.walkSpeed", 200f, "Default walk speed", "Gameplay" );
    HexConfig.Add( "gameplay.runSpeed", 320f, "Default run speed", "Gameplay" );
    HexConfig.Add( "gameplay.maxCarryWeight", 50, "Max carry weight in kg", "Gameplay" );
}
```

### Reading Values

```csharp
float speed = HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
int maxWeight = HexConfig.Get<int>( "gameplay.maxCarryWeight", 50 );
```

The second parameter is a fallback value used if the key has no registered default and no override.

### Writing Values (Admin)

```csharp
HexConfig.Set( "gameplay.walkSpeed", 150f );
```

### Resetting to Default

```csharp
HexConfig.Reset( "gameplay.walkSpeed" );
```

### Change Callbacks

You can register a callback that fires when a config value changes:

```csharp
HexConfig.Add( "gameplay.walkSpeed", 200f, "Walk speed", "Gameplay",
    onChange: ( oldVal, newVal ) =>
    {
        Log.Info( $"Walk speed changed from {oldVal} to {newVal}" );
    }
);
```

### Built-In Config Keys

| Key | Default | Description |
|---|---|---|
| `framework.saveInterval` | 300 | Auto-save interval in seconds. |
| `framework.debug` | false | Enable debug logging. |
| `character.maxPerPlayer` | 5 | Max characters per player. |
| `character.autoLoad` | false | Auto-load last character on connect (false = show character list). |
| `chat.icRange` | 600 | IC chat hearing range in units. |
| `chat.yellRange` | 1200 | Yell chat hearing range. |
| `chat.whisperRange` | 200 | Whisper chat hearing range. |
| `inventory.defaultWidth` | 6 | Default inventory grid width. |
| `inventory.defaultHeight` | 4 | Default inventory grid height. |
| `currency.symbol` | "$" | Currency display symbol. |
| `currency.startingAmount` | 0 | Default starting money. |

---

## 16. Events & Hooks

Hexagon uses **listener interfaces** for event hooks. Implement these interfaces on a `Component` and add the component to a GameObject in the scene (typically the `HexagonFramework` GameObject). The framework discovers listeners automatically via `Scene.GetAll<T>()`.

### Hook Priority

When multiple listeners exist for the same event, they execute in this order:

```
Plugins (by priority) -> Schema -> Framework defaults
```

### Firing Events

Hexagon provides three event-firing patterns:

```csharp
// Fire: call a method on all listeners
HexEvents.Fire<ICharacterLoadedListener>( x => x.OnCharacterLoaded( player, character ) );

// CanAll: return true only if ALL listeners return true (gating checks)
bool allowed = HexEvents.CanAll<ICanCharacterCreate>( x => x.CanCharacterCreate( player, data ) );

// Reduce: transform a value through all listeners
Vector3 spawnPos = HexEvents.Reduce<IPlayerSpawnListener, Vector3>(
    defaultPos,
    ( listener, pos ) => listener.GetSpawnPosition( connection, pos )
);
```

### All Listener Interfaces

#### Framework Lifecycle

```csharp
public interface IFrameworkInitListener
{
    void OnFrameworkInit();
}

public interface IFrameworkShutdownListener
{
    void OnFrameworkShutdown();
}
```

#### Players

```csharp
public interface IPlayerConnectedListener
{
    void OnPlayerConnected( HexPlayerComponent player, Connection connection );
}

public interface IPlayerDisconnectedListener
{
    void OnPlayerDisconnected( HexPlayerComponent player, Connection connection );
}

public interface IPlayerSpawnListener
{
    Vector3 GetSpawnPosition( Connection connection, Vector3 currentPosition );
}
```

#### Characters

```csharp
public interface ICanCharacterCreate
{
    bool CanCharacterCreate( HexPlayerComponent player, HexCharacterData data );
}

public interface ICharacterCreatedListener
{
    void OnCharacterCreated( HexPlayerComponent player, HexCharacter character );
}

public interface ICharacterLoadedListener
{
    void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character );
}

public interface ICharacterUnloadedListener
{
    void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character );
}
```

#### Chat

```csharp
public interface ICanSendChatMessage
{
    bool CanSendChatMessage( HexPlayerComponent player, string message );
}

public interface IChatMessageListener
{
    void OnChatMessage( HexPlayerComponent player, string message, IChatClass chatClass );
}

public interface IChatMessageReceivedListener
{
    void OnChatMessageReceived( string playerName, string message, Color color );
}
```

#### Commands

```csharp
public interface ICanRunCommandListener
{
    bool CanRunCommand( HexPlayerComponent player, HexCommand command );
}
```

#### Currency

```csharp
public interface ICanMoneyChangeListener
{
    bool CanMoneyChange( HexCharacter character, int oldAmount, int newAmount );
}

public interface IMoneyChangedListener
{
    void OnMoneyChanged( HexCharacter character, int oldAmount, int newAmount );
}
```

#### Permissions

```csharp
public interface IPermissionCheckListener
{
    bool CanPerformAction( HexCharacter character, string permission );
}
```

#### Doors

```csharp
public interface ICanUseDoorListener
{
    bool CanUseDoor( HexPlayerComponent player, DoorComponent door );
}

public interface IDoorUsedListener
{
    void OnDoorUsed( HexPlayerComponent player, DoorComponent door );
}

public interface IDoorOwnerChangedListener
{
    void OnDoorOwnerChanged( DoorComponent door, string oldOwner, string newOwner );
}
```

#### Storage

```csharp
public interface ICanOpenStorageListener
{
    bool CanOpenStorage( HexPlayerComponent player, StorageComponent storage );
}

public interface IStorageOpenedListener
{
    void OnStorageOpened( HexPlayerComponent player, StorageComponent storage );
}

public interface IStorageClosedListener
{
    void OnStorageClosed( HexPlayerComponent player, StorageComponent storage );
}
```

#### Vendors

```csharp
public interface ICanBuyItemListener
{
    bool CanBuyItem( HexPlayerComponent player, VendorComponent vendor, string itemDefId );
}

public interface ICanSellItemListener
{
    bool CanSellItem( HexPlayerComponent player, VendorComponent vendor, ItemInstance item );
}

public interface IItemBoughtListener
{
    void OnItemBought( HexPlayerComponent player, VendorComponent vendor, ItemInstance item );
}

public interface IItemSoldListener
{
    void OnItemSold( HexPlayerComponent player, VendorComponent vendor, ItemInstance item );
}

public interface IVendorOpenedListener
{
    void OnVendorOpened( HexPlayerComponent player, VendorComponent vendor );
}
```

#### UI

```csharp
public interface IDeathScreenRespawnListener
{
    void OnRespawnRequested( HexPlayerComponent player );
}

public interface IChatFocusRequestListener
{
    void OnChatFocusRequested();
}
```

#### Logging

```csharp
public interface ILogListener
{
    void OnLog( LogEntry entry );
}
```

---

## 17. Logging

Hexagon includes a server-side logging system for tracking player actions and system events. Logs are date-partitioned into separate database collections for efficient storage and querying.

### Writing Logs

```csharp
// Log with a player reference
HexLog.Add( LogType.Chat, player, "said something in IC" );
HexLog.Add( LogType.Admin, adminPlayer, "banned character xyz" );
HexLog.Add( LogType.Item, player, "picked up Pistol" );

// Log a system event (no player)
HexLog.Add( LogType.System, "Server started" );
```

### Log Types

| Type | Use Case |
|---|---|
| `Chat` | Chat messages. |
| `Command` | Command execution. |
| `Item` | Item creation, transfer, destruction. |
| `Character` | Character creation, loading, deletion. |
| `Door` | Door lock/unlock, ownership changes. |
| `Vendor` | Buy/sell transactions. |
| `Money` | Currency transfers. |
| `Admin` | Administrative actions. |
| `System` | Framework events, errors. |

### Querying Logs

```csharp
// All logs for today
List<LogEntry> todayLogs = HexLog.GetLogs( DateTime.UtcNow );

// Filtered by type
List<LogEntry> chatLogs = HexLog.GetLogs( DateTime.UtcNow, LogType.Chat );

// Filtered by player
List<LogEntry> playerLogs = HexLog.GetLogsForPlayer( DateTime.UtcNow, steamId );
```

### LogEntry Structure

```csharp
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogType Type { get; set; }
    public ulong SteamId { get; set; }
    public string PlayerName { get; set; }
    public string Message { get; set; }
}
```

### Real-Time Log Monitoring

Implement `ILogListener` on a Component to receive log entries as they are created:

```csharp
public class AdminLogMonitor : Component, ILogListener
{
    public void OnLog( LogEntry entry )
    {
        if ( entry.Type == LogType.Admin )
        {
            // Notify online admins, write to external system, etc.
        }
    }
}
```

---

## 18. Complete Example

This section walks through the **Skeleton Schema**, the reference implementation that ships with Hexagon. It demonstrates how all the pieces connect.

### SkeletonCharacter.cs -- Character Data

```csharp
namespace Hexagon.Schema;

public class SkeletonCharacter : HexCharacterData
{
    [CharVar( Default = "John Doe", MinLength = 3, MaxLength = 64, Order = 1, ShowInCreation = true )]
    public string Name { get; set; }

    [CharVar( Default = "A mysterious stranger.", MinLength = 16, MaxLength = 512, Order = 2, ShowInCreation = true )]
    public string Description { get; set; }

    [CharVar( Order = 3, ShowInCreation = true )]
    public string Model { get; set; }

    [CharVar( Local = true, Default = 0 )]
    public int Money { get; set; }
}
```

**What this does:**
- `Name` and `Description` are visible to all players (not `Local`), validated by length, and appear in the character creation form.
- `Model` appears in creation so the player can pick a citizen model.
- `Money` is `Local = true`, so only the owning player sees it (other players cannot see your balance).

### SkeletonPlugin.cs -- Plugin Registration

```csharp
namespace Hexagon.Schema;

[HexPlugin( "Skeleton Schema", Description = "Reference RP schema demonstrating all Hexagon features", Author = "Hexagon", Version = "1.0", Priority = 50 )]
public class SkeletonPlugin : IHexPlugin
{
    public void OnPluginLoaded()
    {
        RegisterFactions();
        RegisterClasses();
        RegisterItems();
        RegisterConfigs();

        // Add SkeletonHooks component to the framework GO for event hooks.
        // Must be a Component so Scene.GetAll<T>() discovers it for listener interfaces.
        var framework = HexagonFramework.Instance;
        if ( framework != null )
        {
            framework.GameObject.GetOrAddComponent<SkeletonHooks>();
        }
    }

    private void RegisterFactions()
    {
        var citizens = TypeLibrary.Create<FactionDefinition>();
        citizens.UniqueId = "citizen";
        citizens.Name = "Citizens";
        citizens.Description = "Regular citizens of the city.";
        citizens.IsDefault = true;
        citizens.MaxPlayers = 0;
        citizens.Color = new Color( 0.29f, 0.565f, 0.886f );
        citizens.StartingMoney = 100;
        citizens.Order = 1;
        FactionManager.Register( citizens );

        var police = TypeLibrary.Create<FactionDefinition>();
        police.UniqueId = "police";
        police.Name = "Civil Protection";
        police.Description = "Law enforcement officers protecting the city.";
        police.IsDefault = false;
        police.MaxPlayers = 4;
        police.Color = new Color( 0.886f, 0.29f, 0.29f );
        police.StartingMoney = 200;
        police.Order = 2;
        FactionManager.Register( police );
    }

    private void RegisterClasses()
    {
        var civilian = TypeLibrary.Create<ClassDefinition>();
        civilian.UniqueId = "civilian";
        civilian.Name = "Civilian";
        civilian.Description = "An ordinary citizen.";
        civilian.FactionId = "citizen";
        civilian.MaxPlayers = 0;
        civilian.Order = 1;
        FactionManager.RegisterClass( civilian );

        var officer = TypeLibrary.Create<ClassDefinition>();
        officer.UniqueId = "officer";
        officer.Name = "Officer";
        officer.Description = "A Civil Protection officer.";
        officer.FactionId = "police";
        officer.MaxPlayers = 0;
        officer.Order = 1;
        FactionManager.RegisterClass( officer );

        var chief = TypeLibrary.Create<ClassDefinition>();
        chief.UniqueId = "chief";
        chief.Name = "Chief";
        chief.Description = "The Chief of Civil Protection.";
        chief.FactionId = "police";
        chief.MaxPlayers = 1;
        chief.Order = 2;
        FactionManager.RegisterClass( chief );
    }

    private void RegisterItems()
    {
        var pistol = TypeLibrary.Create<WeaponItemDef>();
        pistol.UniqueId = "weapon_pistol";
        pistol.DisplayName = "Pistol";
        pistol.Description = "A standard 9mm pistol.";
        pistol.Width = 1;
        pistol.Height = 2;
        pistol.Category = "Weapons";
        pistol.ClipSize = 12;
        pistol.AmmoType = "9mm";
        ItemManager.Register( pistol );

        var baton = TypeLibrary.Create<WeaponItemDef>();
        baton.UniqueId = "weapon_baton";
        baton.DisplayName = "Baton";
        baton.Description = "A standard-issue police baton.";
        baton.Width = 1;
        baton.Height = 2;
        baton.Category = "Weapons";
        baton.ClipSize = 0;
        baton.AmmoType = "";
        ItemManager.Register( baton );

        var cash = TypeLibrary.Create<CurrencyItemDef>();
        cash.UniqueId = "money_cash";
        cash.DisplayName = "Cash";
        cash.Description = "A stack of bills.";
        cash.Width = 1;
        cash.Height = 1;
        cash.Category = "Currency";
        cash.DefaultAmount = 100;
        ItemManager.Register( cash );

        var backpack = TypeLibrary.Create<BagItemDef>();
        backpack.UniqueId = "bag_backpack";
        backpack.DisplayName = "Backpack";
        backpack.Description = "A sturdy backpack for extra storage.";
        backpack.Width = 1;
        backpack.Height = 2;
        backpack.Category = "Storage";
        backpack.BagWidth = 6;
        backpack.BagHeight = 4;
        ItemManager.Register( backpack );

        var policeUniform = TypeLibrary.Create<OutfitItemDef>();
        policeUniform.UniqueId = "outfit_police";
        policeUniform.DisplayName = "Police Uniform";
        policeUniform.Description = "A standard Civil Protection uniform.";
        policeUniform.Width = 1;
        policeUniform.Height = 2;
        policeUniform.Category = "Clothing";
        policeUniform.Slot = "torso";
        ItemManager.Register( policeUniform );

        var ammo = TypeLibrary.Create<AmmoItemDef>();
        ammo.UniqueId = "ammo_9mm";
        ammo.DisplayName = "9mm Ammo";
        ammo.Description = "A box of 9mm rounds.";
        ammo.Width = 1;
        ammo.Height = 1;
        ammo.Category = "Ammo";
        ammo.AmmoType = "9mm";
        ammo.AmmoAmount = 12;
        ItemManager.Register( ammo );
    }

    private void RegisterConfigs()
    {
        Config.HexConfig.Add( "gameplay.walkSpeed", 200f, "Default walk speed", "Gameplay" );
        Config.HexConfig.Add( "gameplay.runSpeed", 320f, "Default run speed", "Gameplay" );
    }
}
```

### SkeletonHooks -- Event Handling

```csharp
namespace Hexagon.Schema;

public class SkeletonHooks : Component,
    ICharacterCreatedListener,
    ICharacterLoadedListener,
    IDeathScreenRespawnListener
{
    // --- Character Created ---

    public void OnCharacterCreated( HexPlayerComponent player, HexCharacter character )
    {
        // Create default inventory for the new character
        var inventory = InventoryManager.CreateDefault( character.Id );

        // Give starting cash
        var cash = ItemManager.CreateInstance( "money_cash", character.Id );
        if ( cash != null )
            inventory.Add( cash );

        // Police faction gets extra starting gear
        if ( character.Data.Faction == "police" )
        {
            var baton = ItemManager.CreateInstance( "weapon_baton", character.Id );
            if ( baton != null )
                inventory.Add( baton );

            var uniform = ItemManager.CreateInstance( "outfit_police", character.Id );
            if ( uniform != null )
                inventory.Add( uniform );
        }
    }

    // --- Character Loaded ---

    public void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character )
    {
        // Apply movement speeds from config
        var walkSpeed = Config.HexConfig.Get<float>( "gameplay.walkSpeed", 200f );
        var runSpeed = Config.HexConfig.Get<float>( "gameplay.runSpeed", 320f );

        character.SetData( "walkSpeed", walkSpeed );
        character.SetData( "runSpeed", runSpeed );

        // Load and sync inventory to the player
        var inventories = InventoryManager.LoadForCharacter( character.Data.Id );
        foreach ( var inv in inventories )
        {
            inv.AddReceiver( player.Connection );
        }
    }

    // --- Death Screen Respawn ---

    public void OnRespawnRequested( HexPlayerComponent player )
    {
        ServerRespawn();
    }

    [Rpc.Host]
    private void ServerRespawn()
    {
        var caller = Rpc.Caller;
        if ( caller == null ) return;

        var player = HexGameManager.GetPlayer( caller );
        if ( player == null || !player.IsDead ) return;

        player.IsDead = false;

        // Teleport to spawn position
        var spawnPos = new Vector3( 0, 0, 100 );
        foreach ( var gm in Scene.GetAll<HexGameManager>() )
        {
            spawnPos = gm.SpawnPosition;
            break;
        }

        player.GameObject.WorldPosition = spawnPos;
    }
}
```

### How It All Connects

1. **SkeletonCharacter** defines four CharVars: `Name`, `Description`, `Model`, and `Money`. The first three appear in the character creation UI (`ShowInCreation = true`). `Money` is `Local = true` so only the owning player sees it.

2. **SkeletonPlugin** is discovered by `PluginManager` via the `[HexPlugin]` attribute. At priority 50, it loads before default-priority plugins. During `OnPluginLoaded()`, it:
   - Registers two factions (Citizens and Civil Protection) with `FactionManager`.
   - Registers three classes (Civilian, Officer, Chief) linked to those factions.
   - Registers six item definitions covering all built-in item types.
   - Registers two config values for movement speed.
   - Attaches `SkeletonHooks` to the framework GameObject for event handling.

3. **SkeletonHooks** implements three listener interfaces:
   - `ICharacterCreatedListener` -- gives new characters a default inventory and starting items. Police faction characters also receive a baton and uniform.
   - `ICharacterLoadedListener` -- applies config-based movement speeds and syncs inventory to the player's client by adding them as a receiver.
   - `IDeathScreenRespawnListener` -- handles the respawn button from the death screen by sending an `[Rpc.Host]` call to the server, which resets the dead state and teleports the player to the spawn point.

This pattern -- **data class + plugin + hooks component** -- is the standard structure for any Hexagon schema. Scale it up by adding more factions, items, commands, chat classes, and hook implementations as your gamemode requires.
