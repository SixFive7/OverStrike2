// The catch-up module's settings: catchup.json beside the plugin DLL. Plain BCL (System.Text.Json), no CounterStrikeSharp type,
// so the self-test compiles this file in source. Every number the owner may want to tune lives here, never in the rules.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatchUp;

/// <summary>Everything that can be tuned, with the defaults the module ships with. catchup.json holds the same values (the self-test
/// fails when they drift apart). A missing or unreadable file means these defaults, and the log says which.</summary>
public sealed class CatchUpConfig
{
    public const string FileName = "catchup.json";

    /// <summary>Master switch. Off: no boost, no bounty, no message; the kill counting still runs, so switching it on mid-match works.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Decide every living player's boost again a frame after each change to the ladder (a kill, a team kill, a demotion, a death
    /// without an enemy, a player leaving or changing team), not only at spawn: a boost shrinks as soon as its
    /// gap closes, so it cannot carry a streak, and grows when the leader pulls away. Within a life health only goes up (a higher maximum; the
    /// current health is never cut), and when the boost shrinks the other three values take the cut (Rules.HoldHealth); spawn protection and
    /// the message stay the spawn's. Off: decided at spawn only, kept for the life.</summary>
    public bool RecalculateOnKill { get; set; } = true;

    /// <summary>The boosts by kills behind the leader, lowest first. A player gets the last tier whose MinBehind they reach.</summary>
    public List<TierConfig> Tiers { get; set; } = DefaultTiers();

    public BountyConfig Bounty { get; set; } = new();
    public BalanceConfig Balance { get; set; } = new();
    public ProgressConfig Progress { get; set; } = new();
    public MessageConfig Message { get; set; } = new();
    public EffectsConfig Effects { get; set; } = new();

    /// <summary>Curve B: one tier per gap from 2 to 8+, little boost near the top and much more far behind.
    /// The own factor of each, (1 + health + lifesteal) x (1 + damage dealt) / (1 + damage taken), follows the steep quadratic 1 + 0.10 x
    /// (gap - 1)^2 in whole 5% steps: 1.10, 1.40, 1.89, 2.56, 3.48, 4.57 and 5.95. The extras keep their bands: regeneration 3%/s after 4 s
    /// at 2-3 behind, 5%/s after 3 s at 4-6, 8%/s after 2 s from 7; a second chance and 2 s more spawn protection from 4; 3 s more, infinite
    /// ammo and the one-hit knife and Zeus from 7.</summary>
    public static List<TierConfig> DefaultTiers() =>
    [
        new() { MinBehind = 2, Health = 0.05, DamageTaken = 0.00, DamageDealt = 0.00, Lifesteal = 0.05, RegenPerSecond = 0.03, RegenDelaySeconds = 4 },
        new() { MinBehind = 3, Health = 0.15, DamageTaken = -0.10, DamageDealt = 0.05, Lifesteal = 0.05, RegenPerSecond = 0.03, RegenDelaySeconds = 4 },
        new() { MinBehind = 4, Health = 0.35, DamageTaken = -0.15, DamageDealt = 0.15, Lifesteal = 0.05, RegenPerSecond = 0.05, RegenDelaySeconds = 3,
                SecondChance = true, ExtraSpawnProtectionSeconds = 2 },
        new() { MinBehind = 5, Health = 0.50, DamageTaken = -0.25, DamageDealt = 0.20, Lifesteal = 0.10, RegenPerSecond = 0.05, RegenDelaySeconds = 3,
                SecondChance = true, ExtraSpawnProtectionSeconds = 2 },
        new() { MinBehind = 6, Health = 0.75, DamageTaken = -0.30, DamageDealt = 0.25, Lifesteal = 0.20, RegenPerSecond = 0.05, RegenDelaySeconds = 3,
                SecondChance = true, ExtraSpawnProtectionSeconds = 2 },
        new() { MinBehind = 7, Health = 0.95, DamageTaken = -0.35, DamageDealt = 0.35, Lifesteal = 0.25, RegenPerSecond = 0.08, RegenDelaySeconds = 2,
                SecondChance = true, ExtraSpawnProtectionSeconds = 3, InfiniteAmmo = true, OneHitKnifeAndTaser = true },
        new() { MinBehind = 8, Health = 1.20, DamageTaken = -0.40, DamageDealt = 0.40, Lifesteal = 0.35, RegenPerSecond = 0.08, RegenDelaySeconds = 2,
                SecondChance = true, ExtraSpawnProtectionSeconds = 3, InfiniteAmmo = true, OneHitKnifeAndTaser = true },
    ];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads the file; never throws. Notes say what was not taken as written (a missing file, a value out of range).</summary>
    public static CatchUpConfig Load(string path, List<string> notes)
    {
        CatchUpConfig config;
        if (!File.Exists(path))
        {
            notes.Add($"no {Path.GetFileName(path)}: built-in defaults");
            config = new CatchUpConfig();
        }
        else
        {
            try
            {
                config = Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                notes.Add($"{Path.GetFileName(path)} unreadable ({e.GetType().Name}: {e.Message}): built-in defaults");
                config = new CatchUpConfig();
            }
        }
        config.Normalize(notes);
        return config;
    }

