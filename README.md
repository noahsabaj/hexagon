# Hexagon

A roleplay framework for s&box. Hexagon is a library: it supplies the components, rules, panels
and tools a serious roleplay server needs, and a game supplies the setting. It is written against
the engine's own features rather than a layer over them.

It is not a port of NutScript or Helix. Those leave a server's hardest problems to a rulebook:
metagaming, duplicated items, disputes and staff abuse. Hexagon holds itself to three laws.

- **Perception.** A client receives only what its character could perceive. Names and factions
  reach their owner alone; speech reaches only those in range.
- **Conservation.** Items and tokens only move: between holders, in from a named source, out
  through a named sink. Nothing is set, spawned or deleted.
- **Memory.** Every host decision is journaled with who, where and who saw it, refusals and
  operator commands included.

[HL2RP](https://github.com/noahsabaj/hl2rp-hexagon) is the first game built on it, and contains
no code at all: its factions, items and scene are assets. That is the standard Hexagon holds
itself to.

## What a game gets

- **Players.** The host gives each connection a pawn driven by the engine's `PlayerController`.
- **Characters.** Several per account. Names are checked for look-alikes, mixed scripts and
  invisible characters, so one player cannot pass for another.
- **Factions**, as `.faction` assets: starting items and tokens, an optional operator whitelist,
  and the capabilities members have, such as `door.lock`.
- **Items**, as `.item` assets, held in a grid inventory. An item can grant capabilities, so a
  key is an item and can be stolen.
- **Verbs.** A world object lists what can be done to it. One host checkpoint decides who may:
  identity, rate limit, reach, capability, journal.
- **Chat**: say, `/w` whisper, `/y` yell, `/me`, and `//` out-of-character. In-character speech
  reaches only players in range, and nobody out of range learns a message existed.
- **Doors** that anyone in reach can open and only a character with `door.lock` can lock.
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
| Rules with no engine in them: names, inventory grid, transfers, journal, capabilities, chat parsing, rate limit, storage | `Code/Logic` | Unit tests in `Tests`, compiled from the same files |
| Components: game manager, player, door, chat, operator commands | `Code` | Compiled against the installed engine, warnings as errors |
| Default HUD | `Code/UI` | Same compile, then looked at in the play test |
| A whole game on top | a game project | `tools/playtest.ps1` plays it in the real editor; `tools/playtest-server.ps1` plays it as two clients on a dedicated server |

What an onlooker could see is `[Sync( SyncFlags.FromHost )]` on components. Everything else goes
to its owner by `[Rpc.Owner]`. A client changes nothing directly: it calls a `[Rpc.Host]` request, and the host
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

```bash
pwsh tools/playtest-server.ps1
```

Starts a real dedicated server and joins two real game clients to it, then asks each client what
it was actually told: that the other player's name never arrived, that a whisper stopped at its
range, that a door one opened is open for the other. Needs Steam running and takes several
minutes, most of it the clients starting.

## Operator commands

Typed in the host's console.

| Command | Effect |
| --- | --- |
| `hexagon_whitelist <steamid64> <faction>` | Allow an account to create characters in a whitelisted faction |
| `hexagon_unwhitelist <steamid64> <faction>` | Remove that permission |
| `hexagon_teleport "<character name>" <x> <y> <z>` | Move a character who is in the city |
| `hexagon_give "<character name>" <item>` | Issue an item, from the `operator` source, to a character who is in the city |

The journal is `journal/<date>.jsonl` under the data folder, one JSON object per line.

See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is not here yet.
