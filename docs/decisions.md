# Decisions

These are Hexagon's. A game built on it keeps its own.

One paragraph each, newest last. A decision records what was chosen and why, so it can be
revisited when the reason stops being true.

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

## 2026-09-18: One JSON file per document, no database and no write-ahead log

A roleplay server holds thousands of characters at most and changes a handful per second. The
sandboxed filesystem has no rename, so a write cannot be atomic. `DocumentStore` writes a complete
sibling copy first, then the real file, then removes the sibling. A crash at any point leaves at
least one complete copy, and loading prefers the real file and falls back to the sibling. Tests
tear each write in turn and reboot. What this gives up is atomicity across documents. Nothing
needs that yet. The first feature that does, most likely a trade between two characters, should
put both sides in one document rather than bring back a transaction log.

## 2026-09-18: The host trusts the connection and its own measurements, nothing else

Every request starts in `Player.Authorize`: the caller must own the pawn it is calling, and must be
within a request budget. Character ownership is checked against the caller's Steam id, and "not
yours" gets the same answer as "does not exist" so ids cannot be probed. A door measures the
distance to the caller's pawn itself. Chat is sent only to the connections in range.

## 2026-09-19: A reported position is a claim, and host rules read the believed one

`PlayerController` runs on the owning client, so `WorldPosition` on the host is whatever the client
said. Hexagon does not fight the engine with server-side movement. `MovementAudit` keeps the last
position the host found believable: travel and rise are limited to one and a half times the
controller's run speed over quarter-second windows, with slack for jitter and physics; falling is
free. Reach, chat range, witnesses and the saved position all read `Player.HostPosition`, never the
claim, so a client that declares itself beside a door or a speaker gains nothing even in the moment
before it is corrected. An unbelievable claim is journaled and the owner is sent back. When the
host moves a character itself, claims from the old place are ignored until the owner arrives, but
never accepted, so a host teleport is not a free one. The cost is that a client which stalls for
over a second and then catches up is pulled back. The play tests use `goto`, which is exactly what
a cheat does, to prove it fails, and the operator's `hexagon_teleport` to move characters honestly.

## 2026-09-18: Test by playing

Every broken feature in the old code was hidden by a test that faked the exact piece that was
wrong. Here, pure rules get unit tests, and everything else is checked by `tools/playtest.ps1`,
which plays the real game in the real editor through the editor's own control endpoint. The
`DevDriver` component exists only for that. It is never placed in a scene, refuses to run outside
the editor, and calls the same client requests the HUD calls, so it cannot pass a rule the real
path would fail.

## 2026-09-18: Carried over unchanged

The character-name work: NFKC normalization, the single-script rule, and the Unicode confusable
skeleton with its generated table. It held up under review and is the hardest part to get right.

## 2026-09-19: Three laws, because the hard problems of a roleplay server are social

NutScript and Helix are toolkits for building a gamemode. What actually costs a roleplay server its
players is metagaming, duplicated items and worthless money, disputes nobody can settle, and staff
abuse. Those frameworks leave all four to a rulebook and the admins. Hexagon enforces them, and
every feature is judged against three laws.

**Perception.** A client receives only what its character could perceive. Chat already reached
only the connections in range. A character's name and faction were `[Sync]`, so every client held
every name; they now go to the owner alone, and what replicates to everyone is presence and
description, which is what an onlooker would see. One leak remains and is known: a spoken line
still carries the speaker's name to those in earshot. Recognition closes it by formatting each line
for its listener.

**Conservation.** Items and tokens only move. `Transfers` is the single way either changes hands:
between holders, in from a named source, out through a named sink. `InventoryGrid` no longer has
an add or a remove. Starting items and tokens are issued from `character.start`, an operator's gift
from `operator`, a discard goes to `discard`, and deleting a character sends what it held through
`character.deleted`. An item keeps its id for life. A holder is anything with an inventory and
tokens: a character today; crates, vendors, corpses and dropped items are the same thing later.

