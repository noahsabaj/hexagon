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

The city's host-side services are one `GameObjectSystem`, `HexagonServices`. The engine makes one for
every scene, so a game places nothing to have a city, and sets its rules, death, sight and lives among
them, in the project's Systems settings, or per scene. It opens the city as the host starts
(`ISceneStartup`), ticks it at the end of each fixed update, saves it as the scene goes, and hears
connections arrive and leave as an `INetworkListener`. A character, played or left lying, is acted on
through one `CharacterService` and has one `CharacterBody`: the engine's `IDamageable` for harm and its
`IPressable` for what anyone can do to a person, so a pawn and a sleeper cannot drift apart. Decisions
with no engine in them, such as who may enter the city, what an offer needs and how a staff command
reads, are in `Code/Logic` with unit tests.

## Four laws

What costs a roleplay server its players is cheap death, metagaming, duplicated items and worthless
money, disputes nobody can settle, and staff abuse. NutScript and Helix leave all of it to a
rulebook. Hexagon enforces it, and every feature is judged against four laws: mortality, perception,
conservation and memory. The README states them; the sections below are how each is kept.

## Death is final, and the dead are kept

A character that can come back has nothing to lose. Harm puts a character down rather than killing
them: at no health a character is down, visible to all, unable to act or to leave the city, and part
of the scene. Anyone can help them up, search them or treat them; finishing them is its own verb,
journaled with its witnesses, and a game can require a capability for it (`FinishRequires`). After
`DownedSeconds` a downed character gets back up, or dies if the game sets `BleedOutKills`. The one
exception is a weapon made to kill (`Lethal`), such as a pistol: a shot from it to the head kills
outright, whether the one shot is standing or already down. How hurt someone else is cannot be read
off them; that they are down, bound, armed, limping or breathing hard can.

Death is `Permanent` unless a game turns respawning on (`HexagonServices.Death`), as the tests may. The
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

## Harm lands somewhere

Harm is a blow: how much, where on the body it lands, whether it wounds, and whether the weapon is made
to kill. The host judges where. A shot is traced from the attacker's eyes, as the host places them,
along the aim they claim, against the hitboxes of the bodies in its way: the engine's `ModelHitboxes`,
from each body's own model, kept on the host alone, whose tags name the head, the body, an arm or a
leg. The capsule a body walks with has a tag of its own and is left out, or it would take every shot.
A limp body is struck where its parts are, which are named for their bones.

A weapon's wound to an arm or a leg stays on the character until it is treated; nothing heals by
itself, and a restore brings a character back wounds and all. A wounded leg walks, slowly
(`LimpSpeed`), and neither runs nor jumps; the host holds it to that speed. A wounded arm drops what
is in hand onto the ground, and holds nothing until it is treated. Fists only hurt. An item lists the
wounds it treats (`Treats`) and what it heals: used on oneself, or held and used on someone else with
Treat, it closes the oldest wound it can, gives back what it heals, and is used up. On someone it can
do nothing for, it is kept. Wounds, treatments and where each shot landed are journaled.

## A body is the engine's ragdoll

Someone down or dead lies as the engine's ragdoll, `ModelPhysics`, in the model of their look. The host
owns it and simulates it, and the engine sends where its parts are to those it is sent to. It falls from
the pose of the body it replaces, pushed by the blow that did it, and a shot pushes it again. A body
with nothing to fall from, after a restart, is laid on its back with its middle where it was kept;
laid with its feet there instead, it once moved its own height at every restart. Where it lies is
saved with the city, and staff moving someone down move the body. The body's model is set on a
dedicated server too, though nobody there is dressed, because where a shot lands and how a body falls
are the model's.

Someone down is that body, whether or not their player is connected: their pawn holds still, unseen
and not solid, and its player looks out of the body's eyes, from the ground, and can look about.
Helped up, they stand where it lies. What anyone near notices is shown by the body itself
(`BodyCues`): a limp in the walk, bound hands behind the back by the engine's hand IK, a flinch where
a blow lands, and the hard breathing of someone badly hurt or down, which they and anyone close hear.
The breathing is a game's own sound (`BreathingSound`); Hexagon ships none.

