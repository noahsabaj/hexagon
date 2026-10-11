# Decisions

These are Hexagon's. A game built on it keeps its own.

One paragraph each, newest last. A decision records what was chosen and why, so it can be
revisited when the reason stops being true. When a later decision changes an earlier one, the
earlier entry is corrected in place and says so, so that nothing here contradicts the code.

## 2026-09-18: Rewrite game-first, in one project

The previous code was a general roleplay framework in one repository and a game in another. It
reached 83,000 lines with a schema compiler, a module graph and a policy pipeline, while
restraining a player, flying the scanner and opening the search panel did not work. The framework
had been designed before a game existed to tell it what it needed. This rewrite builds the game
directly. A framework can be extracted later from whatever gets written twice. The old code is on
the `agent/v2-audit-remediation` branch of both repositories.

## 2026-09-19: Hexagon is the library, HL2RP is a game on it

The first cut of the rewrite was one project. Hexagon is meant to be a reusable roleplay framework,
so it was split the day after, and the split was cheap because almost nothing in it was specific to
Half-Life. Hexagon is an s&box library holding every component, rule, panel and tool. HL2RP is a
game that references it and currently contains no code at all: its factions, items and scene are
assets. That is the test of whether the library is reusable. When HL2RP needs something Hexagon
lacks, the question is whether any roleplay game would need it. If so it goes in Hexagon; if it is
about the Combine, it goes in HL2RP. s&box only compiles a library as part of a game, so the tools
live here but take a game as a parameter, and the game links this checkout through a
`Libraries/hexagon` junction.

## 2026-09-18: Use the engine, not a layer over it

Public state is `[Sync( SyncFlags.FromHost )]`. Private state goes to its owner with `[Rpc.Owner]`.
Client requests are `[Rpc.Host]` methods. Movement is `PlayerController`. Doors are `IPressable`.
Items and factions are `GameResource` assets. Operator tools are `[ConCmd]`. The old code wrapped
each of these in its own abstraction, including a hand-written snapshot wire format. Every wrapper
was a place for the two sides to disagree, and one such disagreement is why the search panel never
opened.

## 2026-09-18: One JSON file per document, no database