    /// <summary>The JSON text as a config, not yet normalized. Throws JsonException on bad JSON.</summary>
    public static CatchUpConfig Parse(string json) => JsonSerializer.Deserialize<CatchUpConfig>(json, Json) ?? new CatchUpConfig();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Puts every value inside its range and the tiers in order, noting each change. Run once after reading.</summary>
    public void Normalize(List<string> notes)
    {
        Tiers ??= DefaultTiers();
        Bounty ??= new();
        Balance ??= new();
        Progress ??= new();
        Message ??= new();
        Effects ??= new();
        Tiers.RemoveAll(t => t == null);
        foreach (var t in Tiers) t.Normalize(notes);
        if (Tiers.Select(t => t.MinBehind).Distinct().Count() != Tiers.Count) notes.Add("two tiers with the same MinBehind: the later one wins");
        Tiers = Tiers.OrderBy(t => t.MinBehind).GroupBy(t => t.MinBehind).Select(g => g.Last()).ToList();
        Bounty.Normalize(notes);
        Balance.Normalize(notes);
        Progress.Normalize(notes);
        Message.Normalize(notes);
        Effects.Normalize(notes);
    }

    internal static double Clamp(double value, double min, double max, string name, List<string> notes)
    {
        if (double.IsNaN(value)) { notes.Add($"{name} is not a number: {min} used"); return min; }
        if (value >= min && value <= max) return value;
        var kept = Math.Clamp(value, min, max);
        notes.Add($"{name} {value} is outside {min}..{max}: {kept} used");
        return kept;
    }

    internal static int Clamp(int value, int min, int max, string name, List<string> notes)
    {
        if (value >= min && value <= max) return value;
        var kept = Math.Clamp(value, min, max);
        notes.Add($"{name} {value} is outside {min}..{max}: {kept} used");
        return kept;
    }
}

/// <summary>One step of the boost table. Fractions are relative: Health 0.40 is 40% more than the health the player spawned with
/// (100 normally, 1,000 under a mode that gives that), DamageTaken -0.20 is 20% less damage from enemies.</summary>
public sealed class TierConfig
{
    /// <summary>From this many kills behind the leader (the leader's progress minus the player's).</summary>
    public int MinBehind { get; set; }

    /// <summary>Extra health at spawn, a fraction of the spawn health.</summary>
    public double Health { get; set; }

    /// <summary>Change in damage taken from enemies: negative is less (-0.20 = 20% less).</summary>
    public double DamageTaken { get; set; }

    /// <summary>Extra damage dealt to enemies (0.20 = 20% more).</summary>
    public double DamageDealt { get; set; }

    /// <summary>Health gained per hit on an enemy, a fraction of the health they lost to it.</summary>
    public double Lifesteal { get; set; }

    /// <summary>Regeneration per second, a fraction of the spawn health, once RegenDelaySeconds have passed without being hurt.</summary>
    public double RegenPerSecond { get; set; }

    public double RegenDelaySeconds { get; set; } = 5;

    /// <summary>Survive one killing hit from an enemy with 1 health, once per life.</summary>
    public bool SecondChance { get; set; }

    /// <summary>Seconds of spawn protection on top of the game's own (mp_respawn_immunitytime). Ends at the player's first attack.</summary>
    public double ExtraSpawnProtectionSeconds { get; set; }

