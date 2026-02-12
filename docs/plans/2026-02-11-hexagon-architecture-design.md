# Hexagon Architecture Design

**Date**: 2026-02-11
**Status**: Approved via brainstorming session
**Updated**: 2026-02-12 (post-audit against s&box MIT source at D:\sbox-public)

## Context

There is no NutScript/Helix equivalent for s&box. Every existing s&box RP project is DarkRP-style casual RP. Hexagon fills this gap as a schema-based roleplay framework - the spiritual successor to NutScript and Helix, built natively for s&box's C# 14 / .NET 10 / Scene+Component architecture.

**Target audience**: Schema developers building themed RP gamemodes (HL2RP, StarWarsRP, MedievalRP, etc.)

---

## Key Decisions

| Decision | Choice |
|---|---|
| Project type | Separate s&box Library (schemas reference it) |
| Scope | Full Helix parity (~20 systems) |
| Hook/event system | C# Events + Interfaces |
| Character variables | Attribute-decorated properties (`[CharVar]`) |
| Item definitions | `[AssetType]` GameResource assets (`.item` files) with virtual methods for behavior |
| Persistence | FileSystem.Data + in-memory cache (custom DatabaseManager) |
| Plugin system | Both s&box addon packages AND in-project plugin folders |
| UI | Full default Razor panels (overridable by schema devs) |
| Reference schema | Yes - "skeleton" schema alongside framework |

---

## Project Structure

```
hexagon/                              # s&box Library (Type: "library")
  Code/
    Hexagon/
      Core/
        HexagonFramework.cs           # Singleton Component, bootstrap, lifecycle
        HexEvents.cs                  # Static event bus
        PluginManager.cs              # Plugin discovery and loading
        IHexPlugin.cs                 # Plugin interface + [HexPlugin] attribute
      Characters/
        HexCharacterData.cs           # Abstract base for character data models
        HexCharacter.cs               # Runtime character wrapper (methods, dirty tracking)
        HexPlayerComponent.cs         # Component on player GameObject ([Sync] props)
        CharacterManager.cs           # CRUD operations, save/load, character list
        CharVarAttribute.cs           # [CharVar] attribute definition
      Items/
        ItemDefinition.cs             # GameResource base for item types
        ItemInstance.cs               # Runtime item instance (unique ID, per-instance data)
        ItemManager.cs                # Registration, instantiation, spawning
        Bases/
          WeaponItemDef.cs            # Weapon item base (equip/unequip, ammo)
          BagItemDef.cs               # Bag item base (nested inventory)
          OutfitItemDef.cs            # Outfit item base (model/bodygroup swap)
          AmmoItemDef.cs              # Ammo item base
          CurrencyItemDef.cs          # Currency item base (split/merge)
      Inventory/
        HexInventory.cs              # Grid-based inventory (width x height, slot math, receiver system)
        InventoryManager.cs          # Create, restore, receiver management
      Factions/
        FactionDefinition.cs         # GameResource for factions
        ClassDefinition.cs           # GameResource for classes within factions
        FactionManager.cs            # Registration, membership, hierarchy
      Chat/
        ChatManager.cs               # Register chat classes, parse prefixes, route
        IChatClass.cs                # Interface for chat classes
        BuiltInChats.cs              # IC, OOC, whisper, yell, me, it, roll, looc
      Commands/
        HexCommand.cs                # Command definition (args, permissions, handler)
        CommandManager.cs            # Registration, parsing, execution
        CommandArg.cs                # Typed argument definitions
        BuiltInCommands.cs           # charsetname, roll, givemoney, etc.
      Config/
        HexConfig.cs                 # Server config system (Add, Set, Get, Save, Load)
        DefaultConfigs.cs            # Built-in config registrations
        # Note: Client options use [ConVar("hex.opt.name", Saved = true)] — no custom HexOption needed
      Permissions/
        FlagSystem.cs                # Single-character flag permissions
        PermissionManager.cs         # HasPermission checks, flag grant/revoke
      Currency/
        CurrencyManager.cs           # Set symbol/names, format, spawn physical money
      Attributes/
        AttributeDefinition.cs       # GameResource for character attributes
        AttributeManager.cs          # Registration, boost system
      Storage/
        StorageComponent.cs          # Persistent container Component (implements IPressable)
        DoorComponent.cs             # Door ownership/locking Component (implements IPressable)
      Vendors/
        VendorComponent.cs           # NPC vendor Component (implements IPressable)
      Logging/
        HexLog.cs                    # Log types, Add, file handler
      Persistence/
        DatabaseManager.cs           # FileSystem.Data + in-memory cache, JSON persistence
      UI/
        Panels/
          CharacterCreate.razor      # Character creation panel
          CharacterSelect.razor      # Character selection/loading
          InventoryPanel.razor       # Grid inventory display
          HudPanel.razor             # Main HUD (health, name, money)
          ChatPanel.razor            # Chat input + history
          Scoreboard.razor           # Player list
          ConfigMenu.razor           # Admin config panel
          DeathScreen.razor          # Respawn screen
          Tooltip.razor              # Entity/item tooltips
        Styles/
          hexagon.scss               # Base framework styles
  Assets/
    ui/                              # Compiled SCSS assets

hexagon-skeleton/                    # s&box Game (Type: "game"), references hexagon
  Code/
    Schema/
      SkeletonSchema.cs              # [HexPlugin] schema entry point
      Characters/
        SkeletonCharacter.cs         # Character data with Name, Desc, Model, Money
      Factions/                      # 1-2 example factions (.faction assets)
      Items/                         # Basic example items (.item assets)
  Assets/
    scenes/minimal.scene
    prefabs/
    items/                           # .item GameResource files
    factions/                        # .faction GameResource files
```

