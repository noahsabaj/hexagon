# Decisions

These are Hexagon's. A game built on it keeps its own.

Each section says what Hexagon does and why, as it is now. When a decision changes, its section is
rewritten to match the code, not annotated; how it got here is in the repository's history. Where an
earlier way failed in a way worth remembering, the section says so, because that is part of why.

## A library, and a game made of assets

Hexagon is an s&box library holding every component, rule, panel and tool. HL2RP is a game that
references it and contains no code at all: its factions, items and scene are assets. That is the
test of whether the library is reusable. When a game needs something Hexagon lacks, the question is
whether any serious roleplay game would need it. If so it goes in Hexagon; if it is about the
Combine, it goes in the game. s&box compiles a library only as part of a game, so the tools live here
but take a game as a parameter, and the game links this checkout through a `Libraries/hexagon`
junction.

It is a rewrite. The code before it was a general framework in one repository and a game in another,
designed before a game existed to say what it needed. It reached 83,000 lines with a schema compiler,
a module graph and a policy pipeline, while restraining a player, flying the scanner and opening the
search panel did not work. So this one was built game-first, and a primitive comes in only by moving
a feature that already plays onto it: the journal, transfers, capabilities and verbs each arrived
carrying a working feature, with the play test green throughout. A primitive that does not make the
next feature smaller is taken out again. The old code is on the `agent/v2-audit-remediation` branch of
both repositories.

## The engine, not a layer over it

Public state is `[Sync( SyncFlags.FromHost )]`; private state goes to its owner by `[Rpc.Owner]`;
client requests are `[Rpc.Host]` methods. Movement is the engine's `PlayerController`, looking at
things is its `IPressable` and its tooltips, items and factions are `GameResource` assets, operator
tools are `[ConCmd]`, outfits are its `Clothing` and `Dresser`, harm is shown through its
post-processing, and dragging an item is its panel input. The old code wrapped each of these in its
own abstraction, including a hand-written snapshot format, and every wrapper was a place for the two
sides to disagree: one such disagreement is why its search panel never opened. Before something is
built, the engine's source is checked for it.

## Four laws

What costs a roleplay server its players is cheap death, metagaming, duplicated items and worthless
money, disputes nobody can settle, and staff abuse. NutScript and Helix leave all of it to a
rulebook. Hexagon enforces it, and every feature is judged against four laws: mortality, perception,
conservation and memory. The README states them; the sections below are how each is kept.

## Death is final, and the dead are kept

A character that can come back has nothing to lose. `Vitals.Damage` never kills: at no health a
character is down, visible to all, unable to act or to leave the city, and part of the scene. Anyone
can help them up or search them; finishing them is its own verb, journaled with its witnesses, and a
game can require a capability for it (`FinishRequires`). After `DownedSeconds` a downed character gets
back up, or dies if the game sets `BleedOutKills`. How hurt someone else is cannot be read off them;
that they are down, bound or armed can.

Death is `Permanent` unless a game turns respawning on (`GameManager.Death`), as the tests may. The
dead are kept: on the list as dead, under names nobody else can take, with what they carried left on
a body that is a holder like any other, laid out as it was carried (`DeathDropsBelongings`). An
account has `LivingCharacters` lives at once, one by default. The next character can be made at once,
but enters the city `NewLifeHours` after the account's latest death, a day by default, however many
are made and deleted meanwhile. A character that has lived ends by dying or by being retired, which
counts as a death: it takes what it carries and starts the wait, and it is refused while the character
is in the city, down or bound. One that never entered the city never lived, so deleting it frees its
name and starts no wait. A character that leaves the world for good gives up every door it owned.

Staff can `restore` a character killed by a rule break, an exploit or a bug. It gets up where its body
lies, with whatever is still on it; anything taken from the body is for staff to move back. A restore
is refused while the account has all the lives it may, so it never makes an extra one.

Restraints are thin on purpose: a zip tie grants `person.restrain`, is used up by it, and the bound
character is denied every act until released, by anyone or by staff. There is no struggle or timer.

## Leaving does not end a scene

A player who leaves while their character is down or bound leaves the character where it was, as a
`Sleeper`: someone lying in the street or standing bound, who keeps bleeding, and whom anyone can help
up, search, release or finish exactly as if the player were there. When the player returns with that
character, they pick up from there and the sleeper goes. Whoever is down or bound when the server
stops is put back the same way when it starts. Someone who leaves on their feet leaves nothing behind:
a city of the logged-off would be a city of loot. Removing an object waits for the end of the frame,
so the character leaves its pawn before the pawn goes; otherwise the sleeper, looking a moment later,
takes the character for still being in the city and removes itself.

