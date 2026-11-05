// The catch-up module's kill counter and standings, free of CounterStrikeSharp so the self-test drives them on their own.
// CatchUpModule.cs only feeds them the game's events.
using System.Globalization;
using System.Text;

namespace CatchUp;

public enum WeaponKind { Gun, Knife, Taser, Grenade, Other }

/// <summary>Weapon names as the game reports them: "weapon_m4a1_silencer" on an entity, "m4a1_silencer" in a kill.</summary>
public static class Weapons
{
    static readonly HashSet<string> Guns = new(StringComparer.Ordinal)
    {
        "ak47", "aug", "awp", "bizon", "cz75a", "deagle", "elite", "famas", "fiveseven", "g3sg1", "galilar", "glock", "hkp2000",
        "m249", "m4a1", "m4a1_silencer", "mac10", "mag7", "mp5sd", "mp7", "mp9", "negev", "nova", "p250", "p90", "revolver",
        "sawedoff", "scar20", "sg556", "ssg08", "tec9", "ump45", "usp_silencer", "xm1014",
    };

    static readonly HashSet<string> Grenades = new(StringComparer.Ordinal)
    {
        "hegrenade", "flashbang", "smokegrenade", "molotov", "incgrenade", "inferno", "decoy", "tagrenade", "firebomb",
        "frag_grenade", "snowball", "diversion", "breachcharge", "bumpmine",
    };

    static readonly HashSet<string> OtherMelee = new(StringComparer.Ordinal) { "bayonet", "melee", "axe", "hammer", "spanner", "fists" };

    /// <summary>One spelling: lower case, no "weapon_" in front, no "_off" (a silencer taken off) behind.</summary>
    public static string Name(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("weapon_", StringComparison.Ordinal)) s = s[7..];
        if (s.EndsWith("_off", StringComparison.Ordinal)) s = s[..^4];
        return s;
    }

    public static WeaponKind Kind(string? raw)
    {
        var s = Name(raw);
        if (s.Length == 0) return WeaponKind.Other;
        if (s.StartsWith("knife", StringComparison.Ordinal) || OtherMelee.Contains(s)) return WeaponKind.Knife;
        if (s == "taser") return WeaponKind.Taser;
        if (Grenades.Contains(s) || s.EndsWith("_projectile", StringComparison.Ordinal)) return WeaponKind.Grenade;
        return Guns.Contains(s) ? WeaponKind.Gun : WeaponKind.Other;
    }
}

/// <summary>Everybody's progress up the Arms Race ladder in this match, in kills (level x KillsPerLevel + kills on the level), and the
/// gun each one holds. The game exposes no ladder position (the ladder itself, CCSGameModeRules_ArmsRace.m_WeaponSequence, cannot be
/// read: a vector of strings), so this counts it from the kills, by the rules in ProgressConfig - CS2's, as five real matches show them
/// (the self-test replays them: 543 of 543 gun kills made with the gun this puts in the killer's hands).
///
/// Besides the level and the kills on it the game keeps a third number: the rung of the gun in hand (Held). It is the level's gun,
/// except after a team kill: the killer goes down a level but keeps the gun he had until he dies, and his kills with it count on the
/// lower level. A level-up, a death and so a respawn put the level's gun in hand again. A gun's rung is always checked and learned
/// against the gun in hand, never against the level.
///
/// It also learns the ladder as the match goes: a gun kill by a player counted from the match's start names the rung that gun is on,
/// and a gun kill by anybody else (joined late, came back, changed team, died to the world above level 0) puts them on that rung. So
/// the count of a player it could not follow is right again from their next gun kill on a rung somebody reached before.</summary>
public sealed class Ladder
{
    sealed class Rec { public int Level, Kills, Held; public bool Trusted; }

    readonly Dictionary<ulong, Rec> players = new();
    readonly Dictionary<string, int> rungOf = new(StringComparer.Ordinal);
    readonly HashSet<string> ambiguous = new(StringComparer.Ordinal); // a gun seen on two rungs this match: it places nobody
    ProgressConfig cfg;

    public Ladder(ProgressConfig cfg) => this.cfg = cfg;

    public void Configure(ProgressConfig config) => cfg = config;

    /// <summary>True once a match was seen from its start; until then nothing is decided (a load in the middle of a match waits).</summary>
    public bool Tracking { get; private set; }

    public int Rungs => rungOf.Count;

    /// <summary>Placements and two-rung notes this match: none while the rules are right and everybody was followed from the start.</summary>
    public int Notes { get; private set; }