---

## Core Systems Design

### 1. HexagonFramework (Singleton Bootstrap)

```csharp
public sealed class HexagonFramework : Component
{
    public static HexagonFramework Instance { get; private set; }

    protected override void OnStart()
    {
        Instance = this;
        PluginManager.DiscoverAndLoad();
        HexEvents.Fire<IFrameworkInitListener>(x => x.OnFrameworkInit());
        DatabaseManager.Initialize();
        CharacterManager.Initialize();
        ItemManager.Initialize();
        FactionManager.Initialize();
        ChatManager.Initialize();
        CommandManager.Initialize();
        ConfigManager.Initialize();
    }
}
```

### 2. Event System (Dual Mechanism)

**Interface-based** (for Components in scene — uses `Scene.GetAll<T>()` which indexes by interface):
```csharp
// Framework defines
public interface ICharacterLoadedListener
{
    void OnCharacterLoaded(HexPlayerComponent player, HexCharacter character);
}

public interface ICanCharacterCreate
{
    bool CanCharacterCreate(HexPlayerComponent player, HexCharacterData data);
}

// Schema dev implements on their Component
public class MySchemaManager : Component, ICharacterLoadedListener
{
    public void OnCharacterLoaded(HexPlayerComponent player, HexCharacter character)
    {
        // Custom logic when character loads
    }
}

// Framework calls via Scene discovery
HexEvents.Fire<ICharacterLoadedListener>(x => x.OnCharacterLoaded(player, character));

// Can-hooks: any returning false blocks
bool allowed = HexEvents.CanAll<ICanCharacterCreate>(x => x.CanCharacterCreate(player, data));
```

**Static events** (for non-Component code / plugins):
```csharp
HexEvents.OnCharacterLoaded += (player, character) => { ... };
```

### 3. Character System (Three Layers)

**Layer 1 - Data Model** (schema-defined):
```csharp
public partial class SkeletonCharacter : HexCharacterData
{
    [CharVar(Default = "John Doe")]
    public string Name { get; set; }

    [CharVar(Default = "A mysterious stranger.", MaxLength = 512)]
    public string Description { get; set; }

    [CharVar]
    public string Model { get; set; }

    [CharVar(Local = true)]
    public int Money { get; set; }

    [CharVar(Local = true)]
    public string Flags { get; set; }

    [CharVar(Local = true, NoNetworking = true)]
    public Dictionary<string, object> Data { get; set; }
}
```

