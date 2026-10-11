# Hexagon

A roleplay framework for s&box. Hexagon is a library: it supplies the components, rules, panels
and tools a serious roleplay server needs, and a game supplies the setting. It is written against
the engine's own features rather than a layer over them.

Where it is going, and the game it is for, is in [docs/vision.md](docs/vision.md).

It is not a port of NutScript or Helix. Those leave a server's hardest problems to a rulebook:
cheap death, metagaming, duplicated items, disputes and staff abuse. Hexagon holds itself to four
laws.

- **Mortality.** Death is final. A wound puts a character down; dying takes someone finishing them
  in front of witnesses, or bleeding out where a game says so. An account lives one life at a time,
  and the next enters the city a day after the last one ended.
- **Perception.** A client receives only what its character could perceive. Names and factions
  reach their owner alone; speech reaches only those in range; people farther away than a character
  can see are not sent at all.
- **Conservation.** Items only move: between holders, in from a named source, out through a named
  sink. Money is items like any other. Nothing is set, spawned or deleted.
- **Memory.** Every host decision is journaled with who, where and who saw it, refusals and
  operator commands included.

[HL2RP](https://github.com/noahsabaj/hl2rp-hexagon) is the first game built on it, and contains
no code at all: its factions, items and scene are assets. That is the standard Hexagon holds
itself to.

## What a game gets

- **Players.** The host gives each connection a pawn driven by the engine's `PlayerController`.
  Where a client says it is counts as a claim; host rules read where the host believes it is.
- **Characters.** One living character per account unless a game allows more, and the dead are
  kept, names and all. After a death the next character can be made at once, and it enters the city
  a day later. A character that has lived ends by dying or by being retired, which counts as a
  death; one that never entered the city can be deleted. Names are checked for look-alikes, mixed
  scripts and invisible characters, so one player cannot pass for another.
- **Recognition.** Strangers are what they look like until they introduce themselves. What each
  character knows is its own and is never sent to anyone else.
- **Factions**, as `.faction` assets: a name, a look and an optional whitelist. A faction is
  people: it grants no abilities, and a new member arrives with nothing but their clothes.
- **Items**, as `.item` assets in a grid inventory. An item can grant capabilities (a zip tie
  restrains), be a key cut to a lock, be used up (food, a bandage), be held in view of everyone, or
  be a weapon that uses ammunition. Identical items pile up in one slot, and every one of them is
  accounted for. Money is an item too.
- **Radio.** A radio is tuned on the radio. `/r` reaches radios tuned the same, as a voice without
  a face, and anyone beside the speaker as ordinary speech.
- **Looks.** A faction sets its members' body and outfit, an item its model and how it is held.
  Appearance goes to everyone; the faction behind a uniform does not.
- **Holders.** A crate, a locker, a dropped item and a body are one kind of thing, so dropping,
  storing, looting and searching are the same transfer. A scene's author can stock a container,
  which is filled once, the first time it is opened, and never refills.
- **Verbs.** A world object or a person lists what can be done to it. One host checkpoint decides
  who may: identity, rate limit, reach, capability, journal. Use does the first, or for a person
  opens the menu, which names them all.
- **Chat**: say, `/w` whisper, `/y` yell, `/me`, `/r` radio and `//` out-of-character. Speech reaches
  only players in range, each in their own words for the speaker.
- **Keys and locks.** A door or a container can have a lock cut to a pattern, and any key cut the
  same works it, whoever holds it, so a ministry's doors can share a pattern while each flat has
  its own. A key shows its cut, which anyone can compare, and never what it opens.
- **Doors** that anyone in reach can open and only a key locks. Owning one is title only: staff
  assign it, its owner hands it over by offering it, and a character gone for good gives up any it
  owned.
- **Handing over.** Giving someone something, or the title to a door, is an offer they accept or
  refuse. It lapses if they part or wait too long.
- **Violence with consequences.** Harm puts a character down, never out. Helping up, searching and
  finishing are deliberate, witnessed acts. Death is permanent and the body keeps what was carried,
  unless a game turns respawning on. A player who leaves while down or bound leaves the character
  lying, or standing bound, where it was.
- **Restraints and search**, conserving every item.
- **Staff tools** in game: the console's own commands, journaled under the staff member's name,
  including restoring a character whose death broke the rules.
- **Persistence.** Characters, what they know, their health and state, holders, doors and
  whitelists survive a restart. So does the journal. A change to two documents, such as a hand-over,
  is saved as one.
- **A default HUD**: character menu, chat, inventory, the open holder, verbs, offers, staff panel.
  In the city it shows only prompts and chat. Being hurt shows through the engine's
  post-processing instead of a number: the edges of sight darken and the colour drains, and being
  down narrows, dims and blurs the view without hiding it.

## Setting up from a clean checkout

Needs Windows, s&box installed through Steam, the .NET 10 SDK and PowerShell 7 (`pwsh`).

1. Clone `hexagon` and a game that uses it, such as `hl2rp-hexagon`, side by side.
2. Link this checkout into the game. From the game's folder:

```bash
pwsh -Command "New-Item -ItemType Junction -Path Libraries/hexagon -Target (Resolve-Path ../hexagon)"
```

3. From this folder, with the s&box editor closed, check that it builds:

```bash
pwsh tools/verify.ps1
```

4. Open the game's `.sbproj` in the s&box editor and press play. To host it instead, see
   [docs/hosting.md](docs/hosting.md).

## Using it in a new game

1. Reference the library in the game's `.sbproj`: `"PackageReferences": [ "kbj.hexagon" ]`.
2. Link this checkout into the game: a `Libraries/hexagon` junction pointing here.
3. In the startup scene, add a `Hexagon Game Manager`, a `ScreenPanel` with the `Hud`, at least one
   `SpawnPoint`, and any `Hexagon Door` objects, which must be networked objects.
4. Author `.faction` and `.item` assets.

## How it is built

| Part | Where | How it is checked |
| --- | --- | --- |
| Rules with no engine in them: names, lives, inventory grid, transfers, keys, journal, capabilities, chat parsing, rate limit, storage | `Code/Logic` | Unit tests in `Tests`, compiled from the same files |
| Components: game manager, player, sleeper, door, container, chat, operator commands | `Code` | Compiled against the installed engine, warnings as errors |
| Default HUD | `Code/UI` | Same compile, then looked at in the play test |
| A whole game on top | a game project | `tools/playtest.ps1` plays it in the real editor; `tools/playtest-server.ps1` plays it as two clients on a dedicated server |

What an onlooker could see is `[Sync( SyncFlags.FromHost )]` on components, and a person is sent
only to clients whose character is within `SightRange` of them. That includes their movement, which
their own client sends: the host tells it who may see it. Everything else goes to its owner by
`[Rpc.Owner]`. A client changes nothing directly: it calls a `[Rpc.Host]` request, and the host
re-derives who is asking from the connection, measures distances itself, and saves before it
replies.

## Commands

Run from this folder with the s&box editor closed. Those that need a game default to the sibling
`hl2rp-hexagon` checkout; pass `-GameRoot` for another.

```bash
pwsh tools/test.ps1
```

Runs the three below: `verify.ps1`, then the two-client play test, and the editor play test beside
it once both clients have joined. Each line is marked with the test that wrote it. Needs Steam
running and takes about three minutes.

```bash
pwsh tools/verify.ps1
```

Runs the logic tests, has s&box generate the projects, and compiles Hexagon against the engine.

```bash
pwsh tools/playtest.ps1
```

Boots the editor, enters play mode, and plays a full scenario through the real RPC path,
including a restart, in a throwaway data folder. It saves pictures of what it saw as
`hexagon-look-*.png` in the temp folder. A failed run keeps its data folder, journal included.
Takes about a minute and a half.

```bash
pwsh tools/playtest-server.ps1
```

Starts a real dedicated server and joins two real game clients to it, then asks each client what
it was actually told: that the other player's name never arrived, that a whisper stopped at its
range, that a door one opened is open for the other. Needs Steam running and takes about two
minutes.

```bash
pwsh tools/generate-confusables.ps1
```

Rebuilds the look-alike table that character names are checked against, from the Unicode release
it is pinned to. It refuses data whose hash is not the pinned one. `-Check` changes nothing and
fails if the table in the repository is not exactly what it would write; CI runs that.

## Operator commands

Typed in the host's console as `hexagon_<command>`, or by staff in game (Tab) without the prefix.

| Command | Effect |
| --- | --- |
| `staff <steamid64> <0\|1>` | Console only. Let an account use these commands in game |
| `whitelist <steamid64> <faction>` / `unwhitelist` | Allow or stop an account creating characters in a whitelisted faction. Taking it away sends that faction's characters on the account back to the menu |
| `give "<character name>" <item> [count] [pattern]` | Issue items, from the `operator` source. A key is cut to the pattern given |
| `take "<character name>" <item> [count]` | Take them out of the world, through the `operator` sink. No count takes all |
| `move "<from>" "<to>" <item> [count]` | Move them from one character to another, as any transfer |
| `teleport "<character name>" <x> <y> <z>`, and in game `bring` / `goto "<character name>"` | Move a character who is in the city |
| `hurt "<character name>" <amount>` / `revive "<character name>"` | Harm a character in the city, or help one up, here or not |
| `release "<character name>"` | Free a character's bound hands, here or not |
| `restore "<character name>"` | Undo a death that broke the rules. The character gets up where its body lies, with whatever is still on it. Refused while the account has all the living characters it may |
| `faction "<character name>" <faction>` | Change a character's faction |
| `door ["<door>"] <open\|close\|lock\|unlock\|release\|assign "<character name>">` | Work a door whatever key it asks for, give its title to a character, or take the title back. The keys are not part of the title. In game an unnamed door is the one beside you; `door` alone lists them |
| `who` | Who is playing whom, and where, and who was left down or bound. The one place a name is shown to someone never told it |
| `journal [text] [count] [days]` | The latest entries containing the text, from the last two days unless told otherwise |

A character is named as staff know it, in quotes, whether or not it is in the city. Every command is
journaled before it runs, under the staff member's name or as the console. The journal is
`journal/<date>.jsonl` under the data folder, one JSON object per line, a file per UTC day.

[docs/v0.1.md](docs/v0.1.md) defines the first release as a checklist. See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is not here yet.
