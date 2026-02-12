# Hexagon API Reference

Complete API reference for the Hexagon roleplay framework for s&box (Source 2, C#).

Organized by namespace. Each section lists public classes, their members, and one-line descriptions. For listener/hook interfaces, see the [Listener Interfaces](#listener-interfaces) section at the end.

---

## Table of Contents

- [Hexagon.Core](#hexagoncore)
- [Hexagon.Characters](#hexagoncharacters)
- [Hexagon.Factions](#hexagonfactions)
- [Hexagon.Items](#hexagonitems)
- [Hexagon.Inventory](#hexagoninventory)
- [Hexagon.Chat](#hexagonchat)
- [Hexagon.Commands](#hexagoncommands)
- [Hexagon.Permissions](#hexagonpermissions)
- [Hexagon.Currency](#hexagoncurrency)
- [Hexagon.Attributes](#hexagonattributes)
- [Hexagon.Config](#hexagonconfig)
- [Hexagon.Logging](#hexagonlogging)
- [Hexagon.Doors](#hexagondoors)
- [Hexagon.Storage](#hexagonstorage)
- [Hexagon.Vendors](#hexagonvendors)
- [Hexagon.UI](#hexagonui)
- [Hexagon.Persistence](#hexagonpersistence)
- [Listener Interfaces](#listener-interfaces)

---

## Hexagon.Core

### HexagonFramework : Component

Singleton component that bootstraps and manages the framework lifecycle.

```csharp
static HexagonFramework Instance            // Singleton instance.
static bool IsInitialized                    // Whether framework has finished init.
```

### HexEvents (static)

Central event dispatcher. Routes events to all Components implementing a given listener interface.

```csharp
static void Fire<T>( Action<T> action )                                     // Fire event to all Components implementing T.
static bool CanAll<T>( Func<T, bool> check )                                // Permission hook; all listeners must return true.
static T Reduce<TListener, T>( T initial, Func<TListener, T, T> reducer )   // Chain-transform a value through listeners.
```

### PluginManager (static)

Discovers, loads, and manages Hexagon plugins (both addon packages and in-project plugins).

```csharp
static IReadOnlyList<PluginEntry> Plugins    // All loaded plugins.
static IHexPlugin Get( string name )         // Get plugin by name.
```

### PluginEntry

Metadata wrapper for a loaded plugin.

```csharp
string Name                   // Plugin name.
string Description            // Plugin description.
string Author                 // Plugin author.
string Version                // Plugin version string.
int Priority                  // Load order (lower = first).
TypeDescription Type          // Plugin type info (via TypeLibrary).
IHexPlugin Instance           // Plugin instance.
```

### IHexPlugin (interface)

Interface that all Hexagon plugins implement.

```csharp
void OnPluginLoaded()          // Called during framework init.
void OnPluginUnloaded()        // Called during shutdown (optional; has default impl).
```

### HexPluginAttribute

Attribute applied to plugin classes to declare metadata.

```csharp
string Name                    // Plugin name.
string Description             // Plugin description.
string Author                  // Plugin author.
string Version                 // Plugin version string.
int Priority                   // Default 100; lower = loads first.
```

---

## Hexagon.Characters

### CharacterManager (static)

Server-side manager for character CRUD, CharVar discovery, and active character tracking.

```csharp
static IReadOnlyDictionary<string, CharVarInfo> CharVars             // All CharVar metadata.
static CharVarInfo GetCharVarInfo( string name )                     // Get CharVar metadata by name.
static IEnumerable<CharVarInfo> GetPublicCharVars()                  // Non-local, non-private CharVars.
static IEnumerable<CharVarInfo> GetLocalCharVars()                   // Local (owner-only) CharVars.
static HexCharacter CreateCharacter( HexPlayerComponent player,
    HexCharacterData data )                                          // Create and persist a new character.
static bool LoadCharacter( HexPlayerComponent player,
    string characterId )                                             // Load a character for a player.
static void UnloadCharacter( HexPlayerComponent player )             // Unload active character (saves first).
static bool DeleteCharacter( HexPlayerComponent player,
    string characterId )                                             // Delete a character permanently.
static List<HexCharacterData> GetCharacterList( ulong steamId )      // Get all characters for a Steam ID.
static HexCharacter GetActiveCharacter( string characterId )         // Get an active character by ID.
static IReadOnlyDictionary<string, HexCharacter>
    GetActiveCharacters()                                            // All currently active characters.
static string ValidateCharacterData( HexCharacterData data )         // Validate data against CharVar constraints; returns null if valid.
static HexCharacterData CreateDefaultData()                          // Create instance of schema's data type with defaults.
static void SaveAll()                                                // Save all dirty active characters.
```

### HexCharacterData (abstract class)

Persistent data model for a character. Subclassed by schemas to add CharVar properties.

```csharp
string Id                              // Unique character ID.
ulong SteamId                          // Owning player's Steam ID.
int Slot                               // Character slot number.
string Faction                         // Faction ID.
string Class                           // Class ID.
string Flags                           // Permission flags string.
Dictionary<string, object> Data        // Generic key-value data store.
DateTime CreatedAt                     // Creation timestamp.
DateTime LastPlayedAt                  // Last played timestamp.
bool IsBanned                          // Whether the character is banned.
DateTime? BanExpiry                    // Ban expiry (null = permanent).
```

### HexCharacter

Runtime wrapper around `HexCharacterData`. Provides convenient accessors and dirty-tracking.

```csharp
HexCharacterData Data                          // Underlying persistent data.
HexPlayerComponent Player                      // Attached player (null if character is inactive).
bool IsDirty                                   // Changed since last save.
string Id                                      // Character ID shortcut.
ulong SteamId                                  // Steam ID shortcut.
string Faction                                 // Faction ID shortcut.
string Class                                   // Class ID shortcut.

bool HasFlag( char flag )                      // Check if character has a specific flag.
bool HasFlags( string flags )                  // Check if character has all specified flags.
void GiveFlag( char flag )                     // Add a flag to the character.
void TakeFlag( char flag )                     // Remove a flag from the character.
T GetData<T>( string key, T defaultValue )     // Read from generic data store.
void SetData( string key, object value )       // Write to generic data store.
T GetVar<T>( string name, T defaultValue )     // Read a CharVar by name.
void SetVar( string name, object value )       // Write a CharVar by name.
void SetFaction( string factionId )            // Change faction (clears class).
void SetClass( string classId )                // Change class.
void Ban( TimeSpan? duration )                 // Ban character (null = permanent).
void Unban()                                   // Unban character.
void MarkDirty( string fieldName )             // Mark a field as changed for save.
void Save()                                    // Persist to database immediately.
```

### HexPlayerComponent : Component

Networking bridge between server-side character state and the client. One per connected player.

```csharp
// --- Synced properties (auto-replicated) ---
[Sync] ulong SteamId                          // Player's Steam ID.
[Sync] string DisplayName                     // Player's display name.
[Sync] string CharacterName                   // Active character's name.
[Sync] string CharacterModel                  // Active character's model path.
[Sync] string CharacterDescription            // Active character's description.
[Sync] string FactionId                       // Active character's faction.
[Sync] string ClassId                         // Active character's class.
[Sync] bool HasActiveCharacter                // Whether a character is loaded.
[Sync] bool IsDead                            // Whether the character is dead.

// --- Server-side ---
HexCharacter Character                         // Active character (server only).
Connection Connection                          // Network connection.

// --- Client-side ---
List<CharacterListEntry> ClientCharacterList   // Character list received from server.
event Action OnCharacterListReceived           // Fires when character list arrives.
event Action<bool, string> OnCharacterCreateResult  // Fires with (success, message).
T GetPrivateVar<T>( string name, T defaultValue )   // Read a private CharVar (client).

// --- RPCs (called from client, executed on host) ---
[Rpc.Host] void RequestCharacterList()                    // Request character list from server.
[Rpc.Host] void RequestLoadCharacter( string characterId ) // Request to load a character.
[Rpc.Host] void RequestCreateCharacter( string json )      // Request to create a character.
[Rpc.Host] void RequestDeleteCharacter( string characterId ) // Request to delete a character.
```

### HexGameManager : Component, INetworkListener

Manages player connections, spawning, and the global player registry.

```csharp
[Property] GameObject PlayerPrefab                              // Prefab spawned for each player.
[Property] Vector3 SpawnPosition                                // Default spawn position.
static Dictionary<ulong, HexPlayerComponent> Players            // All connected players.
static HexPlayerComponent GetPlayer( ulong steamId )            // Find player by Steam ID.
static HexPlayerComponent GetPlayer( Connection connection )    // Find player by connection.
```

### CharVarAttribute

Attribute applied to properties on `HexCharacterData` subclasses to define character variables.

```csharp
object Default              // Default value for new characters.
int MinLength               // Minimum string length (validation).
int MaxLength               // Maximum string length (validation).
bool Local                  // Owner-only networking (not broadcast to all).
bool NoNetworking           // Server-only; never sent to clients.
bool ReadOnly               // Prevent changes via SetVar.
int Order                   // Display order in UI.
bool ShowInCreation         // Show this field in character creation UI.
```

---

## Hexagon.Factions

### FactionManager (static)

Manages faction and class registration, lookups, and player count enforcement.

```csharp
static IReadOnlyDictionary<string, FactionDefinition> Factions   // All registered factions.
static IReadOnlyDictionary<string, ClassDefinition> Classes      // All registered classes.
static void Register( FactionDefinition faction )                // Register a faction.
static void RegisterClass( ClassDefinition classDef )            // Register a class.
static FactionDefinition GetFaction( string uniqueId )           // Get faction by ID.
static ClassDefinition GetClass( string uniqueId )               // Get class by ID.
static List<ClassDefinition> GetClassesForFaction( string factionId )  // Classes belonging to a faction.
static List<FactionDefinition> GetDefaultFactions()              // Factions available by default.
static List<FactionDefinition> GetAllFactions()                  // All factions sorted by order.
static int GetFactionPlayerCount( string factionId )             // Count of active players in faction.
static bool CanJoinFaction( string factionId )                   // Check if faction has room.
static int GetClassPlayerCount( string classId )                 // Count of active players in class.
static bool CanJoinClass( string classId )                       // Check if class has room.
```

### FactionDefinition : GameResource

Asset-defined faction template (`.faction` files).

```csharp
string UniqueId              // Unique faction identifier.
string Name                  // Display name.
string Description           // Faction description.
Color Color                  // UI color.
string[] Models              // Available player models.
bool IsDefault               // Available to all players by default.
int MaxPlayers               // Max simultaneous players (0 = unlimited).
int Order                    // Sort order.
int StartingMoney            // Starting money for new characters.
```

### ClassDefinition : GameResource

Asset-defined class template (`.class` files).

```csharp
string UniqueId              // Unique class identifier.
string Name                  // Display name.
string Description           // Class description.
string FactionId             // Parent faction ID.
string ClassModel            // Override model for this class.
int MaxPlayers               // Max simultaneous players (0 = unlimited).
int Order                    // Sort order.
```

---

## Hexagon.Items

### ItemManager (static)

Central registry for item definitions and active item instances.

```csharp
static IReadOnlyDictionary<string, ItemDefinition> Definitions    // All registered definitions.
static IReadOnlyDictionary<string, ItemInstance> Instances         // All active instances in memory.
static void Register( ItemDefinition definition )                 // Register an item definition.
static ItemDefinition GetDefinition( string uniqueId )            // Get definition by ID.
static ItemInstance CreateInstance( string definitionId,
    string characterId, Dictionary<string, object> data )         // Create a new instance and persist it.
static ItemInstance GetInstance( string instanceId )               // Get instance by ID (loads from DB if needed).
static void DestroyInstance( string instanceId )                  // Remove an instance permanently.
static void SaveAll()                                             // Save all dirty instances.
static List<ItemInstance> LoadInstancesForCharacter(
    string characterId )                                          // Load all items for a character.
static List<ItemDefinition> GetDefinitionsByCategory(
    string category )                                             // Filter definitions by category.
static List<string> GetCategories()                               // Get all item categories.
```

### ItemDefinition : GameResource

Asset-defined item template (`.item` files). Subclass to create custom item types.

```csharp
// --- Properties ---
string UniqueId              // Unique item identifier.
string DisplayName           // Display name shown in UI.
string Description           // Item description.
string WorldModel            // Model path for world representation.
int Width                    // Grid width in inventory.
int Height                   // Grid height in inventory.
string Category              // Item category.
int MaxStack                 // Max stack size (1 = no stacking).
bool CanDrop                 // Whether the item can be dropped.
int Order                    // Sort order.

// --- Virtual methods (override in subclasses) ---
List<ItemAction> GetActions()                            // Define context menu actions.
bool OnUse( HexPlayerComponent player, ItemInstance item )       // Called when item is used.
bool OnCanUse( HexPlayerComponent player, ItemInstance item )    // Check if item can be used.
void OnEquip( HexPlayerComponent player, ItemInstance item )     // Called when item is equipped.
void OnUnequip( HexPlayerComponent player, ItemInstance item )   // Called when item is unequipped.
void OnDrop( HexPlayerComponent player, ItemInstance item )      // Called when item is dropped.
void OnPickup( HexPlayerComponent player, ItemInstance item )    // Called when item is picked up.
void OnTransferred( ItemInstance item, HexInventory from,
    HexInventory to )                                            // Called when item moves between inventories.
void OnInstanced( ItemInstance item )                            // Called when a new instance is created.
void OnRemoved( ItemInstance item )                              // Called when an instance is destroyed.
```

### ItemInstance

Runtime instance of an item, backed by database persistence.

```csharp
// --- Properties ---
string Id                              // Unique instance ID.
string DefinitionId                    // Reference to ItemDefinition.UniqueId.
string CharacterId                     // Owning character ID.
string InventoryId                     // Containing inventory ID.
int X                                  // Grid X position in inventory.
int Y                                  // Grid Y position in inventory.
Dictionary<string, object> Data        // Per-instance key-value data.
bool IsDirty                           // Changed since last save.

// --- Computed ---
ItemDefinition Definition              // Resolved definition reference.

// --- Methods ---
T GetData<T>( string key, T defaultValue )   // Read per-instance data.
void SetData( string key, object value )     // Write per-instance data.
void RemoveData( string key )                // Remove per-instance data key.
void MarkDirty()                             // Mark instance for save.
void Save()                                  // Persist to database immediately.
```

### ItemAction

Defines a context-menu action for an item.

```csharp
string Name                                                  // Action display name.
string Icon                                                  // Icon identifier.
Func<HexPlayerComponent, ItemInstance, bool> OnRun           // Execute the action; return true on success.
Func<HexPlayerComponent, ItemInstance, bool> OnCanRun        // Check if action is available.
```

### Built-in Item Bases (Hexagon.Items.Bases)

Pre-built `ItemDefinition` subclasses for common item archetypes.

#### WeaponItemDef : ItemDefinition

```csharp
string AmmoType              // Ammo type identifier.
int ClipSize                 // Magazine capacity.
string WeaponModel           // Weapon view model.
bool TwoHanded               // Whether the weapon is two-handed.

int GetClipAmmo( ItemInstance item )                  // Read current ammo in clip.
void SetClipAmmo( ItemInstance item, int amount )     // Set ammo in clip.
```

#### BagItemDef : ItemDefinition

```csharp
int BagWidth                 // Bag inventory width.
int BagHeight                // Bag inventory height.

HexInventory GetBagInventory( ItemInstance item )                   // Get or create the bag's inventory.
void OpenBag( HexPlayerComponent player, ItemInstance item )        // Open bag UI for player.
void CloseBag( HexPlayerComponent player, ItemInstance item )       // Close bag UI for player.
```

#### OutfitItemDef : ItemDefinition

```csharp
string OutfitModel           // Outfit model path.
string Bodygroups            // Bodygroup overrides.
string Slot                  // Equipment slot.
```

#### CurrencyItemDef : ItemDefinition

```csharp
int DefaultAmount            // Default currency amount on creation.

int GetAmount( ItemInstance item )                    // Read currency amount.
void SetAmount( ItemInstance item, int amount )       // Set currency amount.
static ItemInstance CreateWithAmount( string defId,
    int amount, string charId )                       // Create a currency item with a specific amount.
```

#### AmmoItemDef : ItemDefinition

```csharp
string AmmoType              // Ammo type identifier.
int AmmoAmount               // Amount of ammo per item.
```

---

## Hexagon.Inventory

### InventoryManager (static)

Creates, loads, and manages grid-based inventories.

```csharp
static IReadOnlyDictionary<string, HexInventory> Inventories    // All active inventories in memory.
static HexInventory Create( int width, int height,
    string ownerId, string type )                                // Create and persist a new inventory.
static HexInventory CreateDefault( string ownerId, string type ) // Create with config-defined dimensions.
static HexInventory Get( string inventoryId )                    // Get by ID (loads from DB if needed).
static List<HexInventory> LoadForCharacter( string characterId ) // Load all inventories for a character.
static void Delete( string inventoryId )                         // Delete inventory and its items.
static void SaveAll()                                            // Save all inventories and their items.
static void Unload( string inventoryId )                         // Save and remove from memory.
```

### HexInventory

Grid-based inventory container with network receiver support.

```csharp
// --- Properties ---
string Id                                      // Unique inventory ID.
int Width                                      // Grid width.
int Height                                     // Grid height.
string OwnerId                                 // Owning character or entity ID.
string Type                                    // Inventory type (e.g. "character", "bag", "storage").
List<string> ItemIds                           // IDs of contained items.

// --- Computed ---
IReadOnlyDictionary<string, ItemInstance> Items // Runtime item instances.
bool IsFull                                    // True if no 1x1 slot is available.
int ItemCount                                  // Number of items currently held.

// --- Placement ---
bool CanItemFit( int x, int y, int w, int h,
    string excludeItemId )                     // Check if an item fits at a position.
(int x, int y)? FindEmptySlot( int w, int h ) // Find the first available slot for the given size.

// --- Item operations ---
bool AddAt( ItemInstance item, int x, int y )  // Add item at a specific grid position.
bool Add( ItemInstance item )                  // Add item at first available slot.
bool Remove( string itemId )                   // Remove item from inventory.
bool Move( string itemId, int newX, int newY ) // Move item within this inventory.
bool Transfer( string itemId, HexInventory target,
    int? targetX, int? targetY )               // Transfer item to another inventory.
bool HasItem( string definitionId )            // Check if inventory contains an item by definition.
int CountItem( string definitionId )           // Count items by definition.
ItemInstance FindItem( string definitionId )    // Find first item matching a definition.

// --- Networking ---
void AddReceiver( Connection conn )            // Add a network receiver (client gets updates).
void RemoveReceiver( Connection conn )         // Remove a network receiver.
IReadOnlySet<Connection> GetReceivers()        // Get current network receivers.

// --- Persistence ---
void Save()                                    // Persist inventory to database.
```

### HexInventoryComponent : Component

Singleton component that bridges server inventory state to clients via RPCs. Flushes dirty inventories to receivers each frame.

---

## Hexagon.Chat

### ChatManager (static)

Manages chat class registration, prefix routing, and message processing.

```csharp
static void Register( IChatClass chatClass )            // Register a chat class.
static IChatClass GetChatClass( string name )           // Get chat class by name.
static IChatClass GetChatClassByPrefix( string prefix ) // Get chat class by prefix.
static List<IChatClass> GetAllChatClasses()             // All registered chat classes.
static void ProcessMessage( HexPlayerComponent sender,
    string rawMessage )                                 // Parse prefix, route, and broadcast a message.
```

### IChatClass (interface)

Defines a type of chat (IC, OOC, whisper, yell, etc.).

```csharp
string Name                  // Chat class name (e.g. "IC", "OOC").
string Prefix                // Trigger prefix (e.g. "//" for OOC).
float Range                  // Hearing range in units (0 = global).
Color Color                  // Chat color.
bool CanHear( HexPlayerComponent listener,
    HexPlayerComponent speaker )               // Override hearing logic.
bool CanSay( HexPlayerComponent player )       // Permission check before sending.
string Format( HexPlayerComponent player,
    string message )                           // Format the output message.
```

### HexChatComponent : Component

Singleton networking component for chat RPCs.

```csharp
[Rpc.Host] void SendMessage( string message )                        // Client sends a chat message to server.
[Rpc.Broadcast] void ReceiveMessage( string name, string msg,
    float r, float g, float b )                                      // Server broadcasts to clients (color as 3 floats).
```

---

## Hexagon.Commands

### CommandManager (static)

Registers, parses, and executes slash commands.

```csharp
static void Register( HexCommand command )          // Register a command.
static HexCommand GetCommand( string name )         // Get command by name or alias.
static List<HexCommand> GetAllCommands()            // All registered commands.
static void Execute( HexPlayerComponent player,
    string input )                                  // Parse input and execute the matching command.
```

### HexCommand

Definition of a slash command.

```csharp
string Name                                          // Command name (e.g. "give").
string Description                                   // Help text.
string[] Aliases                                     // Alternative names.
string Permission                                    // Required permission flags (e.g. "a" for admin).
Func<HexPlayerComponent, bool> PermissionFunc        // Custom permission check function.
CommandArg[] Arguments                               // Argument definitions.
Action<HexPlayerComponent, object[]> OnRun           // Execution callback.
```

### CommandArg

Definition of a single command argument.

```csharp
string Name                  // Argument name.
Type Type                    // Expected C# type.
bool Optional                // Whether the argument can be omitted.
object Default               // Default value if omitted.
```

---

## Hexagon.Permissions

### PermissionManager (static)

Flag-based permission system. Flags are single characters stored on each character.

```csharp
static bool HasPermission( HexCharacter character,
    string flags )                                    // Check if character has all specified flags.
static bool HasPermission( HexPlayerComponent player,
    string flags )                                    // Shorthand; checks the player's active character.
static bool IsAdmin( HexCharacter character )         // Has 'a' or 's' flag.
static bool IsSuperAdmin( HexCharacter character )    // Has 's' flag.
```

---

## Hexagon.Currency

### CurrencyManager (static)

Wraps the "Money" CharVar with hooks for schema and plugin interception.

```csharp
static int GetMoney( HexCharacter character )                    // Read current money.
static bool GiveMoney( HexCharacter character, int amount )      // Add money (fires hooks; returns false if blocked).
static bool TakeMoney( HexCharacter character, int amount )      // Remove money (fires hooks; returns false if blocked).
static bool SetMoney( HexCharacter character, int amount )       // Set money directly (fires hooks).
static bool CanAfford( HexCharacter character, int amount )      // Check if character has enough money.
static string Format( int amount )                               // Format with currency symbol from config.
```

---

## Hexagon.Attributes

### AttributeManager (static)

Manages per-character attributes with base values and a named boost system.

```csharp
static float GetAttribute( HexCharacter character,
    string attrId )                                              // Get effective value (base + all boosts).
static void SetAttribute( HexCharacter character,
    string attrId, float value )                                 // Set base value.
static void AddAttribute( HexCharacter character,
    string attrId, float amount )                                // Add to base value.
static void AddBoost( HexCharacter character,
    string attrId, string boostId, float amount )                // Add a named boost.
static void RemoveBoost( HexCharacter character,
    string attrId, string boostId )                              // Remove a named boost.
static void ClearBoosts( HexCharacter character,
    string attrId )                                              // Remove all boosts for an attribute.
static void InitializeCharacter( HexCharacter character )        // Set all attributes to their start values.
```

### AttributeDefinition : GameResource

Asset-defined attribute template (`.attribute` files).

```csharp
string UniqueId              // Unique attribute identifier.
string DisplayName           // Display name.
float MinValue               // Minimum allowed value.
float MaxValue               // Maximum allowed value.
float StartValue             // Value assigned on character init.
```

---

## Hexagon.Config

### HexConfig (static)

Framework-wide configuration system with override persistence.

```csharp
static IReadOnlyDictionary<string, ConfigEntry> Entries          // All registered config entries.
static void Add( string key, object defaultValue,
    string description, string category,
    Action<object, object> onChange )                             // Register a new config entry.
static T Get<T>( string key, T fallback )                        // Read value (override if set, else default).
static void Set( string key, object value )                      // Set an override value.
static void Reset( string key )                                  // Reset to default value.
static void Save()                                               // Persist overrides to disk.
static void Load()                                               // Load overrides from disk.
```

### ConfigEntry

A single configuration entry.

```csharp
string Key                   // Config key.
object DefaultValue          // Default value.
string Description           // Human-readable description.
string Category              // Grouping category.
Type ValueType               // C# type of the value.
Action<object, object> OnChange  // Callback (oldValue, newValue) when changed.
```

---

## Hexagon.Logging

### HexLog (static)

Structured logging system with date-partitioned database collections.

```csharp
static void Add( LogType type, ulong steamId,
    string playerName, string message )                          // Add a log entry.
static List<LogEntry> GetLogs( DateTime date )                   // Get all logs for a date.
static List<LogEntry> GetLogs( DateTime date, LogType type )     // Get logs filtered by type.
static List<LogEntry> GetLogsForPlayer( DateTime date,
    ulong steamId )                                              // Get logs filtered by player.
```

### LogEntry

A single log record.

```csharp
string Id                    // Unique log entry ID.
DateTime Timestamp           // When the event occurred.
LogType Type                 // Log category.
ulong SteamId                // Associated player's Steam ID.
string PlayerName            // Associated player's name.
string Message               // Log message.
```

### LogType (enum)

```csharp
Chat, Command, Item, Character, Door, Vendor, Money, Admin, System
```

---

## Hexagon.Doors

### DoorManager (static)

Manages door registration, persistence, and lookups.

```csharp
static void Register( DoorComponent door )           // Register a door.
static void Unregister( DoorComponent door )         // Unregister a door.
static DoorComponent GetDoor( string doorId )        // Get door by ID.
static List<DoorComponent> GetAllDoors()             // All registered doors.
static void SaveDoor( DoorComponent door )           // Persist a single door's state.
static void SaveAll()                                // Save all doors.
```

### DoorComponent : Component, IPressable

World door with ownership, locking, and faction-based access control.

```csharp
string DoorId                        // Unique door identifier.
[Sync] bool IsLocked                 // Lock state (replicated).
[Sync] bool IsOpen                   // Open state (replicated).
[Sync] string OwnerDisplay           // Display name of owner (replicated).
string OwnerCharacterId              // Owning character ID (server).
string OwnerFactionId                // Owning faction ID (server).
List<string> AccessList              // Additional character IDs with access.
```

Auth hierarchy: SuperAdmin > Admin > Owner > Faction > AccessList.

---

## Hexagon.Storage

### StorageComponent : Component, IPressable

World container that backs to an inventory via `InventoryManager`.

```csharp
string StorageName           // Display name shown in UI.
int Width                    // Inventory grid width.
int Height                   // Inventory grid height.
string InventoryId           // Backing inventory ID.
```

---

## Hexagon.Vendors

### VendorManager (static)

Handles vendor registration and buy/sell transactions.

```csharp
static void Register( VendorComponent vendor )                   // Register a vendor.
static void Unregister( VendorComponent vendor )                 // Unregister a vendor.
static bool BuyItem( HexPlayerComponent player,
    VendorComponent vendor, string itemDefId )                   // Process a purchase (creates item, takes money).
static bool SellItem( HexPlayerComponent player,
    VendorComponent vendor, string itemInstanceId )              // Process a sale (destroys item, gives money).
```

### VendorComponent : Component, IPressable

World vendor NPC with a configurable buy/sell catalog.

```csharp
string VendorName                                    // Display name.
string VendorId                                      // Unique vendor identifier.

void AddItem( string itemDefId, int buyPrice,
    int sellPrice )                                  // Add item to vendor catalog.
void RemoveItem( string itemDefId )                  // Remove item from catalog.
```

---

## Hexagon.UI

### HexUIManager : Component

Client-side UI state machine. Manages panel visibility and state transitions.

```csharp
static HexUIManager Instance                         // Singleton instance.
UIState State                                        // Current UI state.
void SetState( UIState newState )                    // Transition to a new state.
IHexPanel FindPanel( string name )                   // Find a panel by name.
void OpenPanel( string name )                        // Open a panel.
void ClosePanel( string name )                       // Close a panel.
void TogglePanel( string name )                      // Toggle a panel's visibility.
static HexPlayerComponent GetLocalPlayer()           // Get the local player's HexPlayerComponent.
```

### UIState (enum)

```csharp
Loading, CharacterSelect, CharacterCreate, Gameplay, Dead
```

### IHexPanel (interface)

Interface for swappable UI panels. Implement to create custom panels.

```csharp
string PanelName             // Panel identifier (used by OpenPanel/ClosePanel/TogglePanel).
bool IsOpen                  // Current visibility state.
void Open()                  // Show the panel.
void Close()                 // Hide the panel.
```

### Built-in Panels

| Panel | Name String | Description |
|---|---|---|
| CharacterSelect | `"CharacterSelect"` | Character selection screen |
| CharacterCreate | `"CharacterCreate"` | Character creation form |
| HudPanel | `"Hud"` | Main gameplay HUD |
| ChatPanel | `"Chat"` | Chat input and history |
| InventoryPanel | `"Inventory"` | Player inventory grid |
| StoragePanel | `"Storage"` | Storage container view |
| VendorPanel | `"Vendor"` | Vendor buy/sell interface |
| Scoreboard | `"Scoreboard"` | Player list |
| DeathScreen | `"DeathScreen"` | Death overlay with respawn |

---

## Hexagon.Persistence

### DatabaseManager (static)

JSON-based persistence layer using `FileSystem.Data` with in-memory caching.

```csharp
static void Save<T>( string collection, string key, T data )    // Save an object to a collection.
static T Load<T>( string collection, string key )                // Load an object from a collection.
static void Delete( string collection, string key )              // Delete an entry.
static bool Exists( string collection, string key )              // Check if an entry exists.
static List<T> LoadAll<T>( string collection )                   // Load all entries in a collection.
static List<T> Select<T>( string collection,
    Func<T, bool> predicate )                                    // Query with a filter predicate.
static List<string> GetKeys( string collection )                 // List all keys in a collection.
static string NewId()                                            // Generate a unique ID.
```

---

## Listener Interfaces

All listener interfaces are resolved via `HexEvents.Fire<T>()` or `HexEvents.CanAll<T>()`. Implement these on any `Component` in the scene to receive callbacks.

### Framework Lifecycle

```csharp
// Hexagon.Core
interface IFrameworkInitListener
{
    void OnFrameworkInit();                     // Framework has finished initializing all systems.
}

interface IFrameworkShutdownListener
{
    void OnFrameworkShutdown();                 // Framework is shutting down.
}
```

### Player Lifecycle

```csharp
// Hexagon.Characters
interface IPlayerConnectedListener
{
    void OnPlayerConnected( HexPlayerComponent player, Connection connection );
    // Player has fully connected and their GameObject is spawned.
}

interface IPlayerDisconnectedListener
{
    void OnPlayerDisconnected( HexPlayerComponent player, Connection connection );
    // Player has disconnected.
}

interface IPlayerSpawnListener
{
    Vector3 GetSpawnPosition( Connection connection, Vector3 currentPosition );
    // Override spawn position for a connecting player. Return modified position.
}
```

### Character Lifecycle

```csharp
// Hexagon.Characters
interface ICanCharacterCreate
{
    bool CanCharacterCreate( HexPlayerComponent player, HexCharacterData data );
    // Permission hook: return false to block character creation.
}

interface ICharacterCreatedListener
{
    void OnCharacterCreated( HexPlayerComponent player, HexCharacter character );
    // A new character has been created.
}

interface ICharacterLoadedListener
{
    void OnCharacterLoaded( HexPlayerComponent player, HexCharacter character );
    // A character has been loaded for a player.
}

interface ICharacterUnloadedListener
{
    void OnCharacterUnloaded( HexPlayerComponent player, HexCharacter character );
    // A character has been unloaded (player switched or disconnected).
}
```

### Chat

```csharp
// Hexagon.Chat
interface ICanSendChatMessage
{
    bool CanSendChatMessage( HexPlayerComponent sender, IChatClass chatClass, string message );
    // Permission hook: return false to block the message.
}

interface IChatMessageListener
{
    void OnChatMessage( HexPlayerComponent sender, IChatClass chatClass,
        string rawMessage, string formattedMessage );
    // Server-side: fired after a chat message is sent.
}

interface IChatMessageReceivedListener
{
    void OnChatMessageReceived( string senderName, string chatClassName,
        string formattedMessage, Color color );
    // Client-side: fired when a chat message is received.
}
```

### Commands

```csharp
// Hexagon.Commands
interface ICanRunCommandListener
{
    bool CanRunCommand( HexPlayerComponent player, HexCommand command );
    // Permission hook: return false to block command execution.
}
```

### Permissions

```csharp
// Hexagon.Permissions
interface IPermissionCheckListener
{
    bool OnPermissionCheck( HexPlayerComponent player, string permission );
    // Custom permission check beyond simple flags. Return false to deny.
}
```

### Currency

```csharp
// Hexagon.Currency
interface ICanMoneyChangeListener
{
    bool CanMoneyChange( HexCharacter character, int oldAmount, int newAmount, string reason );
    // Permission hook: return false to block the money change.
}

interface IMoneyChangedListener
{
    void OnMoneyChanged( HexCharacter character, int oldAmount, int newAmount, string reason );
    // Fired after a money change has occurred.
}
```

### Attributes

```csharp
// Hexagon.Attributes
interface IAttributeChangedListener
{
    void OnAttributeChanged( HexCharacter character, string attributeId,
        float oldValue, float newValue );
    // Fired when a character's effective attribute value changes.
}
```

### Doors

```csharp
// Hexagon.Doors
interface ICanUseDoorListener
{
    bool CanUseDoor( HexPlayerComponent player, DoorComponent door );
    // Permission hook: return false to block door interaction.
}

interface IDoorUsedListener
{
    void OnDoorUsed( HexPlayerComponent player, DoorComponent door );
    // Fired after a player uses a door (toggle, lock/unlock).
}

interface IDoorOwnerChangedListener
{
    void OnDoorOwnerChanged( DoorComponent door, string oldOwnerId,
        string newOwnerId, bool isFaction );
    // Fired when door ownership changes.
}
```

### Storage

```csharp
// Hexagon.Storage
interface ICanOpenStorageListener
{
    bool CanOpenStorage( HexPlayerComponent player, StorageComponent storage );
    // Permission hook: return false to block opening the container.
}

interface IStorageOpenedListener
{
    void OnStorageOpened( HexPlayerComponent player, StorageComponent storage );
    // Fired when a player opens a storage container.
}

interface IStorageClosedListener
{
    void OnStorageClosed( HexPlayerComponent player, StorageComponent storage );
    // Fired when a player closes a storage container.
}
```

### Vendors

```csharp
// Hexagon.Vendors
interface ICanBuyItemListener
{
    bool CanBuyItem( HexPlayerComponent player, VendorComponent vendor, VendorItem item );
    // Permission hook: return false to block purchase.
}

interface ICanSellItemListener
{
    bool CanSellItem( HexPlayerComponent player, VendorComponent vendor,
        VendorItem item, ItemInstance instance );
    // Permission hook: return false to block sale.
}

interface IItemBoughtListener
{
    void OnItemBought( HexPlayerComponent player, VendorComponent vendor,
        VendorItem item, ItemInstance instance );
    // Fired after a player buys an item.
}

interface IItemSoldListener
{
    void OnItemSold( HexPlayerComponent player, VendorComponent vendor, VendorItem item );
    // Fired after a player sells an item.
}

interface IVendorOpenedListener
{
    void OnVendorOpened( HexPlayerComponent player, VendorComponent vendor );
    // Fired when a player interacts with a vendor.
}
```

### Inventory (Client-side)

```csharp
// Hexagon.Inventory
interface IInventoryUpdatedListener
{
    void OnInventoryUpdated( string inventoryId );
    // Client-side: an inventory snapshot was received or updated.
}

interface IInventoryRemovedListener
{
    void OnInventoryRemoved( string inventoryId );
    // Client-side: an inventory is no longer available (e.g. closed storage).
}

interface IVendorCatalogReceivedListener
{
    void OnVendorCatalogReceived( string vendorId, string vendorName,
        List<VendorCatalogEntry> items );
    // Client-side: vendor catalog data was received.
}

interface IVendorResultListener
{
    void OnVendorResult( bool success, string message );
    // Client-side: result of a vendor buy/sell operation.
}
```

### UI

```csharp
// Hexagon.UI
interface IDeathScreenRespawnListener
{
    void OnRespawnRequested( HexPlayerComponent player );
    // Death screen respawn button was pressed. Schema implements respawn logic.
}

interface IChatFocusRequestListener
{
    void OnChatFocusRequested();
    // Client-side: chat input should be focused (ENTER key pressed).
}
```

### Logging

```csharp
// Hexagon.Logging
interface ILogListener
{
    void OnLog( LogEntry entry );
    // Real-time listener for all log entries.
}
```