**Layer 2 - Runtime Wrapper** (`HexCharacter`):
- Wraps data with methods: `Save()`, `Sync()`, `HasMoney()`, `GiveMoney()`, `TakeMoney()`, `GetInventory()`, `HasFlag()`, `GiveFlag()`, `TakeFlag()`, `GetAttribute()`, `UpdateAttribute()`, `AddBoost()`, `RemoveBoost()`, `JoinClass()`, `Kick()`, `Ban()`
- Tracks dirty fields for efficient persistence
- Auto-save on configurable interval + disconnect

**Layer 3 - Network Component** (`HexPlayerComponent`):
- Attached to player's GameObject
- `[Sync]` properties for public data: character name, model, faction (all players see)
- `[Rpc.Owner]` for private data: money, flags, attributes (only owner sees)
- `IsProxy` check: only owner runs input/movement logic

**Lifecycle flow**:
1. Player connects → `INetworkListener.OnActive` fires in `HexGameManager`
2. `HexGameManager` spawns PlayerPrefab, attaches `HexPlayerComponent`
3. `CharacterManager.OnPlayerConnected` → loads character list from DatabaseManager
4. Auto-loads most recent character (or awaits character creation UI)
5. `HexCharacter` created, `HexPlayerComponent` configured with `[Sync]` data
6. `ICharacterLoadedListener.OnCharacterLoaded` fires
7. Player spawns with correct model/faction/inventory

### 4. Item System

**ItemDefinition** (GameResource, the template):
```csharp
[AssetType(Name = "Item", Extension = "item")]
public class ItemDefinition : GameResource
{
    [Property] public string UniqueId { get; set; }
    [Property] public string DisplayName { get; set; }
    [Property] public string Description { get; set; }
    [Property] public Model WorldModel { get; set; }
    [Property] public int Width { get; set; } = 1;
    [Property] public int Height { get; set; } = 1;
    [Property] public string Category { get; set; }
    [Property] public int MaxStack { get; set; } = 1;
    [Property] public bool CanDrop { get; set; } = true;

    // Context menu actions (like Helix's ITEM.functions)
    public virtual Dictionary<string, ItemAction> GetActions() => new();

    // Lifecycle hooks
    public virtual bool OnUse(HexPlayerComponent player, ItemInstance item) => false;
    public virtual bool OnCanUse(HexPlayerComponent player, ItemInstance item) => true;
    public virtual void OnEquip(HexPlayerComponent player, ItemInstance item) { }
    public virtual void OnUnequip(HexPlayerComponent player, ItemInstance item) { }
    public virtual void OnDrop(HexPlayerComponent player, ItemInstance item) { }
    public virtual void OnPickup(HexPlayerComponent player, ItemInstance item) { }
    public virtual void OnTransferred(ItemInstance item, HexInventory from, HexInventory to) { }
    public virtual void OnInstanced(ItemInstance item) { }
    public virtual void OnRemoved(ItemInstance item) { }

    // Auto-registers via PostLoad/PostReload
}
```

**ItemInstance** (runtime, persisted):
```csharp
public class ItemInstance
{
    public string Id { get; set; }                       // Unique GUID string
    public string DefinitionId { get; set; }             // Reference to ItemDefinition
    public string InventoryId { get; set; }              // Which inventory
    public int X { get; set; }                           // Grid position
    public int Y { get; set; }                           // Grid position
    public Dictionary<string, object> Data { get; set; } // Per-instance custom data
    public string CharacterId { get; set; }              // Owner character

    [JsonIgnore] public ItemDefinition Definition => ItemManager.GetDefinition(DefinitionId);
    public void SetData(string key, object value) { ... }
    public T GetData<T>(string key, T defaultValue) { ... }
}
```

**Built-in Item Bases**:
- `WeaponItemDef` - Equip/unequip weapon, ammo tracking
- `BagItemDef` - Creates nested inventory of configurable size
- `OutfitItemDef` - Changes player model/bodygroups on equip
- `AmmoItemDef` - Gives ammo type on use
- `CurrencyItemDef` - Physical money, split/merge/give

### 5. Grid Inventory