    /// <summary>A new match: everybody at the bottom, counted from here.</summary>
    public void StartMatch(IEnumerable<ulong> present)
    {
        players.Clear();
        rungOf.Clear();
        ambiguous.Clear();
        Notes = 0;
        foreach (var id in present) players[id] = new Rec { Trusted = true };
        Tracking = true;
    }

    /// <summary>Warmup, a map change, a load mid-match: nothing counts until the next match starts.</summary>
    public void Stop()
    {
        players.Clear();
        rungOf.Clear();
        ambiguous.Clear();
        Notes = 0;
        Tracking = false;
    }

    public void Leave(ulong id) => players.Remove(id);

    /// <summary>Something the counter cannot follow happened to this player (a team change): their next gun kill places them.</summary>
    public void Untrust(ulong id) => Get(id).Trusted = false;

    public int Progress(ulong id) => players.TryGetValue(id, out var r) ? r.Level * cfg.KillsPerLevel + r.Kills : 0;

    public (int Level, int Kills, bool Trusted) State(ulong id) =>
        players.TryGetValue(id, out var r) ? (r.Level, r.Kills, r.Trusted) : (0, 0, false);

    /// <summary>The rung of the gun in this player's hands: the level's, or after a team kill the level they had before it, until they die.</summary>
    public int Held(ulong id) => players.TryGetValue(id, out var r) ? r.Held : 0;

    /// <summary>The rung a gun was learned on this match, or -1.</summary>
    public int RungOf(string weapon) => rungOf.TryGetValue(Weapons.Name(weapon), out var rung) ? rung : -1;

    Rec Get(ulong id)
    {
        if (!players.TryGetValue(id, out var r)) players[id] = r = new Rec { Trusted = false }; // joined after the match started: at level 0, as CS2 starts them
        return r;
    }

    /// <summary>An enemy kill. killerHasGun: whether the killer carried a gun when the kill was reported - on the last level nobody
    /// does. Returns a line for the log when the kill placed the killer on its gun's rung, or showed a gun on two rungs; else null.
    ///
    /// The killer: a gun, an HE grenade or a Zeus adds a kill, and the last kill of a level moves them up with the new level's gun; a
    /// knife moves them up a level and keeps the kills on it. The victim: an enemy's knife takes them down a level and keeps the kills
    /// (at level 0 the kill on it goes instead), an enemy's Zeus takes one kill (from 0 kills a level, with one kill on it); either way
    /// they respawn with their level's gun.
    ///
    /// Only a player counted since the start teaches a rung, and such a player is never moved: their count is the one the replayed
    /// matches prove right. If one of them kills on another rung with a gun already learned, the ladder holds that gun twice (or the
    /// count is off): the gun places nobody for the rest of the match, and that player is placed by their next known gun.</summary>
    public string? Kill(ulong killer, ulong victim, string? weapon, bool killerHasGun)
    {
        var kind = Weapons.Kind(weapon);
        var k = Get(killer);
        var v = Get(victim);
        var note = Learn(k, weapon, kind);

        if (kind == WeaponKind.Knife && cfg.KnifeKillLevelsUp)
        {
            k.Level++; // the kills on the level kept
            k.Held = k.Level;
        }
        // No gun and no kill on the level yet: the golden knife, where only a knife kill counts. With one kill on the level, this
        // kill finished it and the game swapped the gun for the knife before reporting the kill: it counts.
        else if (!(cfg.LastLevelOnlyKnife && kind != WeaponKind.Gun && !killerHasGun && k.Kills == 0))
        {
            if (++k.Kills >= cfg.KillsPerLevel)
            {
                k.Level++;
                k.Kills = 0;
                k.Held = k.Level; // the game hands out the new level's gun at once
            }
        }

        if (kind == WeaponKind.Knife && cfg.KnifeDeathDemotes) Demote(v);
        else if (kind == WeaponKind.Taser && cfg.TaserDeathCostsKill)
        {
            if (v.Kills > 0) v.Kills--;
            else if (v.Level > 0)
            {
                v.Level--;
                v.Kills = cfg.KillsPerLevel - 1;
            }
        }
        v.Held = v.Level; // the victim respawns with the level's gun
        return note;
    }