## Leaving does not end a scene

A character's body is a `Sleeper` whenever its pawn does not carry it: limp on the ground while it is
down, connected or not, and standing bound where it was if its player left it with its hands tied. It
keeps bleeding, and anyone can help it up, search it, treat it, release it or finish it exactly as if
the player were there. When the player returns with that character they pick up from there: bound,
back in their pawn; down, still looking out of the body on the ground. Whoever is down or bound when
the server stops is put back the same way when it starts, where the closing save found them. Someone
who leaves on their feet leaves nothing behind: a city of the logged-off would be a city of loot. The
character leaves its pawn before its sleeper is made: removing an object waits for the end of the
frame, and a sleeper that looked before then took the character for still being in the city and
removed itself.

## Sight is a distance

The engine sends every networked object to every client unless told otherwise. Pawns, sleepers,
bodies and dropped items are spawned with `AlwaysTransmit` off and implement `INetworkVisible`: the
host sends one to a client only while that client's character is within `HexagonServices.SightRange` of
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
Out-of-character chat carries the player's name, not the character's. A faction and a rank are told
only to their own member. Staff are the exception, through `who` and the journal, and both are journaled.

## Everyone arrives the same way

A character is made with a body that is its own for life: the engine's citizen, or one of the human
skins a game lists, each with its own model. It chooses hair the game lists and clothes from the
game's wardrobe, which are items the game marks as such, and arrives wearing them, carrying nothing
but the papers the game gives every arrival: its form, with the character's name in the bearer's
blank and the issuing office's stamp on it. The clothes and the papers are issued from the named
source `arrival`, so they are accounted for like anything else. The stamp itself is an item somewhere
in the world, so whoever holds it can issue papers too. It belongs to no faction. A faction is joined in the city, by recruitment, or by
staff appointing someone with `faction`. There are no whitelists: a whitelist decides who belongs to
a faction outside the world, before anyone in it has met them.

## Clothes are items, and what is worn is seen

Clothing is an item whose asset names the engine clothing piece it is worn as. Put on, it stays in
the inventory marked worn and takes no cells. Taking it off needs room for it, and something worn is
taken off before it is dropped, put away, offered or moved about. Nothing is taken off to make way:
the engine says which pieces cannot be worn together, and the wearer takes off what is in the way
first. Nothing about a character changes by itself, either. Joining a faction changes what the
character belongs to and nothing else; a uniform is handed over and put on like anything else.

A look is the body a character was made with, its hair, and what it wears over them, and a hat hides
the hair under it. It is synced to everyone who can see it, because it is appearance. So a uniform
says "dressed as Civil Protection", which is what an onlooker would know, and a stolen uniform says
the same. Searching someone down or bound, or a body, shows what they wear, and taking a piece takes
it off them, which everyone sees. A body lies in what its character wore. What is in a hand is seen
the same way. An item with no model is a small block in its tint, in the world and in the hand, and
an item with no picture is its title in an inventory; clothing shows the engine's own picture.

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
`scene`, a container a scene's author stocked, which is filled the first time it is opened and never
refills, `arrival`, the clothes a new character chose to arrive in and the papers it arrives with, or
`made`, what a press or a tool made. They leave through `consumed`, `operator`, `made`, what making
something used up, `character.deleted`, `character.retired`, or `character.died` when a death
leaves no body. A new character arrives with only its clothes and its papers. There are
no wages, vendors or starting kits: they made money from nothing and so made it worthless. Money is
an item like any other, money only because people take it.

Identical things pile up, as many to a slot as the item's asset says. Every issue, move and destroy
records a count, so the supply of anything is the journal's issues minus its destroys, and a test
holds the supply of pistol rounds to it. A whole pile that lands in a slot of its own keeps its id;
part of a pile is a new pile. Splitting and merging move nothing between holders and are journaled
anyway, because "where did the other ten go" is a question a dispute asks. All of a transfer happens
or none of it does. Deliveries come later.

## Locks take keys, and a faction grants nothing

