# Hexagon

A roleplay framework for [s&box](https://sbox.game), built as the spiritual successor to [NutScript](https://github.com/NutScript/NutScript) and [Helix](https://github.com/NebulousCloud/helix) for Source 2.

Hexagon is an **s&box Library** — you don't modify it directly. Instead, you create a separate Game project (a "schema") that references Hexagon and extends it with your own characters, items, factions, and game rules.

## Features

- **Characters** — Multi-character system with custom fields via `[CharVar]` attributes, persistent across sessions
- **Items & Inventory** — Grid-based inventories with item definitions (weapons, bags, outfits, ammo, currency) as GameResource assets
- **Factions & Classes** — Data-driven faction/class system with GameResource definitions
- **Chat** — Pluggable chat classes (IC, OOC, whisper, yell, /me, /it, /roll, LOOC) with custom routing
- **Commands** — Typed command system with argument parsing and permission checks
- **Permissions** — Flag-based character permissions with extensible hooks
- **Currency** — Configurable money system with physical currency items
- **Attributes** — Character stats with a boost/debuff system
- **Doors** — Ownership, factions, access lists, locking
- **Storage** — Persistent world containers
- **Vendors** — NPC buy/sell shops with configurable catalogs
- **UI** — 9 default Razor panels (character select, creation, HUD, chat, inventory, storage, vendor, scoreboard, death screen), all overridable
- **Persistence** — Custom JSON database layer with in-memory caching
- **Plugins** — Extend the framework via `[HexPlugin]` classes or s&box addon packages
- **Events & Hooks** — Interface-based event system with fire, permission-gate, and value-reduce patterns

## Quick Start

1. Add Hexagon as a library reference in your s&box Game project
2. Drop the `HexagonFramework` component into your scene — all systems auto-initialize
3. Create a character data class extending `HexCharacterData`
4. Define items, factions, and classes as GameResource assets
5. Optionally create a `[HexPlugin]` to hook into framework events

## Documentation

- **[Wiki](https://github.com/noahsabaj/hexagon/wiki)** — Full schema developer guide + API reference
- **[Example Schema (hl2rp)](https://github.com/noahsabaj/hl2rp-hexagon)** — A Half-Life 2 RP schema built on Hexagon

## Project Structure

```
Code/
├── Core/           # Bootstrap, events, plugin system
├── Characters/     # Character data, runtime, networking
├── Factions/       # Faction and class definitions
├── Items/          # Item definitions and base types
├── Inventory/      # Grid-based inventory system
├── Chat/           # Chat routing and built-in classes
├── Commands/       # Command parsing and execution
├── Permissions/    # Flag-based permission system
├── Currency/       # Money management
├── Attributes/     # Character stats and boosts
├── Doors/          # Door mechanics and ownership
├── Storage/        # Persistent containers
├── Vendors/        # NPC vendor system
├── Config/         # Server configuration
├── Logging/        # Server-side logging
├── Persistence/    # Database layer
├── UI/             # UI manager, panels, styles
└── Schema/         # Skeleton schema template
```

## License

Hexagon is dual-licensed under the [MIT License](LICENSE-MIT) and [Apache License 2.0](LICENSE-APACHE), at your option.