    /// <summary>A team kill under friendly fire (not free-for-all, where a teammate is an enemy). The killer goes down a level, the kills on
    /// it kept - at level 0 the kill on it goes instead - (TeamKillDemotes), and keeps the gun in hand until he dies: his kills with it
    /// count on the lower level. The victim loses nothing, and respawns with their level's gun. Returns the log's line, and apart from it
    /// the note of Kill's kind when the gun placed the killer or showed a gun on two rungs (else null): a team kill's gun names the rung
    /// in hand as well as an enemy kill's.</summary>
    public (string Line, string? Note) TeamKill(ulong killer, ulong victim, string? weapon)
    {
        var k = Get(killer);
        var note = Learn(k, weapon, Weapons.Kind(weapon));
        var was = (k.Level, k.Kills);
        if (cfg.TeamKillDemotes) Demote(k);
        var v = Get(victim);
        v.Held = v.Level;
        var line = $"team-killed with {Weapons.Name(weapon)}: " + (cfg.TeamKillDemotes
            ? $"down from level {was.Level} + {was.Kills} to level {k.Level} + {k.Kills}" + (k.Held != k.Level ? $", the level-{k.Held} gun in hand until death" : "")
            : "the count kept");
        return (line, note);
    }

    /// <summary>A death no enemy caused: the world, a fall, drowning, the kill command, a team change. At level 0 the kill on the level
    /// goes, as CS2 does it. Above level 0 CS2 was never seen, so SuicideDemotes keeps CS:GO's rule (down a level, its kills gone) and
    /// the player's next gun kill places them, whatever the game did. They respawn with their level's gun.</summary>
    public void Suicide(ulong id)
    {
        var r = Get(id);
        if (r.Level > 0)
        {
            if (cfg.SuicideDemotes)
            {
                r.Level--;
                r.Kills = 0;
            }
            r.Trusted = false;
        }
        else if (cfg.SuicideDemotes) r.Kills = 0;
        r.Held = r.Level;
    }

    /// <summary>Down a level, the kills on it kept; at level 0 the kill on it goes instead (a knife death, a team kill).</summary>
    static void Demote(Rec r)
    {
        if (r.Level > 0) r.Level--;
        else r.Kills = 0;
    }

    /// <summary>What a gun kill says about the rung in the killer's hands: a counted player's teaches it, anybody else's places them on a
    /// rung learned before. Null when it says nothing new.</summary>
    string? Learn(Rec k, string? weapon, WeaponKind kind)
    {
        if (kind != WeaponKind.Gun) return null;
        var name = Weapons.Name(weapon);
        if (ambiguous.Contains(name)) return null;
        if (!rungOf.TryGetValue(name, out var rung))
        {
            if (k.Trusted) rungOf[name] = k.Held;
            return null;
        }
        if (rung == k.Held) return null;
        Notes++;
        if (k.Trusted)
        {
            rungOf.Remove(name);
            ambiguous.Add(name);
            k.Trusted = false;
            return $"made a {name} kill on level {k.Held}, learned on level {rung}: two rungs with one gun, or the count is off - {name} places nobody this match"
                   + (k.Held != k.Level ? $" (counted level {k.Level} + {k.Kills}, holding the level-{k.Held} gun)" : "");
        }
        var placed = $"placed on level {rung} by a {name} kill (counted {k.Level})";
        k.Level = rung;
        k.Held = rung;
        k.Kills = Math.Min(k.Kills, cfg.KillsPerLevel - 1);
        return placed;
    }

    /// <summary>The standings among the players on a team now.</summary>
    public Standings Standings(IEnumerable<ulong> present)
    {
        var progress = new Dictionary<ulong, int>();
        foreach (var id in present) progress[id] = Progress(id);
        return new Standings(progress);
    }
}

/// <summary>Who leads and by how much. Leader is 0 when nobody leads alone (a tie, or fewer than two players).</summary>
public sealed class Standings
{
    public Standings(IReadOnlyDictionary<ulong, int> progress)
    {
        Progress = progress;
        if (progress.Count == 0) return;
        Top = progress.Values.Max();
        var top = progress.Where(p => p.Value == Top).Select(p => p.Key).ToList();
        Second = progress.Count < 2 ? 0 : top.Count > 1 ? Top : progress.Where(p => p.Value != Top).Select(p => p.Value).DefaultIfEmpty(0).Max();
        Leader = progress.Count >= 2 && top.Count == 1 ? top[0] : 0;
    }

    public IReadOnlyDictionary<ulong, int> Progress { get; }
    public int Top { get; }
    public int Second { get; }
    public ulong Leader { get; }
    public int Players => Progress.Count;

    /// <summary>The leader's lead over second place; 0 without a leader.</summary>
    public int Lead => Leader == 0 ? 0 : Top - Second;

    public int Of(ulong id) => Progress.TryGetValue(id, out var p) ? p : 0;

    public int Behind(ulong id) => Top - Of(id);
}
