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
  Where a client says it is counts as a claim; host rules read where the host believes it is.
- **Characters.** Several per account. Names are checked for look-alikes, mixed scripts and
  invisible characters, so one player cannot pass for another.
- **Recognition.** Strangers are what they look like until they introduce themselves. What each
  character knows is its own and is never sent to anyone else.
- **Factions**, as `.faction` assets: starting items and tokens, a wage, an optional whitelist,
  and the capabilities members have, such as `door.lock`.
- **Items**, as `.item` assets in a grid inventory. An item can grant capabilities (a key), be
  used up (food, a bandage), be held in view of everyone, or be a weapon that uses ammunition.
- **Holders.** A crate, a dropped item, a body and a vendor are one kind of thing, so dropping,
  storing, looting, searching and trading are the same transfer.
- **Verbs.** A world object or a person lists what can be done to it. One host checkpoint decides
  who may: identity, rate limit, reach, capability, journal.
- **Chat**: say, `/w` whisper, `/y` yell, `/me`, `//` out-of-character, and `/pay`. Speech reaches
  only players in range, each in their own words for the speaker.
- **Doors** that anyone in reach can open, that a capability or ownership can lock, and that can be bought.
- **Violence with consequences.** Harm puts a character down, never out. Helping up, searching and
  finishing are deliberate, witnessed acts. The game chooses what death means.
- **Restraints and search**, **vendors, wages and payments**, all conserving items and tokens.
- **Staff tools** in game: the console's own commands, journaled under the staff member's name.
- **Persistence.** Characters, what they know, their health and state, holders, doors and
  whitelists survive a restart. So does the journal.
- **A default HUD**: character menu, chat, inventory, the open holder, verbs, staff panel.

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

Typed in the host's console as `hexagon_<command>`, or by staff in game (Tab) without the prefix.

| Command | Effect |
| --- | --- |
| `staff <steamid64> <0|1>` | Console only. Let an account use these commands in game |
| `whitelist <steamid64> <faction>` / `unwhitelist` | Allow or stop an account creating characters in a whitelisted faction |
| `give "<character name>" <item>` | Issue an item, from the `operator` source |
| `teleport "<character name>" <x> <y> <z>`, and in game `bring` / `goto "<character name>"` | Move a character |
| `hurt "<character name>" <amount>` / `revive "<character name>"` | Harm a character, or help one up |
| `payday` | Pay every faction's wage now |
| `who` | Who is playing whom, and where. The one place a name is shown to someone never told it |
| `journal [text] [count]` | The latest of today's journal entries containing the text |

The journal is `journal/<date>.jsonl` under the data folder, one JSON object per line.

[docs/v0.1.md](docs/v0.1.md) defines the first release as a checklist. See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is not here yet.
