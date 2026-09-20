# Hosting an evening

How to run a game built on Hexagon on a dedicated server, and how to do the hosted session that
[v0.1](v0.1.md) asks for. What is marked **checked** is what `tools/playtest-server.ps1` does on
every run. What is marked **not yet checked** has only been done over loopback on one machine and
one Steam account; the hosted session is what checks it.

## Start the server

On the hosting machine: s&box installed through Steam, both repositories cloned side by side, and
the `Libraries/hexagon` junction made as in the [README](../README.md).

```bash
& "C:\Program Files (x86)\Steam\steamapps\common\sbox\sbox-server.exe" +game "C:\path\to\hl2rp-hexagon\hl2rp.sbproj" +hexagon_data_root evening
```

- **Checked:** the server starts, loads the scene, accepts clients and saves under
  `sbox\data\kbj\hl2rp#local\evening`.
- `+hexagon_data_root` names the data folder. Leave it the same to keep the city; change it to start
  a new one.
- Never pass `+hexagon_dev 1` to a real server. It opens the test driver.
- **Not yet checked:** joining from another machine. `+net_allow_local 1`, which the play test
  uses, is for loopback only and should be left off. How friends find the server (the server
  list, or `connect` with its Steam id) has not been tried, and is the first thing the hosted
  session will find out. Write down what worked here.

## Make someone staff

In the server's console, once:

```
hexagon_staff <steamid64> 1
```

Staff status can only be given from the console. From then on that player presses Tab in game and
types the same commands without the `hexagon_` prefix: `who`, `whitelist`, `give`, `bring`, `goto`,
`hurt`, `revive`, `payday`, `journal`. Every line is journaled under their name before it runs.

## Settle a dispute

`journal [text] [count]` in the staff panel shows the latest entries containing the text. Useful
searches: a character's name, `REFUSED`, `combat.attack`, `character.death`, `item.move`,
`tokens.move`, `verb.door.buy`. Each entry says who, what, where, whether it was allowed, and who
was close enough to witness it. The files are `journal/<date>.jsonl` in the data folder, one JSON
object per line, and can be read while the server runs.

## Restart

Stop the server with Ctrl+C or by closing it, and start it again with the same data root.
**Checked** (in the editor play test): characters, what they know, health, being down or bound,
what is in hand, inventories, crates, bodies, dropped items, doors and their owners, and whitelists
all return. Characters come back where they stood. Players rejoin and pick their character again.

## Back up

Copy the data folder. It can be copied while the server runs. **Checked:** the two-client play
test copies the folder mid-session, starts a second server on the copy, and compares every
character's tokens, items, health and acquaintances with the original.

Every document is written twice, first to a pending copy beside it, so a copy of the folder taken
mid-write still holds one complete version, and loading prefers whichever is whole. The journal is
appended to, so a copy may end one entry early; a torn last line is skipped when read.

## The v0.1 session

Section 4 of [v0.1.md](v0.1.md), as a script for the evening:

1. Host on a machine nobody is playing on. Three people join on their own accounts.
2. Make one of them staff. They whitelist another for Civil Protection, in game.
3. Play for an hour: talk, trade, lock doors, use the radio, have one fight and one arrest.
4. Have one argument about who did what, and settle it from `journal` without leaving the game.
5. Restart the server in the middle. Everyone rejoins and checks where they are and what they have.
6. Copy the data folder while it runs, start a second server on the copy, and look at `who`.
7. Afterwards, search the server's output for `Exception`, `[store]` and `[journal]`. There should be none.
