# Helix Framework: Complete Technical Reference

## 1. Overview
- Fork/evolution of NutScript 1.1 by NebulousCloud
- MIT licensed, open source, DRM-free
- GitHub: github.com/NebulousCloud/helix
- Docs: docs.gethelix.co

## 2. Key Differences from NutScript
- Namespace: `nut.*` -> `ix.*`
- Method casing: camelCase -> PascalCase
- `getChar()` -> `GetCharacter()`
- Grid-based inventory (width/height) instead of weight-based
- Redesigned modern UI skin system
- 170+ hooks (vs ~50 in NutScript)
- Plugin Center ecosystem

## 3. Three-Tier Architecture
**Framework** (core systems) -> **Schema** (themed gamemode) -> **Plugins** (modular features)

### Schema Structure
```
myschema/
  gamemode/
    init.lua              -- DeriveGamemode("helix")
    cl_init.lua           -- DeriveGamemode("helix")
  schema/
    sh_schema.lua         -- REQUIRED entry point
    factions/sh_*.lua
    classes/sh_*.lua
    items/
      base/sh_*.lua
      <basename>/sh_*.lua
    attributes/
    languages/
  plugins/
```

### Plugin Types
1. Single-file: `plugins/myplugin.lua`
2. Directory: `plugins/myplugin/sh_plugin.lua`

## 4. Character System
### RegisterVar Pattern
```lua
ix.char.RegisterVar("name", {
    field = "name",
    fieldType = ix.type.string,
    default = "John Doe",
    isLocal = false,        -- If true, only networked to owner
    bNoNetworking = false,  -- If true, server-only
    OnSet = function(character, key, oldValue, newValue) end,
    OnValidate = function(value, payload, client) end
})
```

### Character Methods
GetID(), GetPlayer(), GetName/SetName, GetDescription/SetDescription, GetModel/SetModel, GetMoney/SetMoney, GetFaction/SetFaction, GetClass, JoinClass, GetData/SetData, GetAttribute/SetAttrib/UpdateAttrib, AddBoost/RemoveBoost, GetFlags/HasFlags/GiveFlags/TakeFlags, Ban, Kick, Sync, Save, GetInventory

## 5. Item System
### Grid-Based Inventory
Items have `ITEM.width` and `ITEM.height`, placed at (x,y) in grid

### Item Functions
```lua
ITEM.functions.Use = {
    OnRun = function(item) end,      -- Return false to keep item
    OnCanRun = function(item) end,   -- Permission check
}
```

### Built-in Item Bases
base_ammo, base_bags (nested inventories), base_outfit, base_pacoutfit, base_weapons

## 6. Networking (NetVar System)
### Entity NetVars
```lua
entity:SetNetVar(key, value, receiver)
entity:GetNetVar(key, default)
```

### Character Variable Networking
- `isLocal = false` -> synced to ALL players
- `isLocal = true` -> synced ONLY to owner
- `bNoNetworking = true` -> server-only

### Inventory Receiver System
Only registered receivers see inventory contents.

## 7. 170+ Hooks (Categorized)
Character Lifecycle, Inventory/Item, Faction/Class, Economy, Chat, Player Actions, Persistence, Stamina, UI/Rendering, Framework Lifecycle, Misc

## 8. Core Libraries
ix.char, ix.item, ix.inventory, ix.faction, ix.class, ix.chat, ix.command, ix.config, ix.option, ix.storage, ix.util, ix.lang, ix.anim, ix.act, ix.net, ix.log

## 9. Database
Tables: ix_characters, ix_inventories, ix_items
Backends: SQLite (default) or MySQL via MySQLOO

## 10. Key Design Patterns
1. Framework/Schema/Plugin separation
2. Character is not Player (multiple characters per player)
3. Declarative registration patterns (auto-discovery from directories)
4. Grid-based inventory with nested bags
5. Item class/instance split
6. NetVar with selective replication
7. Receiver-based inventory networking
8. Hook-driven extensibility (Can*/On*/Post*)
9. Automatic persistence
10. File convention system (sh_/sv_/cl_ prefixes)
11. CAMI permission integration
12. Config vs Option separation
