# CatchUp - the Arms Race catch-up boosts

The module behind [catch-up](../../docs/reference.md#catch-up): every enemy hit takes its boost from the gap between its two players on the
ladder - the one behind hits harder and takes less from the one ahead, two players within a kill of each other play plain Counter-Strike -
each player gets their own extras by their gap to the leader, and the leader a bounty. The reference has the rules as players meet them and
the tables; this page is how the module does it. It is not
a plugin: the OverStrike2 plugin compiles these files in as source and constructs the module in its `Load`. Nothing here is deployed
or loaded on its own.

| File | What |
|---|---|
| `CatchUpRules.cs` | The decisions, free of CounterStrikeSharp: a hit's boost from the gap between its two players (`Rules.PairFor`), each player's own boost and the bounty (`Rules.Decide`), decided again in the middle of a life (`Rules.Recalculate`), one hit (`Rules.Hit`), the healing, the timing, and what one life took (`LifeTally`). |
| `Ladder.cs` | The kill counter (`Ladder`), the standings and the weapon names, free of CounterStrikeSharp too. |
| `CatchUpWords.cs` | The words: the card's rows, the centre panel's lines, one sentence for chat and the log, the log's lines - a spawn's decision, a change mid-life, a life's end. |
| `CatchUpConfig.cs` | The settings and their defaults; reads `catchup.json` and never throws: a missing or unreadable file means the defaults, and the log says so. |
| `CatchUpModule.cs` | The game half: registers the events through the host, applies what the rules decide, the `os_catchup` command. |
| `catchup.json` | The tuned numbers - the boost table and the bounty - equal to the built-in defaults (the self-test fails when they drift apart). It ships beside the plugin's DLL. |
| `SelfTest/` | `dotnet run`: every decision without a game server; hits to kill with a model of CS2's armour (`Cs2Hits.cs`) for the worked examples of 2026-11-08 and Vampire's numbers (`bridge/Shared/Vampire.cs`, linked in); and the kill counter against six whole real matches (`armsrace-*.txt`, anonymised). `-- --table` also prints the pair table, the cards and the panels. |

## How the plugin hosts it

- `plugin/OverStrike2/OverStrike2.csproj` compiles the five source files by name and copies `catchup.json` beside the DLL. By name,
  never a wildcard over this folder: `SelfTest/` and the build output hold other C# files. A new file here is a new line there.
- `new CatchUpModule(host, logger, configPath)`, then `Load(hotReload)` in the host's `Load`. Everything registers through the host,
  which CounterStrikeSharp removes with it, so nothing is needed in `Unload`. Loaded in the middle of a map (a hot reload), it counts
  nothing until the next match starts.
- At load it compiles its own methods ahead (`CatchUp: N methods compiled ahead in M ms`), so the first spawn or hit does not pay for
  the JIT. Nothing heavy runs inside a player's own callback (spawn, hit, kill, shot): those only count and queue, and the work is done a
  frame later or on a timer.
- For the host: `Show` is how the message is shown (the player, the boost and the centre panel's HTML of it) - the plugin draws the
  HUD's card there, or the centre panel for a game not known to hold the card, and keeps either up 5 s; `Switch(on)` is catch-up's fun
  setting, which the plugin turns on while the setting is on and the map is Arms Race; `Status()` is what `os_catchup` prints.
- It registers the events `round_start`, `player_spawn`, `player_death`, `player_hurt`, `weapon_fire`, `player_team` and
  `player_disconnect` (all post), the listeners `OnClientPutInServer`, `OnMapStart` and `OnPlayerTakeDamagePre`, one repeating timer
  (regeneration's, every 0.5 s) and the server command `os_catchup`. All of them coexist with the host's own handlers.
- It reads one file, `catchup.json` at the path the host passes, and logs through the host's logger.

## The console command

`os_catchup`, from the server console or RCON only; a client typing it gets nothing.

| Command | Effect |
|---|---|
| `os_catchup` | The status: on or off and why, counting this match (rungs learned, placements) or waiting for a match start, free-for-all, headshots only and the game's spawn protection; then top, second, leader, lead and bounty, and per player their progress, level, kills behind and what this life has. |
| `os_catchup reload` | Reads `catchup.json` again; the next spawns use it. |
| `os_catchup on` / `off` | Until the plugin reloads, or catch-up's fun setting switches it. `on` also clears a stop after errors. |
| `os_catchup show <player> <behind>` or `<player> lead <n>` | Shows that player, now, what a gap (or a lead) gives. Changes nothing. |
| `os_catchup give <player> <behind>` or `<player> lead <n>` | Also gives their current life that boost - their own extras, protection from now on, and in every fight the boost of a player that many kills behind (a lead: that many ahead, with the bounty) - kept until they die and not decided again: to try the effects with two players. |

`<player>` is `#<slot>`, an account id, `[U:1:<id>]` or a SteamID64.

## How each boost is applied

| Boost | How |
|---|---|
| The pair | `OnPlayerTakeDamagePre`, on enemy hits only, before the game applies armour: the gap g = the victim's progress minus the attacker's, from the counter at that moment (`Rules.PairFor`). g of 2 or more: the attacker's row's damage dealt, x (1 + damage dealt). -2 or less: the victim's row, x (1 + damage taken) / (1 + health) - a tier's health counts as resistance against the player ahead, so nobody's health is raised. Within one kill: x1. Then x (1 + the victim's bounty). |
| Regeneration | The player's own, by their gap to the leader. Every 0.5 s, once `RegenDelaySeconds` have passed since the player was last hurt by anything (a fall too): a share of the spawn health per second, the fractions carried over, up to the health the life spawned with. |
| Second chance | The pair's: a hit by a player at least the second chance's row ahead (4 by default) that would kill (damage at least the health) is cut to leave 1 health, once per life. Armour, which the game applies after, only leaves more. |
| Extra spawn protection | The player's own, decided at the spawn. Enemy hits are blocked until the spawn + the game's own protection (`mp_respawn_immunitytime`) + the extra seconds, ended by the player's first shot or hit. The game's protection is left alone, and the extra seconds show nothing to attackers. |
| Lifesteal | The pair's: on `player_hurt` by an enemy at least 2 ahead, the row's share of the health the victim lost (at most their maximum health), a frame later, up to the health the attacker's life spawned with. At most half a hit (`CatchUpConfig.MaxLifesteal`); Vampire takes only what it leaves of that half. |
| One-hit knife and Zeus | The pair's: a knife or Zeus hit on a player at least the row ahead (7 by default) - by the weapon the damage names, else the one in hand; never a blast or a fire - is raised to 4 x (health + armour) + 100. |
| Infinite ammo | The player's own, by their gap to the leader. After every shot of a gun, a frame later, the clip is refilled to the gun's `MaxClip1`. Not grenades, the knife or the Zeus. |

**The message** comes 0.25 s after the spawn, or 3 s after it at the first spawn after joining. A message whose player died first is
dropped: the next spawn says its own. A boost decided again in the middle of a life is silent.

**In the middle of a life** - a frame after every change to the ladder - every living player's own boost is decided again: regeneration,
infinite ammo and the bounty follow it; the spawn protection stays the spawn's. The fight needs no deciding: every hit looks its own gap up.
The log's lines show each boost's duel factor against the leader, its row's own factor - (1 + health + lifesteal) x (1 + damage dealt) /
(1 + damage taken) - times (1 + the leader's bounty): 1 is an even fight. Between any two players g kills apart the duel is that row's own
factor.

**A life's end** goes to the log as one `CatchUp: life:` line, in every mode: every enemy's hits on it - how many, how many to the head, the
damage they did to health and armour (as the game reports it: a killing hit's in full, past what the life had left), the multiplier catch-up
put on them (x1.00 when it was off) - its own shots and hits on enemies, its heals by
kind (lifesteal, regeneration, Vampire's, which the host reports through `NoteHeal`), its second chance, and how it ended.

## Together with the other settings

- **Vampire** (`plugin/OverStrike2/FunEffects.cs`, `bridge/Shared/Vampire.cs`): catch-up raises nobody's health, so Vampire's own numbers
  stand - the game's 100 at spawn, a tenth of every hit on an enemy and 40 a kill, up to 150. Catch-up's regeneration and lifesteal fill only up
  to the health the life spawned with, and Vampire takes only what catch-up's lifesteal leaves of half a hit (`LifestealShare`).
- **Headshots only** (`mp_damage_headshot_only`, read every second): every multiplier works on the damage the game passes, and a body
  bullet the game zeroes (hit groups 2-7) never spends a second chance.
- **Falls:** fall damage is being hurt, so it restarts the wait before regeneration; a fall death is a death without an enemy on the ladder.
- **Friendly fire:** the module never touches a teammate's hit - no boost, bounty, second chance, lifesteal or protection - and its rule of who
  is an enemy (`Enemies`) is the one Vampire heals by. On the ladder a team kill takes the killer down a level.
- **Teams:** a gap is between two players, so the leader's own teammates, who never fight him, get no boost against the other team's players
  unless those are ahead of them.
- **Free-for-all** (`mp_teammates_are_enemies`, read every second): everybody is an enemy, so every effect applies to everybody and every
  kill counts on the ladder.
- **The grenade drops:** infinite ammo leaves grenades alone, and a grenade kill counts on the ladder, as in CS2.

## The ladder: kills behind

The game exposes no ladder position (`CCSGameModeRules_ArmsRace.m_WeaponSequence` cannot be read), so `Ladder` counts it from the
kills. Progress is in kills: level x 2 (`Ladder.KillsPerLevel`) + the kills on the level; kills behind is the leader's progress minus
the player's. The rules are CS2's as real matches show them - the game's, so none of them is a setting:

- every enemy kill counts, whatever the weapon (a gun, the Zeus, a grenade): 2 kills a level, and the new level's gun at once;
- a knife kill moves the killer up a level, the kills on it kept;
- killed by an enemy's knife: down a level, the kills on it kept (at level 0 the kill on it goes instead);
- killed by an enemy's Zeus: one kill lost (with none on the level, back a level with one);
- on the last level, the golden knife, only a knife kill counts;
- a team kill under friendly fire: the killer down a level, the kills on it kept (at level 0 the kill on it goes instead). He keeps the gun
  in hand, the old level's, until he dies, and his kills with it count on the lower level. The victim loses nothing;
- a death no enemy caused (the world, a fall, drowning, a team change): at level 0 the kill on the level goes, as CS2 does it. Above level
  0 CS2 was never seen doing it, so CS:GO's rule stands - down a level, its kills gone - and the player's next gun kill places them.

**It corrects itself.** A gun kill by a player counted since the match began names the rung that gun is on. Anybody else - joined late,
came back, changed team, died to the world above level 0 - is placed on the rung of their next gun kill, with the count on that rung nearest
the one they had: no kills on it when they were counted below it, the last one before the level-up when above (`Ladder.Place`). A counted
player whose kill with a learned gun lands on another rung is placed the same way when counted players' kills confirmed that rung twice
(`Ladder.ConfirmedKills`): their count is off, not the rung. With fewer the two weigh the same, and that gun places nobody for the rest of
the match. Warmup counts for nothing.

**The evidence:** the self-test replays five whole real matches through the counter, and every gun kill in them, 543 of 543, was made with
the gun the counter puts in the killer's hands. A sixth, of 2026-11-08, shows a count going wrong: the game counted one player a kill the log
does not show, and from that player's next gun kill on, 222 of its 223 gun kills land - where keeping the kills on the level when placing
left 30 a rung off, half too low and half too high.

## Errors and the log

Every handler catches. The first three errors of a 10-minute window are logged with their stack; 20 within 10 minutes switch the effects
off until `os_catchup on` or a plugin reload, rather than repeating a throw at every hit.

Every log line starts `CatchUp:` - at load (the tiers, the methods compiled ahead), at each match start, every kill with both players'
teams and counts after it (`CatchUp: kill:`), a team kill's demotion, a placement or a gun seen on two rungs, every spawn that gets a boost
or a bounty (and the first spawn after a boost that gets none), a tier or bounty step that changes in the middle of a life, a second
chance used (who dealt the hit, how far ahead), every life's end with what it took (`CatchUp: life:`), a boost given from the console, the
settings file's notes (`CatchUp config:`) and errors.

## `catchup.json`

What is tuned: the boost table and the leader's bounty. Everything else - how the ladder counts, which of a tier's values are the fight's
and which the player's own, the message's timing and lines - is the code's.

| Key | Default | Meaning |
|---|---|---|
| `Tiers[]` | seven tiers, 2 to 8 and more ([the tables](../../docs/reference.md#catch-up)) | `MinBehind`, `Health`, `DamageTaken` (negative: less), `DamageDealt`, `Lifesteal` (at most 0.5), `SecondChance`, `OneHitKnifeAndTaser` - the fight's, by the kills between its two players, `Health` taken as resistance against the player ahead; `RegenPerSecond`, `RegenDelaySeconds`, `ExtraSpawnProtectionSeconds`, `InfiniteAmmo` - the player's own, by their kills behind the leader. The tier with the highest `MinBehind` reached applies; any number of tiers. |
| `Bounty` | `MinLead 2`, `StepKills 2`, `PerStep 0.10`, `Max 0.30` | The leader's extra damage taken from enemies. |

Out-of-range values are clamped and logged (`CatchUp config: ...`); names in any case; `//` comments and trailing commas are accepted; a
section or a value the file leaves out keeps its default, and a key the module does not read is ignored.

**A number changed on a running server** needs no build and no restart: edit the `catchup.json` beside the plugin's DLL in the game's
plugin folder - the file the module reads - and run `os_catchup reload`. The plugin's folder is copied into the game before every engine
start, so make the same change to this folder's `catchup.json` and ship it with the plugin (`os2-deploy plugin ship`), or the next start puts
the old one back.
