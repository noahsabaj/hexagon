# Hexagon

A roleplay framework for s&box. Hexagon is a library: it supplies the components, rules, panels
and tools a serious roleplay server needs, and a game supplies the setting. It is written against
the engine's own features rather than a layer over them.

[HL2RP](https://github.com/noahsabaj/hl2rp-hexagon) is the first game built on it, and contains
no code at all: its factions, items and scene are assets. That is the standard Hexagon holds
itself to.

## What a game gets

- **Players.** The host gives each connection a pawn driven by the engine's `PlayerController`.
- **Characters.** Several per account. Names are checked for look-alikes, mixed scripts and
  invisible characters, so one player cannot pass for another.
- **Factions**, as `.faction` assets: starting items, an optional operator whitelist, and what
  members may do, such as lock doors.
- **Items**, as `.item` assets, held in a grid inventory.
- **Chat**: say, `/w` whisper, `/y` yell, `/me`, and `//` out-of-character. In-character speech
  reaches only players in range, and nobody out of range learns a message existed.
- **Doors** that anyone in reach can open and only permitted factions can lock.
- **Persistence.** Characters, inventories, positions, whitelists and doors survive a restart.
- **A default HUD**: character menu, chat and inventory. A game can place its own instead.

## Using it in a game

1. Reference the library in the game's `.sbproj`: `"PackageReferences": [ "kbj.hexagon" ]`.
2. Link this checkout into the game: a `Libraries/hexagon` junction pointing here.
3. In the startup scene, add a `Hexagon Game Manager`, a `ScreenPanel` with the `Hud`, at least one
   `SpawnPoint`, and any `Hexagon Door` objects, which must be networked objects.
4. Author `.faction` and `.item` assets.

## How it is built

| Part | Where | How it is checked |
| --- | --- | --- |
| Rules with no engine in them: names, inventory grid, chat parsing, rate limit, storage | `Code/Logic` | Unit tests in `Tests`, compiled from the same files |
| Components: game manager, player, door, chat, operator commands | `Code` | Compiled against the installed engine, warnings as errors |
| Default HUD | `Code/UI` | Same compile, then looked at in the play test |
| A whole game on top | a game project | `tools/playtest.ps1` plays it in the real editor |

Public state is `[Sync( SyncFlags.FromHost )]` on components. Private state goes to its owner by
`[Rpc.Owner]`. A client changes nothing directly: it calls a `[Rpc.Host]` request, and the host
re-derives who is asking from the connection, measures distances itself, and saves before it
replies.

## Commands

Run from this folder with the s&box editor closed. Both default to the sibling `hl2rp-hexagon`
checkout; pass `-GameRoot` for another game.

```bash
pwsh tools/verify.ps1
```

Runs the logic tests, has s&box generate the projects, and compiles Hexagon against the engine.

```bash
pwsh tools/playtest.ps1
```

Boots the editor, enters play mode, and plays a full scenario through the real RPC path,
including a restart, in a throwaway data folder.

## Operator commands

Typed in the host's console.

| Command | Effect |
| --- | --- |
| `hexagon_whitelist <steamid64> <faction>` | Allow an account to create characters in a whitelisted faction |
| `hexagon_unwhitelist <steamid64> <faction>` | Remove that permission |
| `hexagon_give "<character name>" <item>` | Give an item to a character who is in the city |

See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is not here yet.
