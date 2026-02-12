# NutScript: Complete Technical Reference

## 1. What Is NutScript?

NutScript is a free, open-source, DRM-free **roleplay framework** (gamemode base) for Garry's Mod, written in Lua (GLua). Created by **Chessnut** (brianhang) and **Black Tea** (rebel1324), first announced December 27, 2013. Designed as a free alternative to Clockwork ($30-$100 with paid developer keys).

Provides: character creation/persistence, grid-based inventories, factions/classes, item system, chat channels (IC/OOC/whisper/yell), currency, attributes, flag permissions, config system, commands, door ownership, logging, vendors.

Built around a **schema + plugin architecture**: core framework = engine, schemas = gamemode themes, plugins = modular features.

**Repos:** `github.com/Chessnut/NutScript`, `github.com/rebel1324/NutScript`, `github.com/NutScript/NutScript` (1.2 community)

## 2. Architecture

### Global Table: `nut`
19 core libraries: `nut.anim`, `nut.attribs`, `nut.bar`, `nut.char`, `nut.chat`, `nut.command`, `nut.config`, `nut.currency`, `nut.data`, `nut.date`, `nut.db`, `nut.faction`, `nut.flag`, `nut.item`, `nut.lang`, `nut.log`, `nut.notice`, `nut.plugin`, `nut.util`

3 primary classes: Character, Inventory, Item

### Schema Structure
```
sample/
  gamemode/
    init.lua              -- DeriveGamemode("nutscript") + nut.schema.Init()
  schema/
    sh_schema.lua         -- Core schema definition (REQUIRED)
    factions/sh_*.lua     -- Faction definitions (REQUIRED)
    classes/sh_*.lua      -- Class definitions
    items/                -- Item definitions
      base/sh_*.lua       -- Item base classes
      <basename>/sh_*.lua -- Items inheriting from bases
    attributes/           -- Attribute definitions
  plugins/                -- Schema-specific plugins
```

### Plugin Structure
```
myplugin/
  sh_plugin.lua           -- Plugin definition (REQUIRED)
  sv_plugin.lua           -- Server-side (optional)
  cl_plugin.lua           -- Client-side (optional)
  items/                  -- Plugin-specific items
  entities/               -- Plugin-specific entities
```

## 3. Character System

### Registered Variable Pattern
```lua
nut.char.registerVar("bar", {
    field = "bar_field",       -- DB column name
    default = "",
    index = 5,                 -- Display order in char creation
    onValidate = function(value, data) end,
    onSet = function(character, value) end,
    bNoDisplay = false,
    noReplication = false,
})
-- Auto-generates: character:setBar(value) and character:getBar()
```

Built-in vars: Name, Desc, Model, Faction, Class, Money, Attribs, Flags, Data

### Two-Tier Data Storage
- **Permanent** (`setData`/`getData`): Persisted to DB + synced to clients
- **Temporary** (`setVar`/`getVar`): Networked but NOT saved to DB

### Database Tables
- `nut_characters` - Character records
- `nut_items` - Item instances
- `nut_inventories` - Inventory records
- Backends: SQLite (default) or MySQL

## 4. Item System

### Class vs Instance
- **Item definitions** (templates) in `nut.item.list`
- **Item instances** (unique DB-backed objects) in `nut.item.instances`

### Item Bases (Inheritance)
Bases in `items/base/`, derived items in `items/<basename>/`

### Item Functions (Context Menu)
```lua
ITEM.functions.Use = {
    name = "Drink",
    onRun = function(item) end,
    shouldDisplay = function(itemTable, data, entity) return true end
}
```

## 5. Networking
Uses **NetStream** library (wrapper around GMod's `net` library) with **pON** serialization. Named message passing with auto chunking for large payloads.

## 6. Factions, Classes, Chat, Commands, Config, Flags, Currency, Inventory
All use declarative file-based registration with convention-over-configuration auto-loading.

## 7. 50+ Hooks
`Can*` pattern for permissions, `On*`/`Post*` for reactions. Covers character lifecycle, items, doors, chat, plugins, combat, UI.