```csharp
public class HexInventory
{
    public string Id { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string OwnerId { get; set; }       // Character ID (string GUID)
    public string Type { get; set; }          // "main", "bag", etc.
    public List<string> ItemIds { get; set; } // Persisted item references

    // Runtime (not persisted)
    [JsonIgnore] private Dictionary<string, ItemInstance> _items;
    [JsonIgnore] private HashSet<Connection> _receivers;

    // Grid operations (AABB collision detection)
    public (int x, int y)? FindEmptySlot(int w, int h) { ... }
    public bool CanItemFit(int x, int y, int w, int h, string excludeItemId = null) { ... }
    public bool Add(ItemInstance item) { ... }         // Auto-find slot
    public bool AddAt(ItemInstance item, int x, int y) { ... }
    public bool Remove(string itemId) { ... }
    public bool Move(string itemId, int newX, int newY) { ... }
    public bool Transfer(string itemId, HexInventory target, int? x, int? y) { ... }
    public bool HasItem(string definitionId) { ... }
    public int CountItem(string definitionId) { ... }

    // Receiver system (selective networking — only certain players see this inventory)
    // Note: [Sync] broadcasts to ALL clients, so we use RPCs + receivers instead
    public void AddReceiver(Connection conn) { ... }
    public void RemoveReceiver(Connection conn) { ... }
}
```

### 6. Chat System

```csharp
public interface IChatClass
{
    string Name { get; }
    string Prefix { get; }          // "/y", "/w", "/me", etc. (empty = default IC)
    float Range { get; }            // 0 = global
    Color Color { get; }
    bool CanHear(HexPlayerComponent speaker, HexPlayerComponent listener);
    bool CanSay(HexPlayerComponent speaker, string message);
    string Format(HexPlayerComponent speaker, string message);
}
```

Built-in: IC (default, ~300 units), Yell (~600), Whisper (~100), Me/It (action text), OOC (global), LOOC (local OOC), Roll (dice), PM, Event (admin).

### 7. Command System

```csharp
CommandManager.Register("charsetname", new HexCommand
{
    Description = "Set a character's name",
    Permission = "admin",
    Arguments = new[] { Arg.Player("target"), Arg.String("name", remainder: true) },
    OnRun = (caller, args) =>
    {
        var target = args.Get<HexPlayerComponent>("target");
        var name = args.Get<string>("name");
        target.Character.SetVar("Name", name);
    }
});
```

Typed arguments: `Arg.Player`, `Arg.String`, `Arg.Number`, `Arg.Character`, `Arg.Bool`, `Arg.Optional(...)`.

### 8. Remaining Systems (Brief)

**Config**: `HexConfig.Add("walkSpeed", 200f, "Default walk speed")` → admin-editable, persisted, synced.

**Flags**: `character.HasFlag('p')`, `character.GiveFlag('p')`. Single-char permissions, simple and effective.

**Currency**: `CurrencyManager.Set("$", "dollar", "dollars")`. Integrates with `[CharVar] Money`.

**Attributes**: `AttributeDefinition` GameResource. `character.GetAttribute("strength")`, `character.AddBoost("item_bonus", "strength", 5)`.

**Storage**: `StorageComponent : Component, IPressable` on world objects. Creates persistent inventory, opens for interacting player. Uses s&box's built-in `IPressable` interface for interaction via `PlayerController`.

**Doors**: `DoorComponent : Component, IPressable`. Ownership by player or faction, lock/unlock, access control. Implements `IPressable` for use-key interaction with tooltips.

**Vendors**: `VendorComponent : Component, IPressable`. Buy/sell lists, admin-configurable, references ItemDefinitions. Implements `IPressable` for NPC interaction.

**Logging**: `HexLog.Add(LogType.Chat, player, "{name} said: {msg}")`. File-based + optional in-game panel.

---

## Networking Strategy

| Data | Mechanism | Visibility |
|---|---|---|
| Character name, model, faction | `[Sync]` on HexPlayerComponent | All players |
| Character money, flags, attributes | `[Rpc.Owner]` push to owner | Owner only |
| Inventory contents | Receiver system (custom RPCs to specific `Connection`s) | Receivers only |
| Item instance data changes | RPCs to inventory receivers | Receivers only |
| Chat messages | `[Rpc.Broadcast]` with range filtering | Hearing range |
| Config values | `[Sync]` on config component (or `NetDictionary<string, string>`) | All players |
| Commands | `[Rpc.Host]` from client to server | Server only |