A permission that lives in an object can be stolen, lent and confiscated; a flag on a faction cannot.
So the host asks `Player.HostCan( capability )`, answered only from what the character carries, and
a faction is a title, a description, a tint and its ranks, and grants and changes nothing: a rank says
only what its holder may do to those below them in it. A door or a container has a
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

## Rank is given in person, by a superior

A faction lists its ranks, lowest first, and each says whether whoever holds it may enlist, promote,
up to which rank, and discharge. Staff appoint a faction's head once, with `faction`; everyone else
is brought in and raised in the world. Enlisting is an offer, like handing something over: the
recruit accepts or refuses, and joins at the lowest rank. Promoting, demoting and discharging are a
superior's act on someone below them in the same faction, and nobody acts on their own rank or above
it. A member can walk out of their own accord. Every one of these is journaled, refusals too. The
verb menu lists enlisting, promoting and discharging only to someone whose rank allows them, and the
host checks again.

Enlisting changes only the record. Papers and a uniform are separate acts: the recruiter fills in and
stamps the faction's form and hands over a uniform from stock, and being discharged takes nothing off
anyone. A rank names its insignia, an item the superior hands over and the member puts on. Onlookers
see the insignia someone wears, which is clothing tagged `insignia`, and not their rank: a recruit
still wearing a recruit's armband looks like one after a promotion, and an armband taken from a
locker says the same as one earned.

## A radio is an object with a dial

The frequency is on the radio, not the character, so whoever takes a radio takes its setting: a
searched officer's radio is tuned to the officers' channel. `/r` reaches everyone else carrying a
radio tuned the same, wherever they are, as a voice: a name if the listener was given one, otherwise
nothing, because a radio shows no face. Anyone within speaking distance hears someone talking into a
radio. Someone down or bound cannot reach theirs. People beside a listener do not yet overhear it.

## Ink stays, and a stamp makes a paper official

Paper is an item whose asset says how many lines it takes, and a printed form is one whose asset
prints blanks, each with its label. Writing takes something whose item grants `paper.write`, a pen
or a pencil, and what is written stays: a line is only ever added below the last until the sheet is
full, and a blank is filled in once. A stamp's item names the mark it leaves, such as an office's
name, and every stamp of that kind leaves the same mark, over whatever is written by then, so a line
below a stamp was written after it. What is on a sheet goes with it through every hand, holder and
restart. Written on, a pile of blank sheets gives up one, and a sheet with anything on it piles with
nothing.

A paper says only what is on it: text and stamps, never whose hand put them there. Anyone can sign
any name to a letter or fill any name into a blank form, and the journal records who really did,
every line, blank and stamp, refused ones too. So forgery is possible, and found out afterwards, and
what makes a paper official is a stamp, which takes the stamp itself. There is no issuing system. An
office issues papers by having its stamp, and a recruiter fills in and stamps a paybook.

What is on a sheet is not sent with an inventory. `Seen` leaves it out, as it shows a key by its cut,
and a sheet is read one at a time by whoever holds it. Holding one up for someone within reach lets
them read it while it stays in the holder's hand, until they part, either is down, or it leaves the
holder's hands. Handing it over is an offer like anything else. HL2RP gives every arrival an
identification card stamped by the Civil Authority, whose stamp is in the Civil Protection locker.

## A map is laid out, leant over and marked in grease pencil

A planning map is an item whose asset names the picture printed on it, how big it is laid out, and
the counters that come with it, one of each colour. Its holder lays it out on a table, or anything
flat, in front of them, where it lies like anything put down: it can be picked up, carried, handed
over and stolen, and what is on it goes with it. Laid out, it is the engine's `WorldPanel`, painting
the picture, the marks and the counters, and leaning over it turns the view straight down at it. The
mouse works on the map itself, through the engine's `WorldInput`, so there is no window to draw in.

Marking a map takes something to write with, as paper does, an item that grants `paper.write`, and it
marks in that item's colour: HL2RP's grease pencil red, a pen blue. Whatever drew it, what goes on is
grease pencil: lines and short words, up to 48 at once, which anyone leaning over the map can wipe
off, leaving nothing behind. The counters
are moved by hand, onto the map and back into their box beside it. Like a paper, a map says only
what is on it, and the journal records whose hand made, wiped or moved each thing, refusals too.

