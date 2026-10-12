# Hexagon

A roleplay framework for s&box. Hexagon is a library: it supplies the components, rules, panels
and tools a serious roleplay server needs, and a game supplies the setting. It is written against
the engine's own features rather than a layer over them.

Where it is going, and the game it is for, is in [docs/vision.md](docs/vision.md).

It is not a port of NutScript or Helix. Those leave a server's hardest problems to a rulebook:
cheap death, metagaming, duplicated items, disputes and staff abuse. Hexagon holds itself to four
laws.

- **Mortality.** Death is final. Harm puts a character down; dying takes someone finishing them in
  front of witnesses, a shot to the head from a weapon made to kill, or bleeding out where a game
  says so. An account lives one life at a time, and the next enters the city a day after the last
  one ended.
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
- **Making a character.** Everyone is made the same way: a body for life, the engine's citizen or
  a human skin the game lists; hair the game lists; and clothes from the game's wardrobe to arrive
  in, carrying nothing else but the papers a game gives every arrival. Nobody is made into a faction.
- **Factions**, as `.faction` assets: a name, a tint and its ranks. A faction is people: it grants
  nothing and changes nothing about its members. Staff appoint its head; its members offer newcomers
  a place, and a superior promotes, demotes and discharges those below them, as each rank allows. A
  member can walk out. A uniform and a rank's insignia are handed over and put on like anything else,
  so what others see is what someone wears; their rank is told only to them.
- **Items**, as `.item` assets in a grid inventory. An item can grant capabilities (a zip tie
  restrains), be a key cut to a lock, be used up (food, or a bandage that treats a wound), be held
  in view of everyone, or be a weapon that uses ammunition, made to kill or not. Identical items
  pile up in one slot, and every one of them is accounted for. Money is an item too.
- **Radio.** A radio is tuned on the radio. `/r` reaches radios tuned the same, as a voice without
  a face, and anyone beside the speaker as ordinary speech.
- **Clothes** are items, worn on the body rather than carried in the grid, and put on and taken off
  by hand. The engine decides what cannot be worn together; nothing comes off by itself to make way.
  Clothes can be taken off someone down or bound, or off a body, and worn as a disguise.
- **Looks.** A character's look is its body, its hair and what it wears; an item has its model and
  how it is held. Appearance goes to everyone; the faction behind a uniform does not.
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
  its own. A key shows its cut, which anyone can compare, and never what it opens. A lock not yet
  fitted is an item, cut as a key is, and shows its cut too.
- **Doors** that anyone in reach can open and only a key locks.
- **Buildings.** A building or a flat is one title over its doors and lockers: the right to sell or
  rent them, and to change their locks, which opens nothing by itself. A character holds it, or
  nobody does and it is its office's, and whoever carries a stamp that leaves the office's mark sells
  it, or takes a title offered to them back for the office. The money is handed over apart, like
  anything else. Whoever holds a title fits a new lock to one of its doors, standing open, or to one
  of its lockers they can open, and the old lock comes out in their hands, cut as it was. Staff
  assign and release titles, and a character gone for good leaves its buildings to their offices.
- **Handing over.** Giving someone something, or the title to a building, is an offer they accept or
  refuse. It lapses if they part or wait too long.
- **Papers.** Paper and printed forms are items. Writing takes something to write with, and ink
  stays: lines are only added below until the sheet is full, and a form's blanks are filled in once.
  A stamp leaves its office's mark over what is written by then. A sheet says only what is on it,
  while the journal knows whose hand it was, so a forgery reads like the real thing and a stamp is
  what makes a paper official. A sheet is read by whoever holds it, or held up for someone within
  reach to read without letting go of it. A game can give every new character a form in its name,
  stamped by the office that issues it.
- **Maps.** A planning map is an item printed with a picture, laid out on a table in front of whoever
  carries it. Leaning over it looks straight down at it, and the mouse works on the map itself: lines
  and short words in grease pencil, which takes something to write with and which anyone at the table
  can wipe off, and the counters that come with it, moved by hand onto it and back into their box.
  Whoever can see the table sees each mark once it is made. What is on a map goes with it when it is
  picked up, carried, handed over or stolen, and the journal knows whose hand made each mark.
- **Making things.** A machine, such as a press, is loaded like a crate and worked by holding Use
  until its work is done; a tool, such as a die, works in hand on what its holder carries. Each lists
  what it makes from what, and a lock keeps a machine to whoever has the key. Letting go, walking
  away or putting the tool down stops the work, and then nothing is made. Whatever makes a thing
  leaves its mark on it, which nobody can read: someone with the right tool, such as a loupe, can
  tell only whether the things in a pile were made alike. A press can print a serial on each thing
  it makes, which anyone can read. A piece of work can keep what it is worked from, such as the key
  a copy is cut from, make more than one thing, such as a lock and the keys that come with it, and
  cut the keys and locks it makes to a pattern nobody has or to the one it copies. Every making is
  on the record, with what made it.