**Key s&box networking types**:
- `[Sync]` — Auto-replicates property from owner to all clients. Supports primitives, structs, `NetList<T>`, `NetDictionary<TKey, TValue>`.
- `NetList<T>` / `NetDictionary<TKey, TValue>` — Delta-synced collections. Only changed entries are transmitted. Use for data visible to ALL players.
- `[Rpc.Owner]` / `[Rpc.Broadcast]` / `[Rpc.Host]` — Explicit remote procedure calls. Use for targeted or event-driven data.
- `Connection.SendMessage<T>()` — Send typed messages to a specific connection. Alternative to RPCs for selective networking.
- Inventory uses receivers + RPCs (NOT `[Sync]`) because `[Sync]` broadcasts to all clients, and inventories should only be visible to authorized players.

---

## Persistence Strategy (DatabaseManager — FileSystem.Data + In-Memory Cache)

**Implementation**: Custom `DatabaseManager` using `FileSystem.Data.WriteAllText`/`ReadAllText` with `Json.Serialize`/`Json.Deserialize` (s&box wrappers around System.Text.Json). All documents cached in memory for fast reads, written to disk on save.

**Disk structure**: `hexagon/{collection}/{key}.json`

**Collections**:
- `characters` - Character data documents
- `inventories` - Inventory metadata (width, height, owner, type)
- `items` - Item instances (defId, invId, x, y, data)
- `players` - Per-player data (SteamID, play time, settings)
- `config` - Server configuration values
- `doors` - Door ownership per map
- `containers` - Persistent container data per map
- `vendors` - Vendor configuration per map
- `logs` - Activity log entries

**Save strategy**: Auto-save dirty data every N seconds (configurable, default 300). Force-save on player disconnect and server shutdown. Each system (CharacterManager, InventoryManager) tracks its own dirty state.

**Note**: Sandbank is NOT built into s&box. We use FileSystem.Data directly instead, which is the engine's sandboxed file I/O API. System.IO is blocked by the s&box security sandbox.

---

## Build Order

### Phase 1 - Foundation ✅ COMPLETE
1. Convert project to Library type, set up `Hexagon` namespace
2. `HexagonFramework` singleton Component + lifecycle
3. Event system (`HexEvents`, interfaces, static events)
4. Config system (`HexConfig.Add/Set/Get`)
5. Persistence layer (`DatabaseManager` — FileSystem.Data + in-memory cache)

### Phase 2 - Characters ✅ COMPLETE
6. `HexCharacterData` + `[CharVar]` attribute (uses `TypeLibrary` for discovery)
7. `HexCharacter` runtime class with dirty tracking
8. `HexPlayerComponent` with `[Sync]` networking + `[Rpc.Owner]` for private data
9. `CharacterManager` CRUD (create/load/switch/delete)
10. `FactionDefinition` `[AssetType]` GameResource + `FactionManager`
11. `ClassDefinition` `[AssetType]` GameResource + class system
12. `HexGameManager` with `INetworkListener` for player connections

### Phase 3 - Items & Inventory ✅ COMPLETE
13. `ItemDefinition` `[AssetType]` GameResource + `ItemAction`
14. `ItemInstance` + persistence + dirty tracking
15. `ItemManager` — definition registration, instance CRUD
16. `HexInventory` grid math + AABB slot management + receiver system
17. `InventoryManager` — lifecycle, dirty tracking
18. Built-in item bases (weapon, bag, outfit, ammo, currency)

### Phase 4 - Interaction Systems
19. Chat system (classes, prefix routing, range filtering via `[Rpc.Broadcast]`)
20. Command framework (typed args, permissions) — complement s&box's `[ConCmd]` with RP-specific commands
21. Flag/permission system
22. Currency system
23. Attribute system + boosts

