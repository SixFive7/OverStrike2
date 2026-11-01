// The catch-up module's decisions, free of CounterStrikeSharp so the self-test drives every one of them on its own: the Arms Race
// kill counter, the standings, the tier and bounty, the balance cap, the damage pipeline, the healing, the message and its timing.
// CatchUpModule.cs only feeds these the game's events and applies what they return.
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

/// <summary>Everybody's progress up the Arms Race ladder in this match, in kills (level x KillsPerLevel + kills on the level). The
/// game exposes no ladder position (the ladder itself, CCSGameModeRules_ArmsRace.m_WeaponSequence, cannot be read: a vector of
/// strings), so this counts it from the kills, by the rules in ProgressConfig.
///
/// It also learns the ladder as the match goes: a gun kill by a player counted from the match's start names the rung that gun is on,
/// and a gun kill by anybody else (joined late, came back, changed team, died to the world) puts them on that rung. So the count of
/// a player it could not follow is right again from their next gun kill on a rung somebody reached before.</summary>
public sealed class Ladder
{
    sealed class Rec { public int Level, Kills; public bool Trusted; }

    readonly Dictionary<ulong, Rec> players = new();
    readonly Dictionary<string, int> rungOf = new(StringComparer.Ordinal);
    readonly HashSet<string> ambiguous = new(StringComparer.Ordinal); // a gun seen on two rungs this match: it places nobody
    ProgressConfig cfg;

    public Ladder(ProgressConfig cfg) => this.cfg = cfg;

    public void Configure(ProgressConfig config) => cfg = config;

    /// <summary>True once a match was seen from its start; until then nothing is decided (a load in the middle of a match waits).</summary>
    public bool Tracking { get; private set; }

    public int Rungs => rungOf.Count;

    /// <summary>A new match: everybody at the bottom, counted from here.</summary>
    public void StartMatch(IEnumerable<ulong> present)
    {
        players.Clear();
        rungOf.Clear();
        ambiguous.Clear();
        foreach (var id in present) players[id] = new Rec { Trusted = true };
        Tracking = true;
    }

    /// <summary>Warmup, a map change, a load mid-match: nothing counts until the next match starts.</summary>
    public void Stop()
    {
        players.Clear();
        rungOf.Clear();
        ambiguous.Clear();
        Tracking = false;
    }

    public void Leave(ulong id) => players.Remove(id);

    /// <summary>Something the counter cannot follow happened to this player (a team change): their next gun kill places them.</summary>
    public void Untrust(ulong id) => Get(id).Trusted = false;

    public int Progress(ulong id) => players.TryGetValue(id, out var r) ? r.Level * cfg.KillsPerLevel + r.Kills : 0;

    public (int Level, int Kills, bool Trusted) State(ulong id) =>
        players.TryGetValue(id, out var r) ? (r.Level, r.Kills, r.Trusted) : (0, 0, false);

    /// <summary>The rung a gun was learned on this match, or -1.</summary>
    public int RungOf(string weapon) => rungOf.TryGetValue(Weapons.Name(weapon), out var rung) ? rung : -1;

    Rec Get(ulong id)
    {
        if (!players.TryGetValue(id, out var r)) players[id] = r = new Rec { Trusted = false }; // joined after the match started
        return r;
    }

