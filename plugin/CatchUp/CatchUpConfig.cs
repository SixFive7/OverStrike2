// The catch-up module's settings: catchup.json beside the plugin DLL. Plain BCL (System.Text.Json), no CounterStrikeSharp type,
// so the self-test compiles this file in source. The file holds what is tuned - the boost table and the leader's bounty; how the game
// counts the ladder, the mid-life cut's steps and the message's timing are the rules', in the code (Ladder, Rules, Words).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatchUp;

/// <summary>What can be tuned, with the defaults the module ships with. catchup.json holds the same values (the self-test fails when they
/// drift apart). A missing or unreadable file means these defaults, and the log says which. A key the module does not read - one an older
/// file still holds - is ignored.</summary>
public sealed class CatchUpConfig
{
    public const string FileName = "catchup.json";

    /// <summary>The boosts by kills behind the leader, lowest first. A player gets the last tier whose MinBehind they reach.</summary>
    public List<TierConfig> Tiers { get; set; } = DefaultTiers();

    public BountyConfig Bounty { get; set; } = new();

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
        Tiers.RemoveAll(t => t == null);
        foreach (var t in Tiers) t.Normalize(notes);
        if (Tiers.Select(t => t.MinBehind).Distinct().Count() != Tiers.Count) notes.Add("two tiers with the same MinBehind: the later one wins");
        Tiers = Tiers.OrderBy(t => t.MinBehind).GroupBy(t => t.MinBehind).Select(g => g.Last()).ToList();
        Bounty.Normalize(notes);
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