### Phase 5 - World Systems
24. Storage/container `Component, IPressable` — uses s&box's built-in interaction system
25. Door ownership `Component, IPressable` — `PlayerController` auto-detects for use-key
26. Vendor `Component, IPressable` — with tooltip support
27. Logging system

### Phase 6 - UI
28. Character creation + selection panels (Razor `PanelComponent`)
29. Grid inventory panel
30. HUD panel (health, name, money, chat) — via `ScreenPanel` root
31. Scoreboard, config menu, death screen, tooltips

### Phase 7 - Polish & Distribution
32. Plugin discovery system (addon packages + in-project)
33. Skeleton schema (example factions, items, basic gameplay)
34. Documentation

---

## s&box Built-in Systems Reference

Systems discovered in the s&box MIT source (D:\sbox-public) that Hexagon leverages or should be aware of:

### IPressable Interface (CRITICAL for Phase 5)
`PlayerController` raycasts for components implementing `IPressable` and provides hover, press, release, and tooltip callbacks. All interactable world objects (doors, storage, vendors) MUST implement this.

```csharp
public interface IPressable
{
    record struct Event(Component Source, Ray? Ray = default);
    record struct Tooltip(string Title, string Icon, string Description, bool Enabled = true, IPressable Pressable = default);

    void Hover(Event e) { }
    void Look(Event e) { }
    void Blur(Event e) { }
    bool Press(Event e);
    bool Pressing(Event e) => true;
    void Release(Event e) { }
    bool CanPress(Event e) => true;
    Tooltip? GetTooltip(Event e) => null;
}
```

### NetList<T> / NetDictionary<TKey, TValue>
Delta-synced network collections that work with `[Sync]`. Only changed entries are transmitted.
- Use for data visible to ALL players (e.g., config values, scoreboard data)
- NOT suitable for inventory (broadcasts to all clients; we need selective receivers)

### ConVar System
`[ConVar("name", Saved = true)]` — Built-in console variable system with persistence via Cookies.
- Flags: `Saved`, `Replicated`, `Admin`, `GameSetting`, `Cheat`
- Client preferences should use `[ConVar("hex.opt.name", Saved = true)]` instead of a custom HexOption system
- Our `HexConfig` is still needed for the plugin override priority system (plugins/schema can override framework defaults)

### ClothingContainer + Dresser
Built-in clothing system for the citizen model. `ClothingContainer` manages clothing items with compatibility rules, `Dresser` component applies them to `SkinnedModelRenderer`.
- Available for schemas using the citizen model
- Our `OutfitItemDef` serves a different purpose: arbitrary model/bodygroup swaps for RP outfits

### Connection API
- `Connection.All` — All active connections
- `Connection.Local` — Local player's connection
- `Connection.Find(Guid)` — Find by ID
- `connection.SendMessage<T>()` — Send typed message to a specific client
- `GameObject.Network.Owner` — Get the owning Connection (NOT `OwnerConnection` which is obsolete)

### Component Lifecycle
```
OnAwake → OnEnabled → OnStart → [OnUpdate/OnFixedUpdate/OnPreRender loop] → OnDisabled → OnDestroy
```
Additional: `OnValidate()`, `OnRefresh()` (after network snapshot), `OnParentChanged()`, `OnTagsChanged()`

### GameObjectSystem
Per-scene singleton systems with lifecycle hooks. Alternative to static manager classes, but our HexagonFramework manages static managers explicitly, which is cleaner for a framework library.

### Blocked APIs
- **System.IO** — Use `FileSystem.Data` instead
- **System.Reflection** (`PropertyInfo.GetValue`, `Type.GetProperties`) — Use `TypeLibrary` and `PropertyDescription` instead
- **Transform.Position** — Obsolete, use `WorldPosition`
- **[GameResource("name", "ext", "desc")]** — Obsolete, use `[AssetType(Name = "...", Extension = "...")]`
- **Network.OwnerConnection** — Obsolete, use `Network.Owner`

---

## Verification

After each phase:
- Launch s&box with the skeleton schema
- Verify the systems built in that phase work in-game
- Test multiplayer (host + at least one client)
- Verify persistence (restart server, check data survived)
- Check networking (data appears correctly on non-owner clients)