## Sight is a distance

The engine sends every networked object to every client unless told otherwise. Pawns, sleepers,
bodies and dropped items are spawned with `AlwaysTransmit` off and implement `INetworkVisible`: the
host sends one to a client only while that client's character is within `GameManager.SightRange` of
it, by where the host believes both are. Owners and the host always have their own. Every object's
name goes to every client, so a pawn is called `Player` and a dropped item `Item`, never by who or
what they are.

The engine asks whoever sends an object whether a connection may have it: the host for what it syncs,
and the owner's client for what it simulates, which for a pawn is its movement. What one client sends
another passes through the host unread. So the host works out who may see each pawn and tells its
owner whenever that changes, at most four times a second (`HostTellObservers`), and the owner's client
sends its movement to them and the host alone. A client that ignores this gives away only its own
movement. The owner's client also tells a client it stops sending to, for the engine there to make
the pawn inactive, but that notice is wrapped by the host as a `TargetedMessage` and the receiving
engine unwraps only packed ones (`NetworkSystem.OnTargetedMessage`; reported as
Facepunch/sbox-public#12071), so it is lost. Each pawn therefore carries a `Pulse` the host advances
twice a second, and a client hides a pawn whose pulse has stopped for two seconds, collision and all.

The engine announces a new object to every client wherever it is, and sends it to all of them for two
seconds before culling applies. So what a sleeper, a body or a dropped item is never goes out with it
(`Reveal`). The host tells the clients that can see the place at once, by an RPC to them alone, and
sets it as synced state three seconds later, by when the engine sends it only to those near enough;
anyone who comes near later is sent it then. Each machine dresses a body and builds an item's model
and shape itself, once it knows. Before this, a sleeper showed nobody anything for three seconds,
witnesses included, and a body or a dropped item went to every client with its look and its name.

## A name is knowledge

Every pawn replicates a face, `CharacterId`, which is the same tomorrow and says nothing about who it
is, and a description, which is what an onlooker sees. Names are in each character's own document,
under the faces that have introduced themselves, and reach only that character's owner. Chat is
formatted per listener: the name if they know it, otherwise the speaker's description in brackets.
Out-of-character chat carries the player's name, not the character's. A faction is told only to its
own member. Staff are the exception, through `who` and the journal, and both are journaled.

## What is worn is seen; who issued it is not

A faction sets a body and an outfit, an item sets a model and how it is held, and both are synced to
everyone who can see them as a look, because they are appearance. So a uniform says "dressed as Civil
Protection", which is what an onlooker would know, and a stolen uniform says the same. A body lies as
its character looked. An item with no model is a small block in its tint, in the world and in the
hand.

## The host trusts the connection and its own measurements

Every request starts in `Player.Authorize`: the caller pays for it from a budget kept per connection,
whichever pawn it names, then must own the pawn it calls, then must have a character loaded where one
is needed. Character ownership is checked against the caller's Steam id, and "not yours" gets the same
answer as "does not exist", so ids cannot be probed. Distances are the host's own (see below), and
chat is sent only to connections in range.

A refusal is evidence too. A request refused there, or for being malformed (an item that is not there,
a verb the target does not offer, an aim that is not a number), is journaled as `request.limited`,
`request.forged` or `request.invalid`, with what was asked. Those cost a client nothing to cause, so
`RefusalThrottle` writes at most one of each kind per account per second and counts the rest into the
next. A shot fired within its weapon's cooldown is refused the same way, as `combat.attack` with the
reason `cooldown`; it once went unrecorded, and a test that fired too soon lost a shot with nothing in
the journal to say so. A refusal a game rule makes is recorded under the kind its success would have
had, or one of its own such as `movement.implausible`. Only rearranging one's own inventory goes
unrecorded. A swing that misses is not a refusal: `combat.attack` records what it hit, if anything.

## A reported position is a claim

`PlayerController` runs on the owning client, so `WorldPosition` on the host is whatever the client
said. Hexagon does not fight the engine with server-side movement. `MovementAudit` keeps the last
position the host found believable: travel and rise are limited to one and a half times the
controller's run speed over quarter-second windows, with slack for jitter and physics, and a fall to
what the scene's own gravity allows (`Scene.PhysicsWorld.Gravity`). The audit knows speeds, not walls,
so the host also traces the line from the last claim it accepted to each new one, at knee height so a
step is not a wall, against the world: not people, items, bodies, or a door still swinging. A claim
too fast, or through a wall, a shut door or a floor, is journaled and the owner is sent back.

Reach, chat range, witnesses, sight and the saved position all read `Player.HostPosition`, never the
claim, so a client that declares itself beside a door or a speaker gains nothing even in the moment
before it is corrected. When the host moves a character itself, claims from the old place are ignored
until the owner arrives, but never accepted, so a host teleport is not a free one. Only the host
places a character: the audit never takes a client's claim as its starting point. The cost is that a
client which stalls for over a second and then catches up is pulled back, and so is one whose updates
stall while it rounds a corner, because the line between the two places it reported crosses it.

## One checkpoint for acting on the world

Everything a character does to something in the world goes through `Player.RequestAct`. A target
implements `IVerbTarget`: it lists its verbs and carries them out, nothing more. Identity, the rate
limit, reach, capability and the journal entry are decided in that one method, so a new target cannot
forget one. Reach is a distance and a line: the host measures from where it believes the character
stands to the target's bounds, then traces from the character's eyes to the target, and a crate near
enough on the far side of a wall is out of reach. It is deliberately not a hook bus: nothing can veto
someone else's verb, and extension is by adding components and assets. Every verb's id and every
journal kind is a constant (`VerbIds`, `JournalKinds`), because staff search the journal by them and
tests count them.

Use does a target's first verb, unless the target says Use opens its menu, as a person does: walking
up to someone and pressing the key everyone presses once gave away a name. Everything else is chosen
by name from a menu, and numbers work only inside it; when every verb hung off a bare number key, a
journal showed a door bought by someone pressing 3.

## Everything that holds things is one kind of thing

A crate in the scene, an item on the ground, a body and a searched person are all holders: an
inventory and a kind. Dropping, picking up, storing, looting and searching are all `Transfers.Move`,
and an item keeps its id through every one. A character looks into at most one other holder at a time,
"the open holder", and only that character is told what is in it. It closes when the character walks
away or when the reason it was open ends, such as a searched person being helped up. A holder cannot
be deleted while anything is in it. A container can have a lock, and a scene's author can stock it.

## Items only move, and money is items

`Transfers` is the only way an item changes hands: between holders, in from a named source, out
through a named sink, each journaled. Things enter from `operator`, staff giving them on the record,
or `scene`, a container a scene's author stocked, which is filled the first time it is opened and never
refills. They leave through `consumed`, `operator`, `character.deleted`, `character.retired`, or
`character.died` when a death leaves no body. A new character arrives with only its clothes. There are
no wages, vendors or starting kits: they made money from nothing and so made it worthless. Money is
an item like any other, money only because people take it.

Identical things pile up, as many to a slot as the item's asset says. Every issue, move and destroy
records a count, so the supply of anything is the journal's issues minus its destroys, and a test
holds the supply of pistol rounds to it. A whole pile that lands in a slot of its own keeps its id;
part of a pile is a new pile. Splitting and merging move nothing between holders and are journaled
anyway, because "where did the other ten go" is a question a dispute asks. All of a transfer happens
or none of it does. Deliveries, production and forgery come later.

## Locks take keys, and a faction grants nothing

A permission that lives in an object can be stolen, lent and confiscated; a flag on a faction cannot.
So the host asks `Player.HostCan( capability )`, answered only from what the character carries, and
a faction is a title, a description, a look and an optional whitelist. A door or a container has a
`LockPattern`, and a key item is cut to a pattern when it is made, by staff's `give` or by a scene's
stock. Any key cut to a lock works it, whoever holds it, so a ministry's doors can share one pattern
while each flat has its own. A client is told a key's cut, a short code anyone holding two keys can
compare, and never the pattern, which would say what it opens. Owning a door is title only, the right
to sell or rent it: staff assign it, an owner hands it over by offering it, and the keys go
separately.

## Handing over takes two

A character offers something, some of a pile or the title to a door, to someone within reach, who
accepts or refuses. Nothing moves until they accept, and then the host checks again that the giver
still has it and that both are still there and able. An offer lapses after thirty seconds, or as soon
as the two part, either is down or bound, or the giver leaves. Every offer, answer and lapse is
journaled. Slipping something into someone's pockets unasked is a different act, and will be done by
stealth.

## A radio is an object with a dial

The frequency is on the radio, not the character, so whoever takes a radio takes its setting: a
searched officer's radio is tuned to the officers' channel. `/r` reaches everyone else carrying a
radio tuned the same, wherever they are, as a voice: a name if the listener was given one, otherwise
nothing, because a radio shows no face. Anyone within speaking distance hears someone talking into a
radio. Someone down or bound cannot reach theirs. People beside a listener do not yet overhear it.

## The HUD shows what a character would know

In the city the HUD shows prompts and chat. Health is not a number, because nobody knows their health
as one: being hurt darkens the edges of sight and drains the colour, and being down narrows, dims and
blurs the view without hiding it. These are the engine's own post-processing (`HarmCues`): two global
volumes with its vignette, colour adjustment and blur, weighted by the character's state, blended over
whatever the game's camera does. At its defaults the engine's vignette left someone down all but
blind, and someone down still sees who comes near, so it was eased until the world shows through.

What is looked at is described by its own `IPressable` tooltip, as the engine's `PlayerController`
gathers it: an icon, a title and a line about it, with the key that acts on it drawn by the engine as
the player has it bound (`Input.GetGlyph`), keyboard or controller. Items move by dragging, the
engine's panel input: onto another cell, onto a pile of the same thing, across into the open holder
or out of it, or out of the window to drop it on the ground. A click still chooses an item for the
buttons. The engine's own platform chat reaches every player by account name, whatever the distance,
so the host warns at startup if a game leaves it on.

## Staff use the console's commands, as themselves

`Operators.Execute` is the one implementation behind both the server console and the in-game staff
panel, so there is no separate admin mod to trust. Staff status is set only from the console, so it
cannot be granted or taken from inside the game. Every line staff type is journaled before it runs,
under their name or as the console, refusals included, and reading the journal is itself an entry.

A dispute ends with something put back, so staff can: `take` sends items out through the `operator`
sink, `move` carries them between characters by the same transfers as anything else, `release` frees
bound hands, `revive` helps someone up, `faction` changes a character's faction, `door` works a door
whatever key it asks for or assigns and releases its title, and `restore` undoes a death that broke
the rules. Commands that name a character find it whether or not it is in the city, except
`teleport`, `bring`, `goto` and `hurt`, which need it there. Taking a whitelist away sends that
account's characters of the faction back to the menu, and entering one is refused until it is given
back.

## The journal is evidence, never state

Every host decision is journaled: who, what, to what, where, who was near enough to see, and whether it
was refused. The journal is an append-only file of JSON lines per UTC day. Nothing is rebuilt from it,
the documents stay the truth, and a failed journal write never undoes a decision. A character id
cannot be turned into a name in game, and a deleted character's name can be taken again, so an entry
keeps the names of the character it concerns and of each witness as they were when it was written;
the ids stay the evidence. `journal` searches the last two days by default and up to fourteen.

## One JSON file per document, and a change to several saved as one

A roleplay server holds thousands of characters at most and changes a handful a second, so there is
no database. The sandboxed filesystem has no rename, so `DocumentStore` writes a complete sibling copy
first, then the real file, then removes the sibling, and loading prefers the real file and falls back
to the sibling. Tests tear each write in turn and reboot.

No order of separate saves is safe once each side of a change gives something, so
`DocumentStore.SaveTogether` writes every document of one change into a single file under `together/`
before it writes any of them alone, then writes each, then removes that file. A crash before the file
is complete changes nothing; a crash after it is finished the next time the store opens. A hand-over,
a body taking what a character carried, a restored character taking it back, a zip tie used on
someone, and staff moving things between characters are saved this way. A deleted character or holder
is never written back by anything still holding it. A copy of the data folder taken during such a
change can still catch one side of it without the other. The store keeps the data folder it was made
with, because the editor can close a city from its menu's context, which has none (reported as
Facepunch/sbox-public#12072).

## Dedicated servers are the target

A serious city needs a host that stays up and is not a player. Listen hosting works and is what the
editor play test uses, but the host's own client sees everything the host knows, so the perception
law is a guarantee only on a dedicated server. Host migration is refused: a new host would have none
of the saved state.

## The character-name rules

Names are normalized (NFKC), held to a single script, and checked against the Unicode confusable
skeleton with its generated table, so one player cannot pass for another. This came over from the old
code because it held up under review and is the hardest part to get right. `tools/generate-confusables.ps1`
rebuilds the table from the pinned Unicode release, and CI checks it is current.

## Test by playing

Every broken feature in the old code was hidden by a test that faked the exact piece that was wrong.
Here, pure rules get unit tests in `Tests`, compiled from the same files. Everything else is checked
by playing: `tools/playtest.ps1` plays the real game in the real editor through its control endpoint,
and `tools/playtest-server.ps1` starts a dedicated server and joins two real clients, because the
editor's one client is also the host and cannot see a leak. A dedicated server ignores a redirected
console, so the test leaves commands as files in the server's data folder; the server relays each to
a client by RPC, the client runs it as its own player, and the reply is read from the server's output.
Both clients are one Steam account, which the engine allows, so they share a character list.

`DevDriver` and `DevCommands` exist only for these. The component is never placed in a scene; outside
the editor the commands run only when both ends were started with `+hexagon_dev 1`, and a client never
runs console commands for a server. They call the same client requests the HUD calls, so they cannot
pass a rule the real path would fail. A client a test drives takes no keyboard or mouse input until its
session ends, because the test clients are real windows with focus and a journal once showed someone at
the machine walking one about; a refused command sets nothing, so no server can take a player's keys.
What the tests cannot do is click: the HUD is looked at in pictures the editor test saves, and played
by a person.

While working, the editor stays open: `dotnet test Tests` for a rule, and `playtest.ps1 -Attach`,
which plays in that editor once it has built the edit, in about forty seconds, or less with `-Until`.
It points the editor at the run's own data folder and asks again, and points it back after. Before
merging, `tools/test.ps1` runs verify and both play tests as one, in three to four minutes.

## What the play tests taught

- A failed run keeps its data folder. Its journal has explained every run that looked unexplained.
- A character was once believed through a roof onto a door: it had spawned on one of the map's own
  spawn points, and falls were free. A game's own spawn points now come first, falls are limited by
  gravity, and every claim is traced. The editor test tries exactly that drop, and steps through a
  locked door.
- A body at rest is asleep, and one teleported into the air asleep hung there. A teleport wakes it.
- The editor builds the game's code in the background and swaps it in when done. A scene played
  before then has none of the game's components; the driver says so and the scene is played again.
  Now and then the editor opens the scene mid-build and never takes the build in, and the test starts
  it again. The editor's own Razor compiler is stricter than `dotnet build`'s, so only the play test
  proves a panel compiles.
- A dedicated server writes errors to its other output stream; the two-client test reads both.
- The server numbers clients in the order they join, so each client is started with its number,
  `+hexagon_dev_client`, and tells the host once its player exists.
- Most of a run was s&box starting. With RenderDoc's folder on the PATH, every native library the
  engine loads is slow; the scripts start s&box without it. A minimized client ran at a few frames a
  second, so the test clients run with `-nowindow`. Four s&box processes starting at once take three
  times as long as one, so the editor starts once both clients have joined. The editor, while playing,
  listens on the engine's default port, so the server takes one of its own.
- Fixed waits hid three things: a shot within its cooldown, a door still swinging when stepped through,
  and a check passing on an earlier refusal. A check now asks until it sees what it expects, a refusal
  waits for the journal to count it, and the host accepts at most four requests a second from anyone,
  so the tests send no faster.

## What is thin, and known

- Weapons hit by a host ray along the attacker's claimed aim. There is no recoil, spread or cover.
- The engine ships no weapon models, so HL2RP's pistol is a block held in a pistol stance. Clothing is
  not yet an item.
- The HUD has been looked at in pictures, not yet played by a person.
- An in-game whitelist application flow is not built. A radio is not overheard beside a listener.
- Sight is a distance, not a line: a client is sent everyone within `SightRange` of its character,
  through walls. Doors and containers are scene objects, sent to every client.
- The engine tells every client who is connected, by Steam name, and which connection owns each pawn
  it is sent. Hexagon keeps character names and factions from clients, but not which account a pawn
  in view belongs to. Closing that needs the engine.
- The engine announces every new networked object to every client, so a client far away learns that
  something was spawned at a place, though not what. Closing that needs the engine too.
- The city is saved every `AutosaveSeconds` and when its game manager is destroyed. Whether stopping
  a dedicated server with Ctrl+C destroys it first has not been tried.
- Unit tests and the confusables check run on a hosted runner on every push. Both play tests need
  s&box and stay local.
- The play tests run both clients as one Steam account, so account-level rules are tested by one
  account, and the two-client test allows that account two lives at once.
- The vision's production, deliveries, recruitment, ranks, documents and player offices are not
  built. Until they are, things enter the world only from staff and from stocked containers.

HL2RP keeps what is about its setting: the scanner, civic records as a view of the journal,
forcefields and ration dispensers.
