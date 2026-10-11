# Vision

This is where Hexagon is going. Where the code disagrees with it, the code changes.
`decisions.md` records what is built today, and why.

## The thesis

If you can just respawn, roleplay is gone. A character who can come back has nothing to lose, so
nothing they do means much. Permadeath is the source of real roleplay: fear when a gun is drawn,
a reason to surrender, to keep your word, to run, to trust someone or not. Everything in Hexagon
serves those stakes.

## The game it is for

Berlin, 1942. You are in the military, going to the Bendlerblock for war planning. You walk into
the planning room and plan, and outside, cars drive past. No HUD shows who is on the server. No
counter says "Reichsmarks: 50000": money is coins and paper in your pocket. If you die, you are
dead, and your character is gone.

This is 1942RP, the Garry's Mod genre of Typhon Networks, Prometheus Networks and People of Gaming,
done properly. On those servers death was cheap, and permanent death was a ticket: staff approved a
"PK" according to their own rank, and the player could appeal. Money came from salaries, money
printers and donor packages. Rank came from applications, whitelists and time gates, and players
waited for staff to run events. Hexagon moves all of that into the world. The game, not a ticket,
decides what death means. Money is made by someone, from something. What happens is what players do.

The game is `1942rp`. HL2RP is the demo: the smallest game that shows every part of Hexagon, for
people learning to build on it.

## Four laws

### Mortality: death is final

- A wound puts you down. Dying takes bleeding out, or someone finishing you deliberately in front
  of witnesses, so there is time to be rescued, taken prisoner or executed.
- A headshot from a weapon marked lethal kills outright.
- What you carried stays on the body.
- An account has one living character. After a death, the player can make a new character at once,
  and it enters the city 24 hours after the death.
- A player can retire their character, though not while it is down or bound. It counts as a death:
  the character leaves for good, taking what it carries, and the new one waits 24 hours.
- Staff can restore a character killed by a rule break, an exploit or a bug, on the record. A death
  within the rules is final. A restored character gets up where its body lies, with whatever is
  still on it, and staff move back anything taken. A restore is refused while the account already
  has its living character; that one is retired first, or deleted if it never lived.
- A character that never entered the city never lived. It can be deleted, which frees its name and
  starts no wait.
- The dead keep their names. Nobody can take the name of someone who died.
- Respawning is a switch for tests and other games, and it is off by default.

### Conservation: nothing comes from nothing

- Every coin, banknote, rifle and ration exists because someone made it from something else. After
  that it only moves: between holders, through a machine, into the ground.
- Raw materials enter the world in two ways. Deliveries arrive at depots by truck and train on a
  schedule: finite, robbable and on the record. Sites in the map, such as mines, farms and forests,
  yield slowly to whoever works them.
- The world as built can hold goods: a scene's author may stock a crate or a vault. Nothing refills
  except by delivery or by work.
- Money is just items. Hexagon has no idea of money: a note or a coin is an item like any other, and
  people decide what counts as money, as cigarettes did in Berlin in 1945. With a gold ingot and a
  press you can strike coins. With paper, ink and a printing press you can print banknotes. Real
  monetary history is the guide.
- A banknote carries a serial and remembers which press printed it. Someone with the right tool or
  training can examine a note and judge it, and an untrained eye cannot tell. A central bank's
  players can keep a register of the serials they issued and check notes against it. The game does
  not keep one for them.
- There are no vendors. All trade is between players, by handing things over.
- A new character arrives with only their clothes. Everything else is issued, earned, found, or
  bought from someone who has it.
- Property belongs to someone, who sells or rents it. A building nobody has bought belongs to an
  office run by players, such as the housing office, and the money goes into its till. Staff assign
  some buildings outright. An owner hands a door over by offering it, like any item.
- Doors open to keys, not to teams. Every lock has a pattern, and a key fits every lock with its
  pattern, so all the Interior Ministry's doors can share one while each flat has its own. Owning a
  door is title only, the right to sell or rent it: locking takes the key, owner or not.
- A key shows its cut, a code anyone holding it can compare with another key's, never which lock it
  opens. Whoever finds a key learns what it opens by trying it.
- Staff can still create items with `give`. It is the one exception, and every use is journaled
  under their name.

### Perception: you know what your character knows

- A client receives only what its character could see or hear.
- There is no player list, no money on screen, and no name over a stranger's head.
- During play the HUD shows only prompts and chat. Being hurt shows through the screen and the body:
  blur, muted colour, a limp, laboured breathing. Being down or bound shows the same way.

### Memory: everything is on the record

- Every decision the host makes is written down, with who, where and who saw it. When death is
  final, staff need the record to judge whether a death was fair, and the world needs to remember
  who did what.

## Society

- You join a faction such as the Heer by being recruited in the world. A recruiter enlists you in
  person, which issues your papers and uniform. There are no forum applications.
- A faction is people, not powers. Membership grants no abilities: everything a character can do
  comes from what they carry. Keys open locks, ties restrain, papers prove who you are.
- Rank is given by a superior's act, in person. It lives on the character and shows on the uniform.
  Staff appoint the head of each faction once, and everyone below is promoted in the world.
- Written documents are items: paper you write on (orders, letters, notes), identity papers
  (Kennkarte and Soldbuch), maps on the planning table that people mark up together, and stamps and
  seals that make a document official.
- Handing something to someone is an offer they accept or refuse. Slipping something into someone's
  pockets is a different act, done by stealth.

## How a feature is judged

- Does it raise the stakes, or offer a way around them?
- Could it happen in the world? If it only works through a menu, a timer or a staff member, look
  for the physical way first.
- Where did it come from, and where does it go?
- What does the character actually know, and who would see it happen?
- Does it need staff to work? Then it is a gap, not a feature.

## Not in Hexagon

- Paychecks, vendors, and money printers that make money from nothing.
- A HUD showing money, health as a number, or who is online.
- Death settled by tickets and appeals.
- Anything bought with real money that changes the game. Supporters may get a badge or a cosmetic
  outside play, never money, items, ranks or roles.