A roleplay server holds thousands of characters at most and changes a handful per second. The
sandboxed filesystem has no rename, so a write cannot be atomic. `DocumentStore` writes a complete
sibling copy first, then the real file, then removes the sibling. A crash at any point leaves at
least one complete copy, and loading prefers the real file and falls back to the sibling. Tests
tear each write in turn and reboot. What this gave up was atomicity across documents. (Corrected
2026-10-10: a sale, a payment, a death and a staff move each change two documents, and saving them
one after the other could duplicate a thing or lose it. They are now saved as one change; see "A
change to several documents is saved as one".)

## 2026-09-18: The host trusts the connection and its own measurements, nothing else

Every request starts in `Player.Authorize`: the caller pays for it from a budget kept per
connection, whichever pawn it names, and must own the pawn it is calling. Character ownership is
checked against the caller's Steam id, and "not yours" gets the same answer as "does not exist" so
ids cannot be probed. A door measures the distance to the caller's pawn itself. Chat is sent only to
the connections in range. (Corrected 2026-10-10: the budget was kept per pawn and charged only after
the ownership check, so a request in another player's name cost nothing, and every request refused
here was dropped without a record. See "A refusal is evidence too".)

## 2026-09-19: A reported position is a claim, and host rules read the believed one

`PlayerController` runs on the owning client, so `WorldPosition` on the host is whatever the client
said. Hexagon does not fight the engine with server-side movement. `MovementAudit` keeps the last
position the host found believable: travel and rise are limited to one and a half times the
controller's run speed over quarter-second windows, with slack for jitter and physics; falling is
limited to what gravity allows, and a drop must not pass through a floor (corrected after a play
test fell through a roof; see "What the play tests taught"). The audit knows speeds, not walls, so
the host also traces the straight line from the last claim it accepted to each new one, at knee
height so a step is not a wall, against the world: not people, items, bodies, or a door still
swinging. A claim through a wall or a shut door is refused like one that is too fast. (Added
2026-10-10: before, only a drop was traced, and walking through a wall at walking pace was
believed.) Reach, chat range, witnesses
and the saved position all read `Player.HostPosition`, never the claim, so a client that declares
itself beside a door or a speaker gains nothing even in the moment before it is corrected. An
unbelievable claim is journaled and the owner is sent back. When the host moves a character
itself, claims from the old place are ignored until the owner arrives, but never accepted, so a
host teleport is not a free one. Only the host places a character: the audit never takes a
client's claim as its starting point. The cost is that a client which stalls for over a second and
then catches up is pulled back, and so is one whose updates stall while it rounds a corner, because
the line between the two places it reported crosses the corner. The play tests use `goto`, which is
exactly what a cheat does, to prove it fails, and the operator's `hexagon_teleport` to move
characters honestly.

## 2026-09-18: Test by playing

Every broken feature in the old code was hidden by a test that faked the exact piece that was
wrong. Here, pure rules get unit tests, and everything else is checked by `tools/playtest.ps1`,
which plays the real game in the real editor through the editor's own control endpoint, and by
`tools/playtest-server.ps1`, described below. The `DevDriver` component and `DevCommands` exist only
for those. The component is never placed in a scene; outside the editor the commands run only when
the process was started with `+hexagon_dev 1`. They call the same client requests the HUD calls, so
they cannot pass a rule the real path would fail. What they cannot do is click: the HUD is looked
at in pictures the test saves, and played by a person.

## 2026-09-18: Carried over unchanged

The character-name work: NFKC normalization, the single-script rule, and the Unicode confusable
skeleton with its generated table. It held up under review and is the hardest part to get right.
(Corrected 2026-10-10: the table came across but its generator and the check that it is current
did not, while the table's header still claimed both. `tools/generate-confusables.ps1` is restored,
and CI runs it with `-Check`.)

## 2026-09-19: Three laws, because the hard problems of a roleplay server are social

NutScript and Helix are toolkits for building a gamemode. What actually costs a roleplay server its
players is metagaming, duplicated items and worthless money, disputes nobody can settle, and staff
abuse. Those frameworks leave all four to a rulebook and the admins. Hexagon enforces them, and
every feature is judged against three laws.

**Perception.** A client receives only what its character could perceive. Chat already reached
only the connections in range. A character's name and faction were `[Sync]`, so every client held
every name; they now go to the owner alone, and what replicates to everyone is presence and
description, which is what an onlooker would see. A spoken line at first still carried the speaker's
name to those in earshot; recognition, below, closed that. (Corrected 2026-10-10: "everyone" was
every client, wherever its character stood, and each pawn's object name carried its owner's Steam
name. See "Sight is a distance".)

**Conservation.** Items and tokens only move. `Transfers` is the single way either changes hands:
between holders, in from a named source, out through a named sink. `InventoryGrid` no longer has
an add or a remove. Starting items and tokens are issued from `character.start`, an operator's gift
from `operator`, and deleting a character sends what it held through `character.deleted`. An item
that does not stack keeps its id for life; piles are followed by count, as decided later. A holder
is anything with an inventory and tokens: a character, a crate, a vendor, a body, a dropped item.
(Corrected: discarding was later removed, and the other holders now exist.)

**Memory.** Every host decision is journaled: who, what, to what, where, who was near enough to
see, and whether it was refused. The journal is an append-only file of JSON lines per day. It is
evidence and never state: nothing is rebuilt from it, the documents stay the truth, and a failed
journal write never undoes a decision. Operator commands are journaled like everything else. The
token supply equals the journal's issues minus its destroys, and a test holds it to that.

## 2026-09-19: Capabilities and verbs, not faction checks

`FactionDefinition.CanLockDoors` was the first of what would have become a flag per feature, the
same shape as Helix's `IsCombine()` and single-letter flags. The host now asks one question,
`Player.HostCan( "door.lock" )`, answered from what the character is and what it holds: a faction
lists capabilities, and so does an item. HL2RP's keycard is an item that grants a door capability,
so a citizen holding one can lock a door and loses the ability with the card. A permission that
lives in an object can be stolen, lent and confiscated; a flag in a database row cannot. Denials,
for conditions such as restraints, are in the rule and win over any grant. (Corrected 2026-10-10:
every door asked for the same `door.lock`, so a keycard or a uniform locked a door someone had
bought. A door now names what locks it, `LockRequires`, which defaults to `door.lock`; whoever
bought it can always lock it. HL2RP's city doors ask for `door.lock.city`, which Civil Protection
and the keycard grant, and its door for sale asks for nothing, so only the owner locks it.)

Everything a character does to something in the world goes through `Player.RequestAct`. A target
implements `IVerbTarget`: it lists its verbs and carries them out, nothing more. Identity, the rate
limit, reach, capability and the journal entry are decided in that one method, so a new target
cannot forget one; the door's own requests were not rate-limited before this. It is deliberately
not a hook bus. Nothing can veto someone else's verb, and extension is by adding components and
assets. Use is a target's first verb, unless the target says Use opens its menu, as a person does;
the rest are chosen from a menu, as decided later.

## 2026-09-19: Hexagon supplies a death pipeline, not a death policy

Whether death is permanent, who may finish a downed character and what is lost are a setting's
choices. Hexagon supplies the downed state, the deliberate and journaled finishing act, and the
settings a game chooses its policy with: `Death`, `DeathDropsBelongings`, `DownedSeconds`,
`BleedOutKills` and `FinishRequires` on the game manager.

## 2026-09-19: Dedicated servers are the target

A serious city needs a host that stays up and is not a player. Listen hosting still works and is
what the editor play test uses, but the host's own client sees everything the host knows, so the
perception law is only a guarantee on a dedicated server. Host migration stays refused.

## 2026-09-19: Each primitive arrives by moving a feature that already plays onto it

The old code reached 440 passing tests with broken gameplay by building abstractions ahead of
features. The journal, transfers, capabilities and verbs each came in by carrying door locking,
discarding (since removed), the operator's give and character creation, and the play test stayed green throughout.
A primitive that does not make the next feature smaller should be taken out again.

Trades between two characters touch two documents. `Transfers` changes memory, and the caller
saves every document it touched as one change. (Corrected 2026-10-10: this said the giver is saved
first, so that a crash loses a thing rather than duplicating it. In a sale each side gives
something, so no order of two saves is safe: saving the vendor first and crashing left the price in
both purses. See "A change to several documents is saved as one".)

## 2026-09-19: What one player learns about another is tested with two real clients

The editor play test has one client that is also the host, so it cannot see a leak. The second
play test starts `sbox-server.exe` on the local project with `+net_allow_local 1` and joins two
`sbox.exe` clients with `+connect local`. A dedicated server ignores a redirected console, so the
test leaves commands as files in the server's data folder; the server relays each to a client by
RPC, the client runs it as its own player and reports back, and the reply is read from the
server's output. The channel exists only when both ends were started with `+hexagon_dev 1`, and a
client never runs console commands for a server. Both clients are one Steam account, which the
engine allows, so they share a character list; that also tests entering a character twice.

## 2026-09-19: Everything that holds things is one kind of thing

A crate in the scene, an item on the ground, a body and a vendor are all `HolderData`: an
inventory, a token balance, a kind. Dropping, picking up, storing, looting, searching a person,
buying and selling are therefore all `Transfers.Move` or `Trade.Sell`, and an item keeps its id
through every one of them. A character looks into at most one other holder at a time, "the open
holder", and only that character is told what is in it. It closes when the character walks away or
when the reason it was open ends, such as a searched person being helped up. Discarding was removed:
things are dropped, used up, or kept. A holder cannot be deleted while anything is in it.

## 2026-09-19: A name is knowledge

Every pawn replicates a face, `CharacterId`, which is the same tomorrow and says nothing about
who it is. Names are in each character's own document, under the faces that have introduced
themselves, and reach only that character's owner. Chat is formatted per listener: the name if
they know it, otherwise the speaker's description in brackets. Out-of-character chat carries the
player's name, not the character's. This closes the spoken-name leak recorded earlier. The
exceptions are for staff: the `who` command, and the journal, which names who an entry concerns.
Both are staff only, and reading either is itself journaled.

## 2026-09-19: Harm puts a character down; death is a separate, deliberate act

`Vitals.Damage` never kills. At no health a character is down: visible to all, unable to act or to
leave the city, and part of the scene. Anyone can help them up or search them; finishing them is
its own verb, journaled with its witnesses, and a game can require a capability for it. After
`DownedSeconds` a downed character gets back up, or dies if the game sets `BleedOutKills`. What
death means is `GameManager.Death`: respawn elsewhere without belongings, or permanent. Unless the
game turns `DeathDropsBelongings` off, a body is left holding what was carried, if anything was, and
it is a holder like any other, laid out as it was carried. Health, being down, how long they have
been down, and being restrained are in the character's document, and a player who leaves while down
or bound leaves the character where it is, so disconnecting escapes nothing. (Corrected 2026-10-10:
this was claimed before it was true. Leaving took the character out of the scene, where nobody could
search, help, release or finish it, and its time down started again on return. See "Leaving does not
end a scene". A body packed its contents afresh, and what did not fit stayed with the character
that had just died.) How hurt someone else is cannot be read off them; that they are down, bound or
armed can.

Restraints are thin on purpose: a zip tie grants `person.restrain`, is used up by it, and the bound
character is denied every act until released, by anyone or by staff. There is no struggle or timer
yet.

## 2026-09-19: Money has declared faucets and sinks, and nothing else

Tokens enter from `character.start`, `wage`, `vendor.float` and `operator`; goods from
`character.start`, `vendor.restock` and `operator`. They leave through `consumed`, `property`,
`character.deleted` and, since 2026-10-10, `operator`, for staff taking back what should not have
been given. A vendor's stock and till are finite: it restocks from its source up to a
limit, and can only pay for goods with tokens it holds. Wages go only to characters who are in the
city and able to act. A sale moves the item first, because that half can still fail for want of
room, and the price is always the vendor's. The journal is enough to compute the money supply and
every faucet's rate, which is what an operator needs to keep tokens worth something.

## 2026-09-19: Staff use the console's commands, as themselves

`Operators.Execute` is the one implementation behind both the server console and the in-game
staff panel. Staff status is set only from the console, so it cannot be granted or taken from
inside the game. Every line staff type is journaled before it runs, refusals included, and reading
the journal is itself an entry. There is no separate admin mod to trust.

## 2026-09-19: Identical things pile up, and a pile is counted

An item asset says how many share a slot. One means each is its own object and is followed
through the journal by id, as before. More than one makes a pile: every issue, move and destroy
records a count, so the supply of rounds is the journal's issues minus its destroys exactly as the
supply of tokens is. A whole pile that lands in a slot of its own keeps its id; part of a pile is a
new pile. Splitting and merging move nothing between holders and are journaled anyway, because
"where did the other ten go" is a question a dispute asks. A vendor trades one at a time, at a
price for one. All of a transfer happens or none of it does.

## 2026-09-19: A radio is an object with a dial

The frequency is on the radio, not the character, so whoever takes a radio takes its setting:
a searched officer's radio is tuned to the officers' channel. `/r` reaches everyone else carrying
a radio tuned the same, wherever they are, as a voice: a name if the listener was given one, and
otherwise nothing, because a radio shows no face. Anyone within speaking distance hears someone
talking into a radio, whatever they carry. Someone down or bound cannot reach theirs. People
standing next to a *listener* do not yet overhear it.

## 2026-09-19: What is worn is seen; who issued it is not

A faction sets a body and an outfit, an item sets a model and how it is held, and both are synced
to everyone as a look, because they are appearance. The faction itself is still told only to its
owner. So a uniform says "this person is dressed as Civil Protection", which is exactly what an
onlooker would know, and a stolen uniform would say the same. A body lies as its character looked.
An item with no model is a small block in its tint, in the world and in the hand.

## 2026-09-19: Use does the obvious thing; everything else is chosen by name

Looking at something shows what Use will do and lists what else is offered. One key lays the rest
out as a menu of named buttons. Numbers work only inside that menu. Before this, every verb hung
off a bare number key, and a journal showed a door bought for 50 tokens by someone pressing 3.
(Corrected 2026-10-10: for a person nothing is obvious. Use introduced you, so walking up to someone
and pressing the key everyone presses gave away a name. A target can now say that Use opens its
menu, and a person does.)

## 2026-09-19: What the play tests taught

- A failed run keeps its data folder. The first kept journal explained the run once recorded here
  as unexplained, where a character was judged from the wrong place: the test clients are real
  windows with focus, and someone at the machine had walked one about, clicked, and pressed keys.
  A client driven by a test now suppresses keyboard and mouse input. It still has a body.
- The movement audit never adopts a client's claim as a starting point. Only the host places a
  character; until it has, claims are neither believed nor punished.
- A body at rest is asleep, and one teleported into the air asleep hung there. A teleport wakes it.
- The editor test saves pictures of the HUD and the scene, so that "it renders" is looked at.
- One editor run accepted a claimed teleport to a door with no refusal on record. It recurred once
  data was being kept, and the journal explained it: the character had been spawned at one of the
  map's own spawn points, on a roof directly above the doors, and the audit treated any fall as
  free, so "I am now 320 units lower, beside the door" was believed, through the roof. Three
  changes came of it. A game's own spawn points are used before a map's. Falling is limited to
  what gravity allows. And a claim well below the believed position must have a clear line down to
  it, which the host traces, because the audit knows speeds and not walls. The editor test now
  tries exactly this from that roof. (Since 2026-10-10 every claim is traced, not only a drop, and
  the editor test also steps through a locked door.)
- Removing an object waits for the end of the frame. In the two-client test, a player who left
  while bound left nobody behind: the pawn being removed still held the character, so the sleeper,
  looking a moment later, took the character for still being in the city and removed itself. The
  character now leaves the pawn before the pawn goes. (2026-10-10)
- The editor builds the game's code in the background and swaps it in when it is done. A scene
  played before then lost every Hexagon component, so nobody ever arrived, and a command sent while
  it happened failed inside the editor. The editor test now waits for the build, plays the scene
  again if nobody arrives within a minute, and sends a command again only when the editor failed
  before setting anything. (2026-10-10)
- A dedicated server writes its errors to its other output stream, which the two-client test did
  not read, so an exception went unseen by its last check. It reads both now. (2026-10-10)

## 2026-10-10: A change to several documents is saved as one

`DocumentStore.SaveTogether` writes every document of one change into a single file under
`together/` before it writes any of them alone, then writes each as before, then removes that file.
A crash before the file is complete changes nothing; a crash after it is finished the next time the
store opens, before anything is read. This is a write-ahead log one change long, which the first
entries here said there would be none of, but no order of separate saves is safe once each side of a
change gives something. Sales, payments, a body taking what a character carried, a door being
bought, a zip tie used on someone, and staff moving things between characters are saved this way.
A character or holder that has been deleted is no longer written back by anything still holding it,
so it cannot return after a restart. A copy of the data folder taken during such a change can still
catch one side of it without the other, because copying a folder is not one act.

## 2026-10-10: Leaving does not end a scene

A player who leaves while their character is down or bound leaves the character where it was, as a
`Sleeper`: someone lying in the street or standing bound, who keeps bleeding, and whom anyone can
help up, search, release or finish exactly as if the player were there. It is the host's object.
When the player returns and enters with that character, they pick up from where it was left, and
the sleeper goes. Whoever is down or bound when the server stops is put back the same way when it
starts. Someone who leaves on their feet leaves nothing behind: a city of the logged-off would be a
city of loot. Deleting a character is refused while it is in the city on any connection, or while it
is down or bound, so a character cannot be deleted out from under a scene either.

## 2026-10-10: Sight is a distance

The engine sends every networked object to every client unless told otherwise. Pawns and sleepers
are spawned with `AlwaysTransmit` off and implement `INetworkVisible`: the host sends one to a client
only while that client's character is within `GameManager.SightRange` of it, by where the host
believes both to be. Owners and the host always get their own. A client is not told when it stops
being sent something, so each pawn carries a `Pulse` that the host advances twice a second, and a
pawn whose pulse has not moved for two seconds is hidden on that client, collision and all, until it
moves again. The engine announces a new object to every client and sends it to all of them for two
seconds before culling applies, so a sleeper keeps its look and description back for its first
three. A pawn's object name was its owner's Steam name, which every client was sent; it is now
`Player`.

## 2026-10-10: A refusal is evidence too

`Player.Authorize` charges each request to its connection first, then checks that the caller owns
the pawn it names, then that a character is loaded where one is needed. A request refused there, or
later for being malformed (an item that is not there, a verb the target does not offer, an aim that
is not a number), is journaled as `request.limited`, `request.forged` or `request.invalid`, with what
was asked. Those three cost a client nothing to cause, so `RefusalThrottle` writes at most one of
each kind per account per second and counts the rest into the next entry, or into one of its own
once the flood stops. A refusal a game rule makes is recorded under the kind its success would have
had, or a kind of its own such as `trade.buy`; only rearranging one's own inventory goes unrecorded,
allowed or not. A swing that misses is not a refusal: `combat.attack` records what it hit, if
anything.

## 2026-10-10: Staff can undo what the game cannot

A dispute ends with something put back. `take` sends items or tokens out through the `operator`
sink, `move` carries them from one character to another by the same transfers as anything else,
`release` frees bound hands, `faction` changes a character's faction, and `door` opens, shuts, locks,
unlocks or releases a door, or on its own lists the doors with their owners. Commands that name a
character find it whether or not it is in the city, except `teleport`, `bring`, `goto` and `hurt`,
which need it there. Lines typed at the server console are journaled too, as the console. Taking a
whitelist away sends that account's characters of the faction back to the menu, and entering one is
refused until the whitelist is given back. A character that leaves the world for good, deleted or
dead under a permanent death policy, releases every door it owned.

## 2026-10-10: The journal answers in names

Staff cannot turn a character id into a name in game, and a deleted character's name can be taken
again, so an entry keeps the names of the character it concerns and of each witness, as they were
when it was written. The ids stay the evidence. `journal` searches the last two days by default and
up to fourteen, naming each day once an answer spans more than one; a day is a UTC day.

## What is thin, and known

- Weapons hit by a host ray along the attacker's claimed aim. There is no recoil, spread or cover.
- The engine ships no weapon models, so HL2RP's pistol is a block held in a pistol stance. Clothing cannot yet be an item.
- The HUD has been looked at in pictures, not yet played by a person. Drag and drop is not done: items move by select, then click.
- An in-game whitelist application flow is not built. A radio is not overheard by those beside a listener.
- Sight is a distance, not a line: a client is sent everyone within `SightRange` of its character, through walls. Doors, crates, vendors, bodies and dropped items are sent to every client wherever its character is.
- The engine tells every client who is connected, by Steam name, and which connection owns each pawn it is sent. Hexagon keeps character names and factions from clients, but not which account a pawn in view belongs to. Closing that needs the engine.
- A client whose updates stall while it rounds a corner is pulled back, because the wall check traces the straight line between the places it reported.
- The city is saved every `AutosaveSeconds` and when its game manager is destroyed. Whether stopping a dedicated server with Ctrl+C destroys it first has not been tried.
- Unit tests run on a hosted runner on every push, and so does the check that the confusables table is what its generator makes. Both play tests need s&box and stay local.
- The play tests run both clients as one Steam account, so account-level rules are tested by one account.

HL2RP keeps what is about its setting: the scanner, civic records as a view of the journal,
forcefields and ration dispensers.

## Order of work, completed 2026-09-19

1. Journal, transfers, capabilities, the verb checkpoint, owner-only names.
2. Two-client play test against a dedicated server.
3. Movement plausibility: host rules read the believed position.
4. World items, item verbs and storage, all as holders.
5. Recognition.
6. The downed state, combat and the death pipeline.
7. Restraints and search.
8. Commerce: vendors, wages, paying people, buying doors.
9. The staff console over the journal.