    /// <summary>The gun in hand is refilled after every shot: no reloads.</summary>
    public bool InfiniteAmmo { get; set; }

    /// <summary>A knife or Zeus hit on an enemy kills.</summary>
    public bool OneHitKnifeAndTaser { get; set; }

    internal void Normalize(List<string> notes)
    {
        var at = $"tier {MinBehind}+";
        MinBehind = CatchUpConfig.Clamp(MinBehind, 1, 1000, "Tiers.MinBehind", notes);
        Health = CatchUpConfig.Clamp(Health, 0, 5, $"{at} Health", notes);
        DamageTaken = CatchUpConfig.Clamp(DamageTaken, -0.9, 0, $"{at} DamageTaken", notes);
        DamageDealt = CatchUpConfig.Clamp(DamageDealt, 0, 5, $"{at} DamageDealt", notes);
        Lifesteal = CatchUpConfig.Clamp(Lifesteal, 0, 1, $"{at} Lifesteal", notes);
        RegenPerSecond = CatchUpConfig.Clamp(RegenPerSecond, 0, 1, $"{at} RegenPerSecond", notes);
        RegenDelaySeconds = CatchUpConfig.Clamp(RegenDelaySeconds, 0, 60, $"{at} RegenDelaySeconds", notes);
        ExtraSpawnProtectionSeconds = CatchUpConfig.Clamp(ExtraSpawnProtectionSeconds, 0, 30, $"{at} ExtraSpawnProtectionSeconds", notes);
    }
}

/// <summary>The leader's handicap: extra damage taken from enemies, by how far the leader is ahead of second place.</summary>
public sealed class BountyConfig
{
    /// <summary>No bounty below this lead over second place.</summary>
    public int MinLead { get; set; } = 2;

    /// <summary>Every this many kills of lead adds PerStep.</summary>
    public int StepKills { get; set; } = 2;

    public double PerStep { get; set; } = 0.10;

    public double Max { get; set; } = 0.30;

    internal void Normalize(List<string> notes)
    {
        MinLead = CatchUpConfig.Clamp(MinLead, 1, 1000, "Bounty.MinLead", notes);
        StepKills = CatchUpConfig.Clamp(StepKills, 1, 1000, "Bounty.StepKills", notes);
        PerStep = CatchUpConfig.Clamp(PerStep, 0, 2, "Bounty.PerStep", notes);
        Max = CatchUpConfig.Clamp(Max, 0, 2, "Bounty.Max", notes);
    }
}

/// <summary>How a boost is weighed against the leader: the duel factor the log shows (Rules.DuelFactor), how much of the leader's bounty
/// a boosted player's hits on him get, and the mid-life cut (Rules.HoldHealth).</summary>
public sealed class BalanceConfig
{
    /// <summary>The most a boosted player's duel factor against the leader may reach with his bounty: on each of their hits on him the
    /// bounty counts only up to this over their own factor, never below no bounty at all. The strongest tier, 5.95, times the largest
    /// bounty, 1.30, is 7.74: under 8, so every hit gets the whole bounty.</summary>
    public double MaxDuelFactor { get; set; } = 8.0;

    /// <summary>Lifesteal counts as health in the duel factor (in a duel it returns that share of the leader's health).</summary>
    public bool LifestealCounts { get; set; } = true;

    /// <summary>A value cut in the middle of a life (Rules.HoldHealth) is rounded down to a multiple of this (0.05 = whole 5% steps), so
    /// the numbers stay readable.</summary>
    public double RoundTo { get; set; } = 0.05;

    internal void Normalize(List<string> notes)
    {
        MaxDuelFactor = CatchUpConfig.Clamp(MaxDuelFactor, 1, 100, "Balance.MaxDuelFactor", notes);
        RoundTo = CatchUpConfig.Clamp(RoundTo, 0, 0.5, "Balance.RoundTo", notes);
    }
}

/// <summary>How Arms Race moves a player up and down its ladder, as the kill counter models it. The defaults are CS2's rules as this
/// server's own match logs show them: the self-test replays one match of 2026-09-19 and four of 2026-11-04, and 543 of 543 gun kills were made
/// with the gun the model puts in the killer's hands.</summary>
public sealed class ProgressConfig
{
    public int KillsPerLevel { get; set; } = 2;