    /// <summary>An enemy kill. killerHasGun: whether the killer carried a gun when the kill was reported - on the last level nobody
    /// does. Returns a line for the log when the kill placed the killer on its gun's rung, or showed a gun on two rungs; else null.
    ///
    /// Only a player counted since the start teaches a rung, and such a player is never moved: their count is the one the replayed
    /// match proves right. If one of them kills on another rung with a gun already learned, the ladder holds that gun twice (or the
    /// count is off): the gun places nobody for the rest of the match, and that player is placed by their next known gun.</summary>
    public string? Kill(ulong killer, ulong victim, string? weapon, bool killerHasGun)
    {
        var kind = Weapons.Kind(weapon);
        var name = Weapons.Name(weapon);
        var k = Get(killer);
        var v = Get(victim);
        string? note = null;

        if (kind == WeaponKind.Gun && !ambiguous.Contains(name))
        {
            if (!rungOf.TryGetValue(name, out var rung))
            {
                if (k.Trusted) rungOf[name] = k.Level;
            }
            else if (rung != k.Level)
            {
                if (k.Trusted)
                {
                    note = $"made a {name} kill on level {k.Level}, learned on level {rung}: two rungs with one gun, or the count is off - {name} places nobody this match";
                    rungOf.Remove(name);
                    ambiguous.Add(name);
                    k.Trusted = false;
                }
                else
                {
                    note = $"placed on level {rung} by a {name} kill (counted {k.Level})";
                    k.Level = rung;
                    k.Kills = Math.Min(k.Kills, cfg.KillsPerLevel - 1);
                }
            }
        }

        if (kind == WeaponKind.Knife && cfg.KnifeKillLevelsUp)
        {
            k.Level++;
            k.Kills = 0;
        }
        // No gun and no kill on the level yet: the golden knife, where only a knife kill counts. With one kill on the level, this
        // kill finished it and the game swapped the gun for the knife before reporting the kill: it counts.
        else if (!(cfg.LastLevelOnlyKnife && kind != WeaponKind.Gun && !killerHasGun && k.Kills == 0))
        {
            if (++k.Kills >= cfg.KillsPerLevel)
            {
                k.Level++;
                k.Kills = 0;
            }
        }

        if (kind == WeaponKind.Knife && cfg.KnifeDeathDemotes)
        {
            if (v.Level > 0)
            {
                v.Level--;
                v.Kills = 0;
            }
        }
        else if (kind == WeaponKind.Taser && cfg.TaserDeathCostsKill)
        {
            if (v.Kills > 0) v.Kills--;
            else if (v.Level > 0)
            {
                v.Level--;
                v.Kills = cfg.KillsPerLevel - 1;
            }
        }
        return note;
    }

