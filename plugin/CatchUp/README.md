# CatchUp - the Arms Race catch-up boosts

The module behind [catch-up](../../docs/reference.md#catch-up): players behind the leader get a boost that grows with the gap, the
leader a bounty. The reference has the rules as players meet them and the tier table; this page is how the module does it. It is not
a plugin: the OverStrike2 plugin compiles these files in as source and constructs the module in its `Load`. Nothing here is deployed
or loaded on its own.

| File | What |
|---|---|
| `CatchUpRules.cs` | The decisions, free of CounterStrikeSharp: the tier and the bounty (`Rules.Decide`), the boost decided again in the middle of a life (`Rules.Recalculate`), one hit (`Rules.Hit`), the healing and the timing. |
| `Ladder.cs` | The kill counter (`Ladder`), the standings and the weapon names, free of CounterStrikeSharp too. |
| `CatchUpWords.cs` | The words: the message's lines, one sentence for chat and the log, the log's lines. |
| `CatchUpConfig.cs` | The settings and their defaults; reads `catchup.json` and never throws: a missing or unreadable file means the defaults, and the log says so. |
| `CatchUpModule.cs` | The game half: registers the events through the host, applies what the rules decide, the `gg_catchup` command. |
| `catchup.json` | The shipped settings, equal to the built-in defaults (the self-test fails when they drift apart). It ships beside the plugin's DLL. |
| `SelfTest/` | `dotnet run`: every decision without a game server, and the kill counter against five whole real matches (`armsrace-*.txt`, anonymised). `-- --table` also prints the tier table, the panels and the mid-life cut. |

## How the plugin hosts it

- `plugin/OverStrike2/OverStrike2.csproj` compiles the five source files by name and copies `catchup.json` beside the DLL. By name,
  never a wildcard over this folder: `SelfTest/` and the build output hold other C# files. A new file here is a new line there.
- `new CatchUpModule(host, logger, configPath)`, then `Load(hotReload)` in the host's `Load`. Everything registers through the host,
  which CounterStrikeSharp removes with it, so nothing is needed in `Unload`. Loaded in the middle of a map (a hot reload), it counts
  nothing until the next match starts.
- At load it compiles its own methods ahead (`CatchUp: N methods compiled ahead in M ms`), so the first spawn or hit does not pay for
  the JIT. Nothing heavy runs inside a player's own callback (spawn, hit, kill, shot): those only count and queue, and the work is done a
  frame later or on a timer.
- For the host: `Show` replaces the centre panel as the way the message is shown (the player, the boost, the packed lines, the panel's
  HTML, the seconds) - the plugin draws the HUD's card there, or the centre panel for a game not known to hold the card, and keeps
  either up 5 s; `Switch(on)` is catch-up's fun setting, which the plugin turns on while the setting is on and the map is Arms Race;
  `BoostOf(slot)` is the current life's boost; `Status()` is what `gg_catchup` prints.
- It registers the events `round_start`, `player_spawn`, `player_death`, `player_hurt`, `weapon_fire`, `player_team` and
  `player_disconnect` (all post), the listeners `OnClientPutInServer`, `OnMapStart` and `OnPlayerTakeDamagePre`, one repeating timer
  (`Effects.RegenTickSeconds`) and the server command `gg_catchup`. All of them coexist with the host's own handlers.
- It reads one file, `catchup.json` at the path the host passes, and logs through the host's logger.

## The console command

`gg_catchup`, from the server console or RCON only; a client typing it gets nothing.

| Command | Effect |
|---|---|
| `gg_catchup` | The status: on or off and why, decided again after every kill or only at spawn, counting this match (rungs learned, placements) or waiting for a match start, free-for-all, headshots only and the game's spawn protection; then top, second, leader, lead and bounty, and per player their progress, level, kills behind and what this life has. |
| `gg_catchup reload` | Reads `catchup.json` again; the next spawns use it. |
| `gg_catchup on` / `off` | Until the plugin reloads, or catch-up's fun setting switches it. `on` also clears a stop after errors. Lives already boosted keep their health. |
| `gg_catchup show <player> <behind>` or `<player> lead <n>` | Shows that player, now, what a gap (or a lead) gives. Changes nothing. |
| `gg_catchup give <player> <behind>` or `<player> lead <n>` | Also gives their current life that boost - health, protection from now on, every effect - kept until they die and not decided again: to try the effects with two players. |

`<player>` is `#<slot>`, an account id, `[U:1:<id>]` or a SteamID64.

## How each boost is applied

| Boost | How |
|---|---|
| Health | `Health` and `MaxHealth` = the spawn health x (1 + bonus), `Effects.ApplyDelaySeconds` (0.1 s) after `player_spawn`, once the game has set the spawn health and loadout. The base is the health the life spawned with, never the maximum. In the middle of a life only the maximum goes up, never the current health. The next spawn without a boost puts the base back when the pawn still holds what the module set. |
| Damage taken, damage dealt, the bounty | `OnPlayerTakeDamagePre`: the damage x (1 + the attacker's damage dealt) x (1 + the victim's damage taken) x (1 + the victim's bounty), on enemy hits only, before the game applies armour. |
| Regeneration | Every `Effects.RegenTickSeconds` (0.5 s), once `RegenDelaySeconds` have passed since the player was last hurt by anything (a fall too): a share of the spawn health per second, the fractions carried over, up to the life's maximum. |
| Second chance | A hit that would kill (damage at least the health) is cut to leave 1 health, once per life. Armour, which the game applies after, only leaves more. |
| Extra spawn protection | Enemy hits are blocked until the spawn + the game's own protection (`mp_respawn_immunitytime`) + the extra seconds, ended by the player's first shot or hit. The game's protection is left alone, and the extra seconds show nothing to attackers. |
| Lifesteal | On `player_hurt` by an enemy: the share of the health the victim lost (at most their maximum health), a frame later, up to the attacker's maximum. |
| One-hit knife and Zeus | A knife or Zeus hit - by the weapon the damage names, else the one in hand; never a blast or a fire - is raised to 4 x (health + armour) + 100. |
| Infinite ammo | After every shot of a gun, a frame later, the clip is refilled to the gun's `MaxClip1`. Not grenades, the knife or the Zeus. |

**The message** comes `Message.DelaySeconds` (0.25 s) after the spawn, or `Message.FirstSpawnDelaySeconds` (3 s) at the first spawn
after joining. A message whose player died first is dropped: the next spawn says its own. A boost decided again in the middle of a life
is silent.

## Together with the other settings

- **Vampire:** the health is a share of what the life spawned with (1,000), and when the maximum is cut in the middle of a life (Vampire
  switched off), the life follows it: regeneration, lifesteal and a later boost count from there.
- **Headshots only** (`mp_damage_headshot_only`, read every second): every multiplier works on the damage the game passes, and a body
  bullet the game zeroes (hit groups 2-7) never spends a second chance.
- **Falls:** fall damage is being hurt, so it restarts the wait before regeneration; a fall death is a death without an enemy on the ladder.
- **Friendly fire:** the module never touches a teammate's hit - no boost, bounty, second chance, lifesteal or protection. On the ladder
  a team kill takes the killer down a level.
- **Free-for-all** (`mp_teammates_are_enemies`, read every second): everybody is an enemy, so every effect applies to everybody and every
  kill counts on the ladder.
- **The grenade drops:** infinite ammo leaves grenades alone, and a grenade kill counts on the ladder, as in CS2.

## The ladder: kills behind

The game exposes no ladder position (`CCSGameModeRules_ArmsRace.m_WeaponSequence` cannot be read), so `Ladder` counts it from the
kills. Progress is in kills: level x `Progress.KillsPerLevel` (2) + the kills on the level; kills behind is the leader's progress minus
the player's. The rules are CS2's as real matches show them, each one switchable under `Progress`:

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
came back, changed team, died to the world above level 0 - is placed on the rung of their next gun kill. A counted player is never moved;
if one of them kills on another rung with a learned gun, that gun places nobody for the rest of the match. Warmup counts for nothing.

**The evidence:** the self-test replays five whole real matches through the counter, and every gun kill in them, 543 of 543, was made with
the gun the counter puts in the killer's hands.

## Errors and the log

Every handler catches. The first three errors of a 10-minute window are logged with their stack; 20 within 10 minutes switch the effects
off until `gg_catchup on` or a plugin reload, rather than repeating a throw at every hit.

Every log line starts `CatchUp:` - at load (the tiers, the methods compiled ahead), at each match start, every kill with both players'
teams and counts after it (`CatchUp: kill:`), a team kill's demotion, a placement or a gun seen on two rungs, every spawn that gets a boost
or a bounty (and the first spawn after a boost that gets none), a tier or bounty step that changes in the middle of a life, a second
chance used, a boost given from the console, the settings file's notes (`CatchUp config:`) and errors.

## `catchup.json`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: no boost, no bounty, no message. The counting still runs, so switching it on in the middle of a match works. |
| `RecalculateOnKill` | `true` | Every living player's boost decided again a frame after each change to the ladder, silently. Off: decided at spawn only, and kept for the life. |
| `Tiers[]` | seven tiers, 2 to 8 and more behind ([the table](../../docs/reference.md#catch-up)) | `MinBehind`, `Health`, `DamageTaken` (negative: less), `DamageDealt`, `Lifesteal`, `RegenPerSecond`, `RegenDelaySeconds`, `SecondChance`, `ExtraSpawnProtectionSeconds`, `InfiniteAmmo`, `OneHitKnifeAndTaser`. The tier with the highest `MinBehind` reached applies; any number of tiers. |
| `Bounty` | `MinLead 2`, `StepKills 2`, `PerStep 0.10`, `Max 0.30` | The leader's extra damage taken from enemies. |
| `Balance` | `LifestealCounts true`, `RoundTo 0.05` | The duel factor the log shows - (1 + health + lifesteal) x (1 + damage dealt) x (1 + the leader's bounty) / (1 + damage taken), 1 an even fight - and the mid-life cut: when a boost shrinks and the life keeps its health, damage taken, damage dealt and lifesteal share what is left, rounded down to whole `RoundTo` steps. |
| `Progress` | `KillsPerLevel 2`; `KnifeKillLevelsUp`, `KnifeDeathDemotes`, `TaserDeathCostsKill`, `SuicideDemotes`, `TeamKillDemotes`, `LastLevelOnlyKnife` all `true` | The counter's rules, above. |
| `Message` | `ToBoosted`, `ToLeader` `true`; `Seconds 2`; `DelaySeconds 0.25`; `FirstSpawnDelaySeconds 3.0`; `LineWidth 28`; `MaxLines 5` | The message: to whom, how long the centre panel holds it, when, and the panel's lines at most `MaxLines` of `LineWidth`. |
| `Effects` | `ApplyDelaySeconds 0.1`, `RegenTickSeconds 0.5` | Timing. The tick takes effect at the next plugin load, everything else at `gg_catchup reload`. |

Out-of-range values are clamped and logged (`CatchUp config: ...`); names in any case; `//` comments and trailing commas are accepted.

**A number changed on a running server** needs no build and no restart: edit the `catchup.json` beside the plugin's DLL in the game's
plugin folder - the file the module reads - and run `gg_catchup reload`. The plugin's folder is copied into the game before every engine
start, so make the same change to this folder's `catchup.json` and ship it with the plugin (`deploy/plugin.py`), or the next start puts
the old one back.