**Memory.** Every host decision is journaled: who, what, to what, where, who was near enough to
see, and whether it was refused. The journal is an append-only file of JSON lines per day. It is
evidence and never state: nothing is rebuilt from it, the documents stay the truth, and a failed
journal write never undoes a decision. Operator commands are journaled like everything else. The
token supply equals the journal's issues minus its destroys, and a test holds it to that.

## 2026-09-19: Capabilities and verbs, not faction checks

`FactionDefinition.CanLockDoors` was the first of what would have become a flag per feature, the
same shape as Helix's `IsCombine()` and single-letter flags. The host now asks one question,
`Player.HostCan( "door.lock" )`, answered from what the character is and what it holds: a faction
lists capabilities, and so does an item. HL2RP's keycard is an item that grants `door.lock`, so a
citizen holding one can lock a door and loses the ability with the card. A permission that lives in
an object can be stolen, lent and confiscated; a flag in a database row cannot. Denials, for
conditions such as restraints, are in the rule and win over any grant.

Everything a character does to something in the world goes through `Player.RequestAct`. A target
implements `IVerbTarget`: it lists its verbs and carries them out, nothing more. Identity, the rate
limit, reach, capability and the journal entry are decided in that one method, so a new target
cannot forget one; the door's own requests were not rate-limited before this. It is deliberately
not a hook bus. Nothing can veto someone else's verb, and extension is by adding components and
assets. Until there is a verb menu, Use is a target's first verb and Reload its second.

## 2026-09-19: Hexagon supplies a death pipeline, not a death policy

Whether death is permanent, who may finish a downed character and what is lost are a setting's
choices. Hexagon will supply the downed state, the deliberate and journaled finishing act, and the
settings a game chooses its policy with.

## 2026-09-19: Dedicated servers are the target

A serious city needs a host that stays up and is not a player. Listen hosting still works and is
what the play test uses, but the host's own client sees everything the host knows, so the
perception law is only a guarantee on a dedicated server. Host migration stays refused.

## 2026-09-19: Each primitive arrives by moving a feature that already plays onto it

The old code reached 440 passing tests with broken gameplay by building abstractions ahead of
features. The journal, transfers, capabilities and verbs each came in by carrying door locking,
discarding, the operator's give and character creation, and the play test stayed green throughout.
A primitive that does not make the next feature smaller should be taken out again.

Trades between two characters will touch two documents. `Transfers` changes memory and the caller
saves the giver first, so a crash between the saves loses a thing the journal can restore and
never duplicates one. This replaces the earlier note about putting both sides in one document.

## 2026-09-19: What one player learns about another is tested with two real clients

The editor play test has one client that is also the host, so it cannot see a leak. The second
play test starts `sbox-server.exe` on the local project with `+net_allow_local 1` and joins two
`sbox.exe` clients with `+connect local`. A dedicated server ignores a redirected console, so the
test leaves commands as files in the server's data folder; the server relays each to a client by
RPC, the client runs it as its own player and reports back, and the reply is read from the
server's output. The channel exists only when both ends were started with `+hexagon_dev 1`, and a
client never runs console commands for a server. Both clients are one Steam account, which the
engine allows, so they share a character list; that also tests entering a character twice.

## Order of work

1. Done: journal, transfers, capabilities, the verb checkpoint, owner-only names.
2. Done: two-client play test against a dedicated server.
3. Done: movement plausibility. Host rules read the believed position; implausible claims are journaled and corrected.
4. World items, item verbs and storage, all as holders.
5. Recognition, which also closes the spoken-name leak.
6. Downed state and combat.
7. Restraints and search: a denial, and opening someone else's holder with a capability.
8. Commerce, with declared sources and sinks.
9. A staff console over the journal.

HL2RP keeps what is about its setting: the scanner, civic records as a view of the journal,
forcefields and ration dispensers. Continuous integration is still missing.