What is on a map is not sent with an inventory, as with a sheet. Laid out, it is shown to whoever can
see it, each mark once it is finished; only the hand drawing a line sees it as it goes. Someone
leaning stands up when they walk away from it, are down or bound, or it is picked up. HL2RP's city
map is a picture of its own streets, taken once from straight above through the engine's
orthographic camera (the test driver's `mapshot`), and its planning table stands beside a case
holding the map and grease pencils.

## Money is made by someone, from something

A machine placed in the world, such as a press, is a holder: it is loaded like a crate, and makes
from what it holds only while someone works it, holding Use for as long as the work takes. What it
makes it keeps, for whoever opens it next, and a lock keeps the opening and the working to whoever
has a key. A tool, such as a die, is an item whose asset lists what it makes, and it works in its
holder's hand on what they carry. Either way, letting go, walking away, putting the tool down, or
being down or bound stops the work, and then nothing is made and nothing used up. What it makes from
leaves through the `made` sink and what it makes enters through the `made` source, as one transfer,
so the supply of money is still the journal's issues minus its destroys. The work is timed by the
host, and the HUD shows how far along it is.

Whatever makes a thing leaves its mark on it, a press its own and a die its own, and a press can
print a serial on each thing, numbered on from the last, as on a banknote. A pile keeps one mark for
each thing in it, so handing over some of it hands over those particular things, and the mark goes
with each through every hand and pile. The mark never leaves the host: a client is sent a serial,
which anyone can read, and nothing of what made it. Someone carrying a loupe, which grants
`item.compare`, can look hard at a pile and is told only whether its things were made alike, and
which go together, never what made them. So a forger's tokens pass among the untrained, and a
trained eye with one known to be good can tell them apart. Knowing which notes an office issued is
a matter of keeping a register of its serials, which the game does not keep for it. HL2RP's token
press stands locked to the city's keycard and strikes tokens from blanks the locker holds; a little
way off, a forger's crude press does the same for anyone, beside a crate holding blanks, a die and
a loupe.

## The HUD shows what a character would know

In the city the HUD shows prompts and chat. Health is not a number, because nobody knows their health
as one: being hurt darkens the edges of sight and drains the colour, and being down narrows, dims and
blurs the view from the ground without hiding it. These are the engine's own post-processing
(`HarmCues`): two global volumes with its vignette, colour adjustment and blur, weighted by the
character's state, blended over whatever the game's camera does. At its defaults the engine's
vignette left someone down all but blind, and someone down still sees who comes near, so it was
eased until the world shows through.

What is looked at is described by its own `IPressable` tooltip, as the engine's `PlayerController`
gathers it: an icon, a title and a line about it, with the key that acts on it drawn by the engine as
the player has it bound (`Input.GetGlyph`), keyboard or controller. Items move by dragging, the
engine's panel input: onto another cell, onto a pile of the same thing, across into the open holder
or out of it, or out of the window to drop it on the ground. A click still chooses an item for the
buttons. The engine's own platform chat reaches every player by account name, whatever the distance,
so the host warns at startup if a game leaves it on.

The HUD is one `PanelComponent`, `Hud`, made of panels: the character list, what is looked at, the verb
menu, offers, chat, the keys, the inventory and the staff console. Each rebuilds only when what it
shows changes.

## Staff use the console's commands, as themselves

`Operators.Execute` is the one implementation behind both the server console and the in-game staff
panel, so there is no separate admin mod to trust. Staff status is set only from the console, so it
cannot be granted or taken from inside the game. Every line staff type is journaled before it runs,
under their name or as the console, refusals included, and reading the journal is itself an entry.

A dispute ends with something put back, so staff can: `take` sends items out through the `operator`
sink, `move` carries them between characters by the same transfers as anything else, `release` frees
bound hands, `revive` helps someone up, `faction` appoints a character to a faction, at its head
unless a rank is named, or, with `none`, takes it out of one, `door` works a door whatever key it asks for or assigns and releases its title,
and `restore` undoes a death that broke the rules. Commands that name a character find it whether or
not it is in the city, except `teleport`, `bring`, `goto` and `hurt`, which need it there.

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

`DevDriver`, `DevCommands` and `DevSystem` exist only for these, and only in debug builds: what is
published is compiled without them. The component is never placed in a scene; outside the editor the
commands run only when both ends were started with `+hexagon_dev 1`, and a client never runs console
commands for a server. Game code knows nothing of them: `DevSystem` suppresses the engine's input and
holds the test camera as an `ICameraModifier`. They call the same client requests the HUD calls, so they cannot
pass a rule the real path would fail. A client a test drives takes no keyboard or mouse input until its
session ends, because the test clients are real windows with focus and a journal once showed someone at
the machine walking one about; a refused command sets nothing, so no server can take a player's keys.
What the tests cannot do is click: the HUD is looked at in pictures the editor test saves, and played
by a person.

A test says where its characters stand by what is there, beside `Door 1` or behind the `Crate`, never
by coordinates. `spot` finds the object by name, goes out from the face on the side asked for by its
own facing, and finds the floor there, so moving the map about does not break the tests, and a check
on where someone is asks whether they are near the place rather than matching numbers. On the host it
also finds a character, whose front is the way their body faces, and `part` says where the middle of
a part of their body is, so a test shoots at what the host would judge.

While working, the editor stays open: `dotnet test Tests` for a rule, and `playtest.ps1 -Attach`,
which plays in that editor once it has built the edit, in about forty seconds, or less with `-Until`.
It points the editor at the run's own data folder and asks again, and points it back after. Before
merging, `tools/test.ps1` runs verify and both play tests as one, in five to six minutes.

The two-client test spends half its time starting a server and two clients, so with `-Keep` it
leaves them running, and the next run starts the city over on them: `fresh` loads the scene again,
which brings everyone connected back into it, and opens the city on an empty data folder. The
server's output goes to a file rather than to the script, so it outlives the run. Anything that
changed the code or an asset since starts them again, because what they loaded is no longer what
is being tested. The run stops at its first failed check, since everything after it plays on a
city it left wrong.

## What the play tests taught

- A failed run keeps its data folder. Its journal has explained every run that looked unexplained.
- A dedicated server reads only compiled assets, and only the editor compiles them. A run once
  played a faction with none of the ranks just written into it, and could not find the new items
  at all, so the two-client test now has the editor compile what changed before it starts.
- A client sometimes drops its first connection and never tries again, and a run once waited out
  its whole timeout on one. A client that has not joined in two and a half minutes is started
  again.
- A game started on a dedicated server is loaded into an empty scene, which keeps no note of the
  file it came from, so `fresh` loads again the scene the game started on.
- A character was once believed through a roof onto a door: it had spawned on one of the map's own
  spawn points, and falls were free. A game's own spawn points now come first, falls are limited by
  gravity, and every claim is traced. The editor test tries exactly that drop, and steps through a
  locked door.
- A body at rest is asleep, and one teleported into the air asleep hung there. A teleport wakes it,
  and makes the engine's controller forget the ground it stood on: on ground the controller turns
  gravity off, and a body woken without gravity went back to sleep in the air.
- The engine's `GetComponent` passes over a disabled component unless asked not to. A body's model
  is off until the body is revealed, so a lookup without `includeDisabled` found none, and limp
  bodies and the dead were never shown.
- Shots at a leg and an arm once struck a wall: their places were measured from a door an earlier
  section had opened, and an open door is turned. Places are measured from what never moves. Each
  machine also animates a body for itself, a few units apart, so a client aiming at its own copy of
  an arm could miss the host's, and one shot at a far leg struck the near arm. The shooter now stands
  in front of the one shot, and aims where the host has the part.
- Even then every shot at someone standing landed on "the body". On the host it struck their walking
  capsule, which a shot is meant to pass by its `capsule` tag. The engine's controller makes its
  colliders when it is added, before the pawn gave it that tag set, and it never reapplies it; the
  clients receive the pawn with the set, so only the host, which judges, was wrong. The pawn now
  tells the colliders the set once it has one.
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
- Text in a panel's markup is set into a label of its own, and the HUD styles every label, so the ink
  on a sheet first came out in the HUD's grey, with its margins. Text on a sheet is set straight into
  labels, each with a class for what it is. A blend mode drew the stamp's own box over the paper, so
  a stamp is plain ink.
- The stationery box was first put where the map's floor is raised, sunk into it, and the spot in
  front of it found no ground. Something put in a scene by hand is put where a trace down finds the
  floor.
- The planning table was first put across the edge of the raised floor, as the stationery box had
  been, so nothing could be laid out on it. For something that big a trace down at one spot is not
  enough: the floor under all of it is traced first.
- A word painted on a panel came out as nothing. A font's size is in the panel's own units, which
  the painter scales to pixels, and given in pixels, the text outgrew the box it was painted in and
  the painter dropped it.
- Lit, a world panel lying under a roof took about four times the light of the table top beside it,
  in the sun's colour. A map is unlit.
- Saving a scene from the editor rewrites all of it: it drops the scene's title and description and
  adds settings of its own. A play test's scene is edited as text, with the editor's copy closed.
- The engine gives a dedicated server ten seconds to log on to Steam. When Steam was slower, the
  server never listened, no client could join, and the two-client test waited out its fifteen minutes
  and then deleted its log. A server that says so is now started again with its clients, and a run
  that gives up keeps its log and data.

## What is thin, and known

- Weapons hit by a host ray along the attacker's claimed aim, against the bodies' hitboxes. There is
  no recoil, spread or cover.
- A shot is judged against where bodies are on the host when it arrives, with no allowance for what
  the attacker saw: the engine has no lag compensation. Over a slow connection, someone moving is
  harder to hit than they look.
- A ragdoll is announced with its model, the citizen or a human skin, to every client, before the
  reveal's hold. A client far away can learn which kind of body lies somewhere, though not whose.
- Bodies cannot be dragged yet; that comes with stealth.
- A body emptied of what it carried is removed, so someone who dies carrying nothing leaves no body.
- The engine ships no weapon models, so HL2RP's pistol is a block held in a pistol stance.
- A new character's body, hair and clothes are chosen from pictures; there is no preview of the
  character wearing them yet.
- The HUD has been looked at in pictures, not yet played by a person.
- A radio is not overheard beside a listener.
- There is no handwriting among the engine's fonts, so ink is the HUD's font, leaning.
- A paper is read in a window, not in the hand: nobody sees from across a room that someone is
  reading, and a sheet held up for someone shows no model.
- Leaning over a map has no pose: the body stands as it was while its view looks down at the map.
- Others see a line or a word on a map only once it is finished, not as it is drawn.
- Insignia have no models yet: HL2RP's armbands are clothing with no model, seen only as words in
  a person's tooltip.
- A map's counters are discs painted on it, not things standing on the table, and the map is unlit,
  so it does not darken with the room.
- Presses and dies have no models of their own: HL2RP's presses are crates, and a die is a word in
  the inventory. Working shows no pose.
- Sight is a distance, not a line: a client is sent everyone within `SightRange` of its character,
  through walls. Doors and containers are scene objects, sent to every client.
- The engine tells every client who is connected, by Steam name, and which connection owns each pawn
  it is sent. Hexagon keeps character names and factions from clients, but not which account a pawn
  in view belongs to. Closing that needs the engine.
- The engine announces every new networked object to every client, so a client far away learns that
  something was spawned at a place, though not what. Closing that needs the engine too.
- The city is saved every `AutosaveSeconds` and when its scene is destroyed. Whether stopping a
  dedicated server with Ctrl+C destroys it first has not been tried.
- Unit tests and the confusables check run on a hosted runner on every push. Both play tests need
  s&box and stay local.
- The play tests run both clients as one Steam account, so account-level rules are tested by one
  account, and the two-client test allows that account two lives at once.
- The vision's deliveries and player offices are not built. Until they are, things enter the world
  only from staff, from stocked containers, with new characters, and made from those.

HL2RP keeps what is about its setting: the scanner, civic records as a view of the journal,
forcefields and ration dispensers.