- **Trains.** Goods come into the city and leave it by train, at set times of day: a train runs in
  along its track, stands at its station for a while and runs out. Its wagons are opened only there,
  with the key its lock is cut to. It brings what its asset lists, with a manifest of it under its
  railway's stamp, and takes away whatever is still aboard, loaded or never unloaded. A train missed
  while the server was down never comes; staff can send one, send it away, or hold it.
- **Trucks.** A truck is driven by whoever sits at its wheel with a key cut to its lock, on the fuel
  in its tank, which driving burns by the distance and which is poured in by hand, a whole can at a
  time. The host drives it, as one body held up by its wheels; a driver sends only the pedals and the
  wheel. Someone else can ride beside the driver, and the bed behind locks to the same key. A truck
  that strikes someone faster than a walk hurts them, by how fast, on the record under its driver;
  slower, it pushes them aside. It stays where it was left, with what is in its tank.
- **Sites.** A place worked for what it yields, such as a scrap yard, is worked like a machine, with
  the right tool in hand, and gives a little each time. What is taken grows back slowly, up to what
  the site holds at most.
- **Violence with consequences.** The host judges where a shot lands, on the body's own hitboxes. A
  weapon's wound to an arm or a leg stays until it is treated: a wounded leg limps, and a wounded arm
  drops what it held and holds nothing. Harm puts a character down, never out, unless a weapon made
  to kill finds the head. Helping up, treating, searching and finishing are deliberate, witnessed
  acts. Death is permanent and the body keeps what was carried, unless a game turns respawning on.
- **Bodies.** Someone down or dead lies as the engine's ragdoll, and someone down sees from it, on
  the ground. A limp, bound hands, a flinch and hard breathing show on the body and are heard near
  it, never as numbers. A player who leaves while down or bound leaves the character lying, or
  standing bound, where it was. The dead and the down are dragged by whatever part is taken hold
  of, slowly and with one hand, or carried over the shoulder at a walk with both; someone standing
  bound is led by the arm. A truck's bed takes a few bodies, which go wherever the truck goes until
  someone with its key takes them out.
- **Restraints and search**, conserving every item.
- **Staff tools** in game: the console's own commands, journaled under the staff member's name,
  including restoring a character whose death broke the rules.
- **Persistence.** Characters, what they know and wear, their health and state, what is written on
  papers and drawn on maps, holders, doors, who holds each building, the locks changed since the
  world was built, the last serial each press printed, a train standing at
  its station, what each site has left, and where each truck was left with what is in its tank
  survive a restart. So does the journal. A change to two documents, such as a hand-over,
  is saved as one.
- **A default HUD**: character menu, chat, inventory, the open holder, verbs, offers, the tools of a
  map leant over, how far along some work is, the seat taken and the fuel gauge, staff panel.
  In the city it shows only prompts and chat; a prompt draws the key as the player has it bound.
  Items are dragged between cells, piles and holders, and out of the window to drop them. Being hurt shows through the engine's
  post-processing instead of a number: the edges of sight darken and the colour drains, and being
  down narrows, dims and blurs the view from the ground without hiding it.

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
3. In the startup scene, add a `ScreenPanel` with the `Hud`, at least one `SpawnPoint`, and any
   `Hexagon Door` objects, which must be networked objects. A `Hexagon Property` makes a building or a
   flat of doors and containers, held by the office whose stamp leaves the mark it names until it is
   sold. The city's services come with the library;
   their rules are under Hexagon in the project's Systems settings. A model from the cloud needs a
   `Hexagon Cloud Model` beside its renderer, naming its package, or a dedicated server and its
   clients draw the error model.
4. Author `.faction` and `.item` assets.

## How it is built

| Part | Where | How it is checked |
| --- | --- | --- |
| Rules with no engine in them: names, lives, wounds, inventory grid, transfers, keys, papers, maps, journal, capabilities, chat parsing, rate limit, storage | `Code/Logic` | Unit tests in `Tests`, compiled from the same files |
| Services and components: the city's services, characters, player, sleeper, door, container, chat, operator commands | `Code` | Compiled against the installed engine, warnings as errors |
| Default HUD | `Code/UI` | Same compile, then looked at in the play test |
| A whole game on top | a game project | `tools/playtest.ps1` plays it in the real editor; `tools/playtest-server.ps1` plays it as two clients on a dedicated server |

What an onlooker could see is `[Sync( SyncFlags.FromHost )]` on components, and a person is sent
only to clients whose character is within `SightRange` of them. That includes their movement, which
their own client sends: the host tells it who may see it. Everything else goes to its owner by
`[Rpc.Owner]`. A client changes nothing directly: it calls a `[Rpc.Host]` request, and the host
re-derives who is asking from the connection, measures distances itself, and saves before it
replies.