    /// <summary>A knife kill moves the killer up a level, the kills on the level kept (level 5 + 1 becomes 6 + 1), with the new level's gun.</summary>
    public bool KnifeKillLevelsUp { get; set; } = true;

    /// <summary>Killed by an enemy's knife: down one level, the kills on it kept (7 + 1 becomes 6 + 1); at level 0 the kill on it goes instead.</summary>
    public bool KnifeDeathDemotes { get; set; } = true;

    /// <summary>Killed by an enemy's Zeus: one kill of progress lost (7 + 0 becomes 6 + 1; nothing at 0 + 0).</summary>
    public bool TaserDeathCostsKill { get; set; } = true;

    /// <summary>Death without an enemy (the world, a fall, drowning, a team change): at level 0 the kill on the level goes, as CS2 does it.
    /// Above level 0 CS2 was never seen: down one level with its kills, as in CS:GO's Arms Race, and placed again by the next gun kill, so
    /// the count heals whatever the game did.</summary>
    public bool SuicideDemotes { get; set; } = true;

    /// <summary>A team kill (friendly fire, without free-for-all): the killer down one level, the kills on it kept (9 + 1 becomes 8 + 1; at
    /// level 0 the kill on it goes instead), as Arms Race punishes it. The killer keeps the gun in hand, the old level's, until he dies, and
    /// kills with it count on the lower level. The victim loses nothing. Ignoring team kills lets the counts run past the golden knife: 38 and
    /// 40 of 32 were measured for two teammates, with friendly fire and Instakill on.</summary>
    public bool TeamKillDemotes { get; set; } = true;

    /// <summary>On the last level (the golden knife) only a knife kill counts.</summary>
    public bool LastLevelOnlyKnife { get; set; } = true;

    internal void Normalize(List<string> notes)
    {
        KillsPerLevel = CatchUpConfig.Clamp(KillsPerLevel, 1, 10, "Progress.KillsPerLevel", notes);
    }
}

/// <summary>The private centre-screen message at spawn.</summary>
public sealed class MessageConfig
{
    public bool ToBoosted { get; set; } = true;
    public bool ToLeader { get; set; } = true;

    /// <summary>How long it stays up (the centre panel's own duration, whole seconds) before it fades.</summary>
    public int Seconds { get; set; } = 2;

    /// <summary>After a spawn, once the boost is applied.</summary>
    public double DelaySeconds { get; set; } = 0.25;

    /// <summary>The first spawn after joining waits this long, for the join tagline to clear.</summary>
    public double FirstSpawnDelaySeconds { get; set; } = 3.0;

    /// <summary>The centre panel wraps at about 31 characters and scrolls taller content (measured 2026-10-04): stay inside these.</summary>
    public int LineWidth { get; set; } = 28;

    public int MaxLines { get; set; } = 5;

    internal void Normalize(List<string> notes)
    {
        Seconds = CatchUpConfig.Clamp(Seconds, 1, 10, "Message.Seconds", notes);
        DelaySeconds = CatchUpConfig.Clamp(DelaySeconds, 0, 10, "Message.DelaySeconds", notes);
        FirstSpawnDelaySeconds = CatchUpConfig.Clamp(FirstSpawnDelaySeconds, 0, 30, "Message.FirstSpawnDelaySeconds", notes);
        LineWidth = CatchUpConfig.Clamp(LineWidth, 16, 80, "Message.LineWidth", notes);
        MaxLines = CatchUpConfig.Clamp(MaxLines, 1, 12, "Message.MaxLines", notes);
    }
}

/// <summary>Timing of the effects in game.</summary>
public sealed class EffectsConfig
{
    /// <summary>After a spawn, before the boost is decided and applied: the game sets the spawn health and loadout first.</summary>
    public double ApplyDelaySeconds { get; set; } = 0.1;

    /// <summary>How often regeneration is applied.</summary>
    public double RegenTickSeconds { get; set; } = 0.5;

    internal void Normalize(List<string> notes)
    {
        ApplyDelaySeconds = CatchUpConfig.Clamp(ApplyDelaySeconds, 0, 2, "Effects.ApplyDelaySeconds", notes);
        RegenTickSeconds = CatchUpConfig.Clamp(RegenTickSeconds, 0.1, 5, "Effects.RegenTickSeconds", notes);
    }
}