    /// <summary>A death no enemy caused: the world, a fall, the kill command, a team change. Its rule is CS:GO's, not seen in CS2, so
    /// the player's next gun kill places them.</summary>
    public void Suicide(ulong id)
    {
        var r = Get(id);
        if (cfg.SuicideDemotes)
        {
            r.Kills = 0;
            if (r.Level > 0) r.Level--;
        }
        r.Trusted = false;
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

/// <summary>What one life gets: a catch-up boost, the leader's bounty, or nothing. Decided at spawn and kept for that life, so the
/// spawn message stays true until the player dies.</summary>
public sealed record Boost
{
    public static readonly Boost None = new();

    public int KillsBehind { get; init; }

    /// <summary>The leader's lead over second place, on the leader's own decision.</summary>
    public int Lead { get; init; }

    /// <summary>MinBehind of the tier given, 0 for none.</summary>
    public int Tier { get; init; }

    public double Health { get; init; }
    public double DamageTaken { get; init; }
    public double DamageDealt { get; init; }
    public double Lifesteal { get; init; }
    public double RegenPerSecond { get; init; }
    public double RegenDelaySeconds { get; init; }
    public bool SecondChance { get; init; }
    public double ExtraProtection { get; init; }
    public bool InfiniteAmmo { get; init; }
    public bool OneHitMelee { get; init; }

    /// <summary>The leader's handicap: extra damage taken from enemies.</summary>
    public double Bounty { get; init; }

    /// <summary>The duel factor against the leader as the tier gives it, as given, and the most the cap allowed.</summary>
    public double RawFactor { get; init; } = 1;
    public double Factor { get; init; } = 1;
    public double Allowed { get; init; } = 1;
    public bool Capped { get; init; }

    /// <summary>The most the leader's bounty may multiply this player's hits on the leader: below 1 + the bounty only when the bounty
    /// alone would take this player past what the cap allows.</summary>
    public double BountyAllowed { get; init; } = double.PositiveInfinity;

    public bool IsBoost => Tier > 0 && (Health > 0 || DamageTaken < 0 || DamageDealt > 0 || Lifesteal > 0 || RegenPerSecond > 0
                                        || SecondChance || ExtraProtection > 0 || InfiniteAmmo || OneHitMelee);

    public bool IsBounty => Bounty > 0;
}

public enum Melee { None, Knife, Taser }

/// <summary>One enemy hit as the damage hook sees it, before the game applies armour.</summary>
public readonly record struct HitInput(
    float Damage, Boost Attacker, Boost Victim, int VictimHealth, int VictimArmor, Melee Melee,
    bool VictimProtected, bool SecondChanceLeft, bool GameZeroesIt);

/// <summary>What to do with it: block it, or let it through with this damage.</summary>
public readonly record struct HitResult(bool Block, float Damage, bool SecondChanceUsed, bool OneHit);

public static class Rules
{
    /// <summary>The tier for a gap, or null below the first.</summary>
    public static TierConfig? TierFor(IReadOnlyList<TierConfig> tiers, int behind)
    {
        TierConfig? best = null;
        foreach (var t in tiers)
            if (behind >= t.MinBehind && (best == null || t.MinBehind > best.MinBehind)) best = t;
        return best;
    }

    /// <summary>The leader's bounty for a lead over second place: PerStep per StepKills of lead, from MinLead, at most Max.</summary>
    public static double BountyFor(BountyConfig b, int lead) =>
        lead < b.MinLead ? 0 : Math.Min(b.Max, b.PerStep * (lead / b.StepKills));

    /// <summary>How many times faster a player kills the leader than the leader kills them, on paper (same aim, same gun): their
    /// effective health (health, plus lifesteal's share of the leader's health over a duel) over the damage they take, times the damage
    /// they deal, times the leader's bounty. 1 is an even fight.</summary>
    public static double DuelFactor(double health, double lifesteal, double damageDealt, double damageTaken, double bounty, bool lifestealCounts) =>
        (1 + health + (lifestealCounts ? lifesteal : 0)) * (1 + damageDealt) * (1 + bounty) / (1 + damageTaken);

    /// <summary>The most a player behind may get: never more than MaxDuelFactor, and with ScoreboardCap never more than the edge the
    /// leader has shown on the scoreboard, (leader + 1) / (player + 1).</summary>
    public static double Allowed(BalanceConfig b, int leaderProgress, int playerProgress)
    {
        var allowed = b.MaxDuelFactor;
        if (b.ScoreboardCap) allowed = Math.Min(allowed, Math.Max(1.0, (leaderProgress + 1.0) / (playerProgress + 1.0)));
        return allowed;
    }

    /// <summary>The boost for this player now, by the standings. The leader may get a bounty, everybody else a tier, capped.</summary>
    public static Boost Decide(CatchUpConfig c, Standings s, ulong id)
    {
        if (!c.Enabled || s.Players < 2) return Boost.None;
        var bounty = s.Leader == 0 ? 0 : BountyFor(c.Bounty, s.Lead);
        if (id == s.Leader) return new Boost { Lead = s.Lead, Bounty = bounty };
        return ForGap(c, s.Behind(id), s.Top, s.Of(id), bounty);
    }

    /// <summary>The tier for a gap, capped against a leader at leaderProgress with that bounty.</summary>
    public static Boost ForGap(CatchUpConfig c, int behind, int leaderProgress, int playerProgress, double leaderBounty)
    {
        var t = TierFor(c.Tiers, behind);
        if (t == null) return new Boost { KillsBehind = behind };
        var raw = new Boost
        {
            KillsBehind = behind, Tier = t.MinBehind,
            Health = t.Health, DamageTaken = t.DamageTaken, DamageDealt = t.DamageDealt, Lifesteal = t.Lifesteal,
            RegenPerSecond = t.RegenPerSecond, RegenDelaySeconds = t.RegenDelaySeconds, SecondChance = t.SecondChance,
            ExtraProtection = t.ExtraSpawnProtectionSeconds, InfiniteAmmo = t.InfiniteAmmo, OneHitMelee = t.OneHitKnifeAndTaser,
        };
        return Cap(c.Balance, raw, Allowed(c.Balance, leaderProgress, playerProgress), leaderBounty);
    }

    /// <summary>Keeps the duel factor against the leader inside what is allowed. The leader's bounty counts first; the player's health,
    /// resistance, damage and lifesteal share the rest: scaled down together (the same power of each factor, so their proportions
    /// stay), rounded down to whole RoundTo steps, then given back a step at a time, in that order, while they still fit and never past
    /// the tier's own value. When the bounty alone is past the limit, the four are zero and the bounty on this player's hits on the
    /// leader is trimmed to the limit (BountyAllowed). Regeneration, the second chance, spawn protection, infinite ammo and the one-hit
    /// knife and Zeus are outside the factor: they stay as the tier gives them.</summary>
    public static Boost Cap(BalanceConfig b, Boost raw, double allowed, double leaderBounty)
    {
        var lsc = b.LifestealCounts;
        var bountyFactor = 1 + Math.Max(0, leaderBounty);
        var rawFactor = DuelFactor(raw.Health, raw.Lifesteal, raw.DamageDealt, raw.DamageTaken, leaderBounty, lsc);
        if (rawFactor <= allowed + 1e-9)
            return raw with { RawFactor = rawFactor, Factor = rawFactor, Allowed = allowed, BountyAllowed = allowed / Own(raw.Health, raw.Lifesteal, raw.DamageDealt, raw.DamageTaken, lsc) };

        // magnitudes: health, resistance (as a positive share), damage, lifesteal
        var limit = new[] { raw.Health, -raw.DamageTaken, raw.DamageDealt, raw.Lifesteal };
        var part = new double[4];
        var target = allowed / bountyFactor; // what the player's own boosts may multiply to
        if (target > 1 + 1e-9)
        {
            var own = Own(raw.Health, raw.Lifesteal, raw.DamageDealt, raw.DamageTaken, lsc); // > target here
            var k = Math.Log(target) / Math.Log(own);
            var healthPart = 1 + raw.Health + (lsc ? raw.Lifesteal : 0);
            var share = healthPart - 1;
            var scaled = Math.Pow(healthPart, k) - 1;
            part[0] = share > 0 ? raw.Health * scaled / share : 0;
            part[3] = !lsc ? raw.Lifesteal : share > 0 ? raw.Lifesteal * scaled / share : 0;
            part[1] = 1 - Math.Pow(1 + raw.DamageTaken, k);
            part[2] = Math.Pow(1 + raw.DamageDealt, k) - 1;
            for (var i = 0; i < 4; i++)
                if (lsc || i != 3) part[i] = Down(part[i], b.RoundTo);
            for (var grew = b.RoundTo > 0; grew;)
            {
                grew = false;
                for (var i = 0; i < 4; i++)
                {
                    if (!lsc && i == 3) continue;
                    var next = Math.Round(part[i] + b.RoundTo, 6);
                    if (next > limit[i] + 1e-9) continue;
                    var was = part[i];
                    part[i] = next;
                    if (Own(part[0], part[3], part[2], -part[1], lsc) <= target + 1e-9) grew = true;
                    else part[i] = was;
                }
            }
        }
        else if (!lsc) part[3] = raw.Lifesteal; // outside the factor: as the tier gives it

        var given = Own(part[0], part[3], part[2], -part[1], lsc);
        var bountyAllowed = allowed / given; // >= 1: given <= target <= allowed, or given is 1
        return raw with
        {
            Health = part[0], DamageTaken = part[1] == 0 ? 0 : -part[1], DamageDealt = part[2], Lifesteal = part[3],
            RawFactor = rawFactor, Factor = given * Math.Min(bountyFactor, bountyAllowed), Allowed = allowed, Capped = true,
            BountyAllowed = bountyAllowed,
        };
    }

    static double Own(double health, double lifesteal, double damageDealt, double damageTaken, bool lifestealCounts) =>
        DuelFactor(health, lifesteal, damageDealt, damageTaken, 0, lifestealCounts);

    static double Down(double value, double step)
    {
        if (value <= 0) return 0;
        if (step <= 0) return value;
        return Math.Round(Math.Floor(value / step + 1e-9) * step, 6);
    }

    /// <summary>The damage multiplier for an enemy hit: the attacker's damage, the victim's resistance and the victim's bounty (no more
    /// of the bounty than the attacker's cap leaves room for).</summary>
    public static float Multiplier(Boost attacker, Boost victim) =>
        (float)((1 + attacker.DamageDealt) * (1 + victim.DamageTaken) * Math.Min(1 + victim.Bounty, attacker.BountyAllowed));

    /// <summary>Damage that kills anybody: more than their health and armour together, several times over.</summary>
    public static float OneHitDamage(int health, int armor) => Math.Max(1, health + armor) * 4f + 100f;

    /// <summary>One enemy hit, in order: the extra spawn protection blocks it; the multipliers scale it; a boosted knife or Zeus makes
    /// it lethal; the second chance turns a lethal hit into one that leaves 1 health. A hit the game is going to zero anyway (headshots
    /// only, a bullet not on the head) is left alone, so it cannot use up a second chance.</summary>
    public static HitResult Hit(in HitInput h)
    {
        if (h.Damage <= 0 || h.GameZeroesIt) return new HitResult(false, h.Damage, false, false);
        if (h.VictimProtected) return new HitResult(true, 0, false, false);
        var damage = h.Damage * Multiplier(h.Attacker, h.Victim);
        var oneHit = false;
        if (h.Attacker.OneHitMelee && h.Melee != Melee.None)
        {
            damage = Math.Max(damage, OneHitDamage(h.VictimHealth, h.VictimArmor));
            oneHit = true;
        }
        var used = false;
        if (h.Victim.SecondChance && h.SecondChanceLeft && damage >= h.VictimHealth)
        {
            damage = Math.Max(0, h.VictimHealth - 1); // armour, which the game applies after this, only leaves more
            used = true;
        }
        return new HitResult(false, damage, used, oneHit);
    }

    /// <summary>Which hit this is: a knife, a Zeus, or neither. The weapon the damage names (its ability) first, else the weapon in
    /// the attacker's hands; a blast or a fire is never a knife or a Zeus whatever is in hand.</summary>
    public static Melee MeleeOf(string? ability, string? inHand, bool blastOrFire)
    {
        if (blastOrFire) return Melee.None;
        var kind = Weapons.Kind(string.IsNullOrEmpty(ability) ? inHand : ability);
        return kind switch { WeaponKind.Knife => Melee.Knife, WeaponKind.Taser => Melee.Taser, _ => Melee.None };
    }

    /// <summary>Under headshots only, a bullet that is not on the head does no damage: true when the hit group says chest, stomach, an
    /// arm or a leg (2-7). Anything else - the head, the neck, an unknown group (0, or the invalid value) - is not taken as proof.</summary>
    public static bool GameZeroes(bool headshotsOnly, bool bullet, uint hitGroup) =>
        headshotsOnly && bullet && hitGroup is >= 2 and <= 7;

    /// <summary>Spawn health with the boost, relative to the spawn health the game (or a mode) gave.</summary>
    public static int SpawnHealth(int baseHealth, Boost b) => Math.Max(1, (int)Math.Round(Math.Max(1, baseHealth) * (1 + b.Health)));

    /// <summary>Health back for a hit on an enemy: the lifesteal share of what they lost, never more than their whole health.</summary>
    public static int Lifesteal(Boost attacker, int healthLost, int victimMaxHealth) =>
        attacker.Lifesteal <= 0 || healthLost <= 0 ? 0 : (int)Math.Round(attacker.Lifesteal * Math.Min(healthLost, Math.Max(1, victimMaxHealth)));

    /// <summary>Regeneration for one tick of dt seconds. The fractions carry over, so 2 health a second at a half-second tick is 1, 1.</summary>
    public static int Regen(Boost b, double sinceHurt, double dt, int baseHealth, ref double carry)
    {
        if (b.RegenPerSecond <= 0 || sinceHurt < b.RegenDelaySeconds) { carry = 0; return 0; }
        carry += b.RegenPerSecond * Math.Max(1, baseHealth) * dt;
        var whole = (int)Math.Floor(carry);
        carry -= whole;
        return whole;
    }

    /// <summary>Until when the extra spawn protection holds: the game's own protection plus the boost's seconds. None without extra.</summary>
    public static double ProtectedUntil(double spawnedAt, double gameProtection, Boost b) =>
        b.ExtraProtection > 0 ? spawnedAt + Math.Max(0, gameProtection) + b.ExtraProtection : spawnedAt;

    /// <summary>Seconds after the spawn until the message. When the host said when its tagline ends (taglineLeft, negative once it
    /// is gone), just after that; otherwise the first spawn after joining waits FirstSpawnDelaySeconds; otherwise DelaySeconds.</summary>
    public static double MessageDelay(MessageConfig m, bool firstSinceJoin, double? taglineLeft)
    {
        if (taglineLeft is { } left) return Math.Max(m.DelaySeconds, left + m.TaglineGapSeconds);
        return firstSinceJoin ? Math.Max(m.DelaySeconds, m.FirstSpawnDelaySeconds) : m.DelaySeconds;
    }
}

/// <summary>The words: the private message (packed for the centre panel), one sentence for a log or a chat line, and the log line.</summary>
public static class Words
{
    static string Pct(double fraction) => Math.Round(Math.Abs(fraction) * 100).ToString(CultureInfo.InvariantCulture) + "%";
    static string Sec(double seconds) => Math.Round(seconds, 1).ToString(CultureInfo.InvariantCulture);
    static string Kills(int n) => n == 1 ? "1 kill" : $"{n} kills";

    /// <summary>What the boost gives, in the order of the owner's example; long words or short ones.</summary>
    public static List<string> Items(Boost b, bool shortWords)
    {
        var items = new List<string>();
        if (b.IsBounty)
        {
            items.Add(shortWords ? $"+{Pct(b.Bounty)} dmg taken (bounty)" : $"+{Pct(b.Bounty)} damage taken (a bounty)");
            return items;
        }
        if (!b.IsBoost) return items;
        if (b.Health > 0) items.Add(shortWords ? $"+{Pct(b.Health)} HP" : $"+{Pct(b.Health)} health");
        if (b.DamageTaken < 0) items.Add(shortWords ? $"-{Pct(b.DamageTaken)} dmg taken" : $"-{Pct(b.DamageTaken)} damage taken");
        if (b.DamageDealt > 0) items.Add(shortWords ? $"+{Pct(b.DamageDealt)} dmg" : $"+{Pct(b.DamageDealt)} damage");
        if (b.Lifesteal > 0) items.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.RegenPerSecond > 0) items.Add(shortWords ? "regen" : "regeneration");
        if (b.SecondChance) items.Add(shortWords ? "2nd chance" : "a second chance");
        if (b.ExtraProtection > 0) items.Add(shortWords ? $"+{Sec(b.ExtraProtection)}s protection" : $"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) items.Add(shortWords ? "no reloads" : "infinite ammo");
        if (b.OneHitMelee) items.Add(shortWords ? "1-hit knife/zeus" : "one-hit knife and Zeus");
        return items;
    }

    /// <summary>Why: the kills behind the leader, or the lead.</summary>
    public static string Why(Boost b, bool shortWords) => b.IsBounty
        ? $"You lead by {Kills(b.Lead)}:"
        : shortWords ? $"{Kills(b.KillsBehind)} behind:" : $"{Kills(b.KillsBehind)} behind the leader:";

    /// <summary>The whole message in one sentence, for example: "You are 5 kills behind the leader: +40% health, ...".</summary>
    public static string Sentence(Boost b)
    {
        var items = Items(b, shortWords: false);
        if (items.Count == 0) return "";
        return b.IsBounty ? $"You lead by {Kills(b.Lead)}: {string.Join(", ", items)}."
            : $"You are {Kills(b.KillsBehind)} behind the leader: {string.Join(", ", items)}.";
    }

    /// <summary>The centre-panel lines: the reason first, then the items, wrapped at commas. Tries long words with the reason on a line
    /// of its own, then short words, then short words with the short reason inline; the first that fits MaxLines x LineWidth wins,
    /// else the last. Empty when there is nothing to say.</summary>
    public static string[] Lines(Boost b, MessageConfig m)
    {
        if (Items(b, false).Count == 0) return [];
        string[]? last = null;
        foreach (var (shortWords, inline) in new[] { (false, false), (true, false), (true, true) })
        {
            var lines = Pack(Why(b, shortWords && inline), Items(b, shortWords), m.LineWidth, inline);
            if (lines.Length <= m.MaxLines && lines.All(l => l.Length <= m.LineWidth)) return lines;
            last = lines;
        }
        return last!;
    }

    static string[] Pack(string why, List<string> items, int width, bool inline)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        if (inline) line.Append(why);
        else lines.Add(why);
        for (var i = 0; i < items.Count; i++)
        {
            var piece = items[i] + (i < items.Count - 1 ? "," : "");
            if (line.Length > 0 && line.Length + 1 + piece.Length > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(piece);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines.ToArray();
    }

    /// <summary>The panel's HTML: the reason gold and bold, the rest white, only the tags CounterStrikeSharp's own centre menu uses.</summary>
    public static string Html(string[] lines, Boost b)
    {
        if (lines.Length == 0) return "";
        var why = Why(b, false);
        var whyShort = Why(b, true);
        var html = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) html.Append("<br>");
            var line = lines[i];
            var head = i == 0 && line.StartsWith(why, StringComparison.Ordinal) ? why
                : i == 0 && line.StartsWith(whyShort, StringComparison.Ordinal) ? whyShort : "";
            var colour = b.IsBounty ? "#FF6347" : "#FFD700";
            if (head.Length > 0)
            {
                html.Append($"<font color='{colour}'><b>{Escape(head)}</b></font>");
                line = line[head.Length..];
            }
            if (line.Length > 0) html.Append(Escape(line));
        }
        return html.ToString();
    }

    static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The server log's line for one decision. Names come from the game at run time.</summary>
    public static string Decision(string who, Boost b, Standings s, string? leaderName, int baseHealth, int spawnHealth)
    {
        var standing = $"(leader {leaderName ?? "tied"} {s.Top}, them {s.Top - b.KillsBehind}, {s.Players} players)";
        if (b.IsBounty) return $"{who} leads by {Kills(b.Lead)} ({s.Top} to {s.Second}, {s.Players} players): bounty +{Pct(b.Bounty)} damage taken from enemies";
        if (!b.IsBoost) return $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: no boost";
        var parts = new List<string>();
        if (b.Health > 0) parts.Add($"+{Pct(b.Health)} health ({baseHealth} -> {spawnHealth})");
        if (b.DamageTaken < 0) parts.Add($"-{Pct(b.DamageTaken)} damage taken");
        if (b.DamageDealt > 0) parts.Add($"+{Pct(b.DamageDealt)} damage");
        if (b.Lifesteal > 0) parts.Add($"{Pct(b.Lifesteal)} lifesteal");
        if (b.RegenPerSecond > 0) parts.Add($"regeneration {Pct(b.RegenPerSecond)}/s after {Sec(b.RegenDelaySeconds)} s");
        if (b.SecondChance) parts.Add("second chance");
        if (b.ExtraProtection > 0) parts.Add($"+{Sec(b.ExtraProtection)} s spawn protection");
        if (b.InfiniteAmmo) parts.Add("infinite ammo");
        if (b.OneHitMelee) parts.Add("one-hit knife and Zeus");
        var cap = b.Capped
            ? string.Create(CultureInfo.InvariantCulture, $"capped: duel factor {b.RawFactor:0.00} -> {b.Factor:0.00}, allowed {b.Allowed:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"duel factor {b.Factor:0.00}, allowed {b.Allowed:0.00}");
        return string.Create(CultureInfo.InvariantCulture, $"{who} spawns {Kills(b.KillsBehind)} behind {standing}: tier {b.Tier}+: {string.Join(", ", parts)}; {cap}");
    }
}