## Commands

Run from this folder with the s&box editor closed, but for `playtest.ps1 -Attach`. Those that need
a game default to the sibling `hl2rp-hexagon` checkout; pass `-GameRoot` for another.

```bash
pwsh tools/test.ps1
```

Runs the three below: `verify.ps1`, then the two-client play test, and the editor play test beside
it once both clients have joined. Each line is marked with the test that wrote it. Needs Steam
running and takes about three minutes.

While working, keep the editor open on the game and run the least that covers the change:
`dotnet test Tests` for a rule in `Code/Logic`, in seconds, and `playtest.ps1 -Attach`, which plays
in the open editor once it has built the edit, in about forty seconds, or less with `-Until`; for
what one player is told about another, `playtest-server.ps1 -Keep -Until <section>`. Close the
editor and run `test.ps1` once before merging: it also compiles with warnings as errors and plays
two clients on a dedicated server.

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
pwsh tools/playtest.ps1 -Attach -Until Doors
```

Plays in the editor already open on the game instead of starting one, a minute sooner, and leaves
it open; the editor builds code edits as they are saved. The editor is pointed at the run's own
data folder, and asked, before anything is played, and pointed back after. `-Until` stops once the
section it names is played; each section builds on the ones before, so the run starts from the
first.

```bash
pwsh tools/playtest-server.ps1
```

Starts a real dedicated server and joins two real game clients to it, then asks each client what
it was actually told: that the other player's name never arrived, that a whisper stopped at its
range, that a door one opened is open for the other. Needs Steam running and takes about three
minutes, half of it starting the server and the clients. It stops at the first check that fails.

```bash
pwsh tools/playtest-server.ps1 -Keep -Until 'A faction'
```

`-Keep` leaves the server and both clients running when the run ends, and the next run with
`-Keep` starts on them, in a fresh city: the whole test then takes about half a minute. A change to
the code or an asset since starts them again. `-Close` closes them, and so does a run without `-Keep`, or `test.ps1`. `-Until` stops
once the section it names is played; with `-Keep`, the backup section, which stops the server, is
left out.

```bash
pwsh tools/compile-assets.ps1
```

A dedicated server reads assets only as the editor compiled them. This opens the editor hidden
when an asset is new or has changed since it was compiled, waits until it has compiled them, and
closes it. The two-client test runs it first: it costs nothing when nothing changed, and about a
minute and a half when something did.

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
| `give "<character name>" <item> [count] [pattern]` | Issue items, from the `operator` source. A key is cut to the pattern given |
| `take "<character name>" <item> [count]` | Take them out of the world, through the `operator` sink. No count takes all |
| `move "<from>" "<to>" <item> [count]` | Move them from one character to another, as any transfer |
| `teleport "<character name>" <x> <y> <z>`, and in game `bring` / `goto "<character name>"` | Move a character who is in the city. Someone down is moved in the body they lie in |
| `hurt "<character name>" <amount> ["<place>"]` / `revive "<character name>"` | Harm a character in the city, or help one up, here or not. With a place (`head`, `body`, `left arm`, `right arm`, `left leg`, `right leg`), it lands there as a weapon's would, without killing outright: a wound to an arm or a leg stays until it is treated |
| `release "<character name>"` | Free a character's bound hands, here or not |
| `restore "<character name>"` | Undo a death that broke the rules. The character gets up where its body lies, with whatever is still on it. Refused while the account has all the living characters it may |
| `faction "<character name>" <faction\|none> ["<rank>"]` | Appoint a character to a faction, at its head unless a rank is named, or take it out of one. Nothing else about the character changes |
| `door ["<door>"] <open\|close\|lock\|unlock>` | Work a door whatever key it asks for. In game an unnamed door is the one beside you; `door` alone lists them, with what each lock is cut to |
| `property "<property>" <assign "<character name>"\|release>` | Give a building's title to a character, or give it back to its office. The keys are not part of the title. `property` alone lists them, with who holds each |
| `train "<train>" <send\|away\|hold\|resume>` | Send a train in now, send one standing at its station away, or hold it away from its times until resumed. `train` alone lists them, with where each is and when it comes next |
| `who` | Who is playing whom, and where, and who was left down or bound. The one place a name is shown to someone never told it |
| `journal [text] [count] [days]` | The latest entries containing the text, from the last two days unless told otherwise |

A character is named as staff know it, in quotes, whether or not it is in the city. Every command is
journaled before it runs, under the staff member's name or as the console. The journal is
`journal/<date>.jsonl` under the data folder, one JSON object per line, a file per UTC day.

[docs/v0.1.md](docs/v0.1.md) defines the first release as a checklist. See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is not here yet.
