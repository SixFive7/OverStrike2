using System.Globalization;
using CatchUp;

// CatchUp.SelfTest - every decision of the catch-up module, without a game server: the config and its file, the tiers and the
// bounty, the balance cap, each hit, the healing, the Arms Race kill counter (unit rules, and a whole real match replayed), the
// message and its timing. CatchUpConfig.cs and CatchUpRules.cs are compiled in as source; CatchUpModule.cs (the game half) is not,
// and is held to these by the compile check (../CatchUp.CompileCheck.csproj).
//
// Run with `dotnet run -c Release`; exit code 0 means every check passed. `--table` also prints the tier table and the cap's examples.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var failures = 0;
var checks = 0;
var here = AppContext.BaseDirectory;
var printTable = args.Contains("--table");

void Check(bool ok, string what)
{
    checks++;
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL  {what}");
}

void Eq<T>(T actual, T expected, string what) =>
    Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

void Near(double actual, double expected, string what, double tolerance = 1e-6) =>
    Check(Math.Abs(actual - expected) <= tolerance, $"{what}: expected {expected}, got {actual}");

CatchUpConfig Defaults()
{
    var c = new CatchUpConfig();
    c.Normalize(new List<string>());
    return c;
}

// The numbers before 2026-11-03 (the owner made catch-up a little stronger that evening). The mechanics below - the bounty steps, the cap,
// each hit, the healing, the message - are checked against these, so tuning the defaults moves only the checks of the defaults themselves.
static CatchUpConfig Reference()
{
    var c = new CatchUpConfig
    {
        Tiers =
        [
            new() { MinBehind = 2, Health = 0.20, DamageTaken = -0.10, DamageDealt = 0.10, Lifesteal = 0.10, RegenPerSecond = 0.02, RegenDelaySeconds = 5 },
            new() { MinBehind = 4, Health = 0.40, DamageTaken = -0.20, DamageDealt = 0.20, Lifesteal = 0.20, RegenPerSecond = 0.04, RegenDelaySeconds = 4,
                    SecondChance = true, ExtraSpawnProtectionSeconds = 2 },
            new() { MinBehind = 7, Health = 0.75, DamageTaken = -0.30, DamageDealt = 0.30, Lifesteal = 0.30, RegenPerSecond = 0.06, RegenDelaySeconds = 3,
                    SecondChance = true, ExtraSpawnProtectionSeconds = 3, InfiniteAmmo = true, OneHitKnifeAndTaser = true },
        ],
    };
    (c.Bounty.Max, c.Balance.MaxDuelFactor, c.Balance.ScoreboardCapFactor) = (0.30, 3.0, 1.0);
    c.Normalize(new List<string>());
    return c;
}

Standings Stand(params (ulong Id, int Progress)[] players) => new(players.ToDictionary(p => p.Id, p => p.Progress));

// --spawns <csv>: the cap on a log's spawns (the analysis's spawns.csv: behind, top, mine, lead_est), as the leader's enemy and as his
// teammate, with the defaults - one line a spawn, for a cross-check against another implementation. Not a check: it changes nothing.
if (args.Length >= 2 && args[0] == "--spawns")
{
    var cfg = Defaults();
    var rows = File.ReadAllLines(args[1]);
    var head = rows[0].Split(',');
    int Col(string n) => Array.IndexOf(head, n);
    var (ckind, cbehind, ctop, cmine, clead) = (Col("kind"), Col("behind"), Col("top"), Col("mine"), Col("lead_est"));
    foreach (var line in rows.Skip(1))
    {
        var f = line.Split(',');
        if (f.Length <= clead || f[ckind] != "spawn" || !int.TryParse(f[cbehind], out var behind) || !int.TryParse(f[ctop], out var top)
            || !int.TryParse(f[cmine], out var mine)) continue;
        var lead = int.TryParse(f[clead], out var l) ? l : 0;
        var bounty = Rules.BountyFor(cfg.Bounty, lead);
        var e = Rules.ForGap(cfg, behind, top, mine, bounty);
        var t = Rules.ForGap(cfg, behind, top, mine, 0);
        Console.WriteLine(string.Join(",", behind, top, mine, lead, $"{bounty:0.00}", $"{e.Allowed:0.0000}", $"{e.Factor:0.0000}", $"{e.Health:0.00}", $"{e.DamageTaken:0.00}", $"{e.DamageDealt:0.00}", $"{e.Lifesteal:0.00}",
            $"{t.Allowed:0.0000}", $"{t.Factor:0.0000}", $"{t.Health:0.00}", $"{t.DamageTaken:0.00}", $"{t.DamageDealt:0.00}", $"{t.Lifesteal:0.00}"));
    }
    return 0;
}

// ---- config: the defaults, the shipped file, and what a bad file does ----

{
    var notes = new List<string>();
    var d = new CatchUpConfig();
    d.Normalize(notes);
    Eq(notes.Count, 0, "the defaults need no correction");
    Eq(d.Tiers.Count, 3, "three tiers by default");
    Eq(string.Join(",", d.Tiers.Select(t => t.MinBehind)), "2,4,7", "tiers from 2, 4 and 7 behind");

    var shipped = Path.Combine(here, CatchUpConfig.FileName);
    Check(File.Exists(shipped), "catchup.json is copied beside the test");
    var fileNotes = new List<string>();
    var f = CatchUpConfig.Load(shipped, fileNotes);
    Eq(fileNotes.Count, 0, $"catchup.json needs no correction ({string.Join("; ", fileNotes)})");
    Eq(f.ToJson(), d.ToJson(), "catchup.json holds exactly the built-in defaults");

    var missing = new List<string>();
    var m = CatchUpConfig.Load(Path.Combine(here, "no-such-file.json"), missing);
    Check(missing.Count == 1 && missing[0].StartsWith("no no-such-file.json"), "a missing file is noted");
    Eq(m.ToJson(), d.ToJson(), "a missing file means the defaults");

    var badPath = Path.Combine(Path.GetTempPath(), $"catchup-selftest-{Environment.ProcessId}.json");
    File.WriteAllText(badPath, "{ \"Enabled\": tru");
    var bad = new List<string>();
    var b = CatchUpConfig.Load(badPath, bad);
    File.Delete(badPath);
    Check(bad.Count == 1 && bad[0].Contains("unreadable"), "bad JSON is noted");
    Eq(b.ToJson(), d.ToJson(), "bad JSON means the defaults");

    var loose = CatchUpConfig.Parse("{ // comments and trailing commas are allowed\n \"enabled\": false, \"bounty\": { \"max\": 0.5, }, }");
    loose.Normalize(new List<string>());
    Eq(loose.Enabled, false, "property names in any case");
    Near(loose.Bounty.Max, 0.5, "a nested value read");
    Eq(loose.Tiers.Count, 3, "a missing section keeps its defaults");

    var wild = CatchUpConfig.Parse("""
        { "Tiers": [ { "MinBehind": 9, "Health": 9, "DamageTaken": 0.5 }, { "MinBehind": 3, "DamageTaken": -2, "Lifesteal": 2 },
                     { "MinBehind": 3, "Health": 0.1 } ],
          "Message": { "Seconds": 0, "LineWidth": 3 }, "Progress": { "KillsPerLevel": 0 }, "Balance": { "MaxDuelFactor": 0.5 } }
        """);
    var wildNotes = new List<string>();
    wild.Normalize(wildNotes);
    Eq(string.Join(",", wild.Tiers.Select(t => t.MinBehind)), "3,9", "tiers sorted, a duplicate MinBehind once");
    Near(wild.Tiers[0].Health, 0.1, "the later of two tiers with one MinBehind wins");
    Near(wild.Tiers[1].Health, 5, "health clamped to 5");
    Near(wild.Tiers[1].DamageTaken, 0, "damage taken cannot be raised by a tier");
    Eq(wild.Message.Seconds, 1, "Seconds at least 1");
    Eq(wild.Message.LineWidth, 16, "LineWidth at least 16");
    Eq(wild.Progress.KillsPerLevel, 1, "KillsPerLevel at least 1");
    Near(wild.Balance.MaxDuelFactor, 1, "MaxDuelFactor at least 1");
    Check(wildNotes.Count >= 8, $"every correction noted ({wildNotes.Count})");

    var nulls = CatchUpConfig.Parse("{ \"Tiers\": null, \"Bounty\": null }");
    nulls.Normalize(new List<string>());
    Eq(nulls.Tiers.Count, 3, "null tiers: the defaults");
    Eq(nulls.Bounty.MinLead, 2, "a null section: its defaults");
}

// ---- tiers and bounty ----

{
    var c = Reference();
    foreach (var (behind, tier) in new[] { (0, 0), (1, 0), (2, 2), (3, 2), (4, 4), (5, 4), (6, 4), (7, 7), (8, 7), (30, 7) })
        Eq(Rules.TierFor(c.Tiers, behind)?.MinBehind ?? 0, tier, $"{behind} behind: tier");
    foreach (var (lead, bounty) in new[] { (0, 0.0), (1, 0.0), (2, 0.1), (3, 0.1), (4, 0.2), (5, 0.2), (6, 0.3), (7, 0.3), (20, 0.3) })
        Near(Rules.BountyFor(c.Bounty, lead), bounty, $"lead {lead}: bounty");

    var s = Stand((1, 9), (2, 4), (3, 6), (4, 9 - 1));
    Eq(s.Leader, 1UL, "the one on top leads");
    Eq(s.Lead, 1, "lead over second place");
    Eq(s.Behind(2), 5, "kills behind the leader");
    var lead1 = Rules.Decide(c, s, 1);
    Check(!lead1.IsBounty && !lead1.IsBoost, "a lead of 1: no bounty");
    var clear = Stand((1, 12), (2, 4), (3, 6), (4, 8));
    var leader = Rules.Decide(c, clear, 1);
    Check(leader.IsBounty && Math.Abs(leader.Bounty - 0.2) < 1e-9 && leader.Lead == 4, "a lead of 4: +20% bounty");
    Check(!leader.IsBoost, "the leader gets no boost");
    var tie = Stand((1, 10), (2, 10), (3, 2));
    Eq(tie.Leader, 0UL, "a tie at the top: no leader");
    Check(!Rules.Decide(c, tie, 1).IsBounty, "a tie: no bounty");
    Eq(Rules.Decide(c, tie, 3).KillsBehind, 8, "a tie: behind is measured from the top");
    Check(!Rules.Decide(c, Stand((1, 9)), 1).IsBounty, "alone on the server: nothing");
    Check(!Rules.Decide(c, Stand((1, 9), (2, 8)), 2).IsBoost, "1 behind: nothing");

    // The table as configured, with the cap out of the way: every value reaches the boost.
    var open = Reference();
    open.Balance.ScoreboardCap = false;
    open.Balance.MaxDuelFactor = 100;
    foreach (var t in open.Tiers)
    {
        var bo = Rules.ForGap(open, t.MinBehind, t.MinBehind, 0, 0);
        Check(!bo.Capped && bo.Tier == t.MinBehind && bo.Health == t.Health && bo.DamageTaken == t.DamageTaken && bo.DamageDealt == t.DamageDealt
              && bo.Lifesteal == t.Lifesteal && bo.RegenPerSecond == t.RegenPerSecond && bo.SecondChance == t.SecondChance
              && bo.ExtraProtection == t.ExtraSpawnProtectionSeconds && bo.InfiniteAmmo == t.InfiniteAmmo && bo.OneHitMelee == t.OneHitKnifeAndTaser,
            $"tier {t.MinBehind}+ uncapped: the table's values");
    }
    // The owner's own example, word for word (2026-11-01), on the numbers of then.
    Eq(Words.Sentence(Rules.ForGap(open, 5, 5, 0, 0)),
        "You are 5 kills behind the leader: +40% health, -20% damage taken, +20% damage, 20% lifesteal, regeneration, a second chance, +2 s spawn protection.",
        "the owner's example sentence");
    // ...and on the defaults of 2026-11-03
    var now5 = Defaults();
    (now5.Balance.ScoreboardCap, now5.Balance.MaxDuelFactor) = (false, 100);
    Eq(Words.Sentence(Rules.ForGap(now5, 5, 5, 0, 0)),
        "You are 5 kills behind the leader: +60% health, -25% damage taken, +25% damage, 25% lifesteal, regeneration, a second chance, +2 s spawn protection.",
        "the same sentence on today's numbers");
    Eq(Words.Sentence(new Boost { Lead = 4, Bounty = 0.2 }), "You lead by 4 kills: +20% damage taken (a bounty).", "the leader's sentence");
    Eq(Words.Sentence(Boost.None), "", "nothing to say without a boost");
}

// ---- the balance cap ----

{
    var c = Reference();
    Near(Rules.DuelFactor(0.20, 0.10, 0.10, -0.10, 0, true), 1.3 * 1.1 / 0.9, "tier 2+ duel factor");
    Near(Rules.DuelFactor(0.40, 0.20, 0.20, -0.20, 0, true), 2.4, "tier 4+ duel factor");
    Near(Rules.DuelFactor(0.75, 0.30, 0.30, -0.30, 0, true), 2.05 * 1.3 / 0.7, "tier 7+ duel factor");
    Near(Rules.DuelFactor(0.75, 0.30, 0.30, -0.30, 0, false), 1.75 * 1.3 / 0.7, "without lifesteal");
    Near(Rules.DuelFactor(0, 0, 0, 0, 0.3, true), 1.3, "the bounty alone");
    Near(Rules.Allowed(c.Balance, 16, 8), 17.0 / 9.0, "allowed: the scoreboard's ratio");
    Near(Rules.Allowed(c.Balance, 9, 0), 3.0, "allowed: never more than MaxDuelFactor");
    var noBoard = Reference();
    noBoard.Balance.ScoreboardCap = false;
    Near(Rules.Allowed(noBoard.Balance, 16, 15), 3.0, "without the scoreboard cap: MaxDuelFactor only");
    // The defaults of 2026-11-03 (the owner's), and the worked examples from that evening's log: the caps before and after the change.
    var now = Defaults();
    Check(now.Bounty.Max == 0.30 && now.Balance.MaxDuelFactor == 3.5 && now.Balance.ScoreboardCapFactor == 2.0 && now.Balance.ScoreboardCap
        && now.Bounty.MinLead == 2 && now.Bounty.StepKills == 2 && now.Bounty.PerStep == 0.10,
        "the defaults of 2026-11-03 (the owner's 42 d): the bounty as before (up to 30%), MaxDuelFactor 3.5, the scoreboard cap on at 2.0 times the ratio");
    Check(now.Tiers.Select(t => (t.MinBehind, t.Health, t.DamageTaken, t.DamageDealt, t.Lifesteal, t.RegenPerSecond, t.RegenDelaySeconds)).SequenceEqual(
        [(2, 0.30, -0.15, 0.15, 0.15, 0.03, 4.0), (4, 0.60, -0.25, 0.25, 0.25, 0.05, 3.0), (7, 1.00, -0.35, 0.40, 0.35, 0.08, 2.0)]),
        "the tiers of 2026-11-03: 2+ +30%/-15%/+15%/15%/3%/s after 4 s, 4+ +60%/-25%/+25%/25%/5%/s after 3 s, 7+ +100%/-35%/+40%/35%/8%/s after 2 s");
    Check(now.Tiers.Select(t => (t.SecondChance, t.ExtraSpawnProtectionSeconds, t.InfiniteAmmo, t.OneHitKnifeAndTaser)).SequenceEqual(
        c.Tiers.Select(t => (t.SecondChance, t.ExtraSpawnProtectionSeconds, t.InfiniteAmmo, t.OneHitKnifeAndTaser))), "...and the extras as they were");
    Near(Rules.Allowed(c.Balance, 13, 3), 3.0, "3 vs leader 13: allowed 3.0 before (MaxDuelFactor 3.0, under the ratio 3.5)");
    Near(Rules.Allowed(now.Balance, 13, 3), 3.5, "...and min(3.5, 2.0 x 14/4) = 3.5 now");
    Near(Rules.Allowed(c.Balance, 14, 7), 1.875, "7 vs 14: allowed 1.875 before (the ratio 15/8)");
    Near(Rules.Allowed(now.Balance, 14, 7), 3.5, "...and min(3.5, 2.0 x 15/8) = 3.5 now");
    Near(Rules.Allowed(now.Balance, 30, 23), 2.0 * 31.0 / 24.0, "a late 23 vs 30: 2.0 x 31/24 = 2.58");
    Near(Rules.BountyFor(now.Bounty, 8), 0.30, "a lead of 8: the bounty stays at most 30%");
    // The leader's bounty and his own teammates (the owner's 42 d): a teammate's cap is computed without it, an enemy's counts it first.
    var standings = Stand((1, 20), (2, 12), (3, 10), (4, 8)); // 1 leads by 8: a 30% bounty; 3 is 10 behind (tier 7+)
    var asEnemy = Rules.Decide(now, standings, 3);
    var asMate = Rules.Decide(now, standings, 3, teammateOfLeader: true);
    Near(asEnemy.Allowed, 3.5, "10 vs 20: allowed min(3.5, 2.0 x 21/11) = 3.5, the same for the leader's enemy and teammate");
    Near(asMate.Allowed, 3.5, "...");
    Check(asEnemy.Capped && asMate.Capped && asMate.Health > asEnemy.Health && asMate.DamageDealt >= asEnemy.DamageDealt,
        $"...the enemy's own boosts share 3.5 / 1.3 (the bounty first), the teammate's the whole 3.5: health +{asEnemy.Health:0.00} for the enemy, +{asMate.Health:0.00} for the teammate");
    var mateOwn = Rules.DuelFactor(asMate.Health, asMate.Lifesteal, asMate.DamageDealt, asMate.DamageTaken, 0, true);
    var enemyOwn = Rules.DuelFactor(asEnemy.Health, asEnemy.Lifesteal, asEnemy.DamageDealt, asEnemy.DamageTaken, 0, true);
    Check(mateOwn <= 3.5 + 1e-9 && enemyOwn <= 3.5 / 1.3 + 1e-9 && Math.Abs(asMate.Factor - mateOwn) < 1e-9 && asEnemy.Factor <= 3.5 + 1e-9,
        $"...the teammate's own factor {mateOwn:0.00} within 3.5, the enemy's {enemyOwn:0.00} within 3.5 / 1.3 and {asEnemy.Factor:0.00} with the bounty");
    var tight = Stand((1, 9), (2, 3), (3, 7)); // 1 leads by 2 over 3: a 10% bounty; 2 is 6 behind
    Check(Rules.Decide(now, tight, 2, teammateOfLeader: true).Health >= Rules.Decide(now, tight, 2).Health, "a teammate never gets less than an enemy in the same place");
    Check(Rules.Decide(now, standings, 1, teammateOfLeader: true) is { IsBounty: true, Bounty: 0.30 }, "the leader's own bounty does not depend on the flag");
    var offCap = Defaults();
    offCap.Balance.ScoreboardCap = false;
    Near(Rules.Allowed(offCap.Balance, 14, 7), 3.5, "the scoreboard cap still switches off: MaxDuelFactor alone");
    var worstNow = 0.0;
    for (var top = 2; top <= 40; top++)
        for (var lead = 0; lead <= 10; lead++)
            for (var mine = 0; mine <= top - 2; mine++)
                worstNow = Math.Max(worstNow, Rules.ForGap(now, top - mine, top, mine, Rules.BountyFor(now.Bounty, lead)).Factor);
    Check(worstNow <= 3.5 + 1e-9, $"with today's defaults the duel factor never goes above 3.5 (worst {worstNow:0.00})");

    var worst = 0.0;
    var notStep = 0;
    var overTable = 0;
    var overAllowed = 0;
    for (var top = 2; top <= 40; top++)
        for (var lead = 0; lead <= 8; lead++)
            for (var mine = 0; mine <= top - 2; mine++)
            {
                var bounty = Rules.BountyFor(c.Bounty, lead);
                var b = Rules.ForGap(c, top - mine, top, mine, bounty);
                var t = Rules.TierFor(c.Tiers, top - mine)!;
                if (b.Factor > b.Allowed + 1e-9) overAllowed++;
                if (b.Health > t.Health + 1e-9 || b.DamageDealt > t.DamageDealt + 1e-9 || b.Lifesteal > t.Lifesteal + 1e-9 || b.DamageTaken < t.DamageTaken - 1e-9
                    || b.Health < 0 || b.DamageDealt < 0 || b.Lifesteal < 0 || b.DamageTaken > 0) overTable++;
                if (b.Capped)
                    foreach (var v in new[] { b.Health, b.DamageDealt, b.Lifesteal, -b.DamageTaken })
                        if (Math.Abs(v / 0.05 - Math.Round(v / 0.05)) > 1e-6) notStep++;
                Check(b.SecondChance == t.SecondChance && b.ExtraProtection == t.ExtraSpawnProtectionSeconds && b.InfiniteAmmo == t.InfiniteAmmo
                      && b.OneHitMelee == t.OneHitKnifeAndTaser && b.RegenPerSecond == t.RegenPerSecond, $"top {top} mine {mine}: the extras are the tier's");
                worst = Math.Max(worst, b.Factor);
            }
    Eq(overAllowed, 0, "never above what is allowed (on paper never stronger than the leader has shown to be)");
    Eq(overTable, 0, "the cap only ever takes away from the table");
    Eq(notStep, 0, "capped values are whole 5% steps");
    Check(worst <= 3.0 + 1e-9, $"the duel factor never above 3 (worst {worst:0.00})");

    // As the gap grows against the same leader, the duel factor does not fall (more than the 5% rounding can take).
    var dips = 0.0;
    for (var top = 4; top <= 40; top++)
        for (var lead = 0; lead <= 8; lead++)
        {
            var bounty = Rules.BountyFor(c.Bounty, lead);
            var last = 0.0;
            for (var mine = top - 2; mine >= 0; mine--)
            {
                var f = Rules.ForGap(c, top - mine, top, mine, bounty).Factor;
                dips = Math.Max(dips, last - f);
                last = Math.Max(last, f);
            }
        }
    Check(dips <= 0.16, $"a bigger gap never gives a weaker boost beyond rounding (largest dip {dips:0.000})");

    // The bounty alone already gives all the edge allowed: health, resistance, damage and lifesteal go, the extras stay, and this
    // player's hits on the leader get only as much of the bounty as the cap leaves.
    var none = Rules.ForGap(c, 7, 30, 23, 0.3); // allowed 31/24 = 1.29 < the bounty's 1.3
    Check(none.Capped && none.Health == 0 && none.DamageTaken == 0 && none.DamageDealt == 0 && none.Lifesteal == 0, "a bounty past the cap: no stat boost");
    Check(none.SecondChance && none.InfiniteAmmo && none.OneHitMelee && none.ExtraProtection == 3, "a bounty past the cap: the extras stay");
    Check(none.IsBoost, "...which is still a boost, with a message");
    Near(none.BountyAllowed, 31.0 / 24.0, "...and the bounty on their hits is trimmed to the limit");
    Near(none.Factor, 31.0 / 24.0, "...so the duel factor is exactly the limit");
    var leader = new Boost { Lead = 6, Bounty = 0.3 };
    Near(Rules.Multiplier(none, leader), 31.0 / 24.0, "their hit on the leader: the trimmed bounty", 1e-5);
    Near(Rules.Multiplier(Boost.None, leader), 1.3, "anybody else's hit on the leader: the whole bounty", 1e-5);
    Near(Rules.Multiplier(none, Boost.None), 1.0, "their hit on anybody else: no bounty to trim", 1e-5);

    // Given back after the round-down: a capped boost leaves less than one 5% step of any value unused.
    var slack = 0;
    for (var top = 2; top <= 40; top++)
        for (var mine = 0; mine <= top - 2; mine++)
        {
            var bounty = Rules.BountyFor(c.Bounty, 2);
            var b = Rules.ForGap(c, top - mine, top, mine, bounty);
            if (!b.Capped || b.Allowed / (1 + bounty) <= 1) continue;
            var t = Rules.TierFor(c.Tiers, top - mine)!;
            var target = b.Allowed / (1 + bounty);
            if (b.Health + 0.05 <= t.Health + 1e-9 && Rules.DuelFactor(b.Health + 0.05, b.Lifesteal, b.DamageDealt, b.DamageTaken, 0, true) <= target - 1e-9) slack++;
            if (b.DamageDealt + 0.05 <= t.DamageDealt + 1e-9 && Rules.DuelFactor(b.Health, b.Lifesteal, b.DamageDealt + 0.05, b.DamageTaken, 0, true) <= target - 1e-9) slack++;
        }
    Eq(slack, 0, "no 5% step of health or damage left unused under the cap");

    // An early lead is not capped below the table for the first tiers.
    var early = Rules.ForGap(c, 2, 2, 0, 0);
    Check(!early.Capped && early.Health == 0.20, "2 kills to 0: tier 2+ as the table says");
}

// ---- one hit ----

{
    var c = Reference();
    var open = Reference();
    open.Balance.ScoreboardCap = false;
    open.Balance.MaxDuelFactor = 100;
    var t2 = Rules.ForGap(open, 2, 2, 0, 0);
    var t4 = Rules.ForGap(open, 4, 4, 0, 0);
    var t7 = Rules.ForGap(open, 7, 7, 0, 0);
    var leader = new Boost { Lead = 4, Bounty = 0.2 };
    HitInput H(float damage, Boost attacker, Boost victim, int health = 100, int armor = 0, Melee melee = Melee.None, bool prot = false, bool left = true, bool zero = false) =>
        new(damage, attacker, victim, health, armor, melee, prot, left, zero);

    var plain = Rules.Hit(H(37, Boost.None, Boost.None));
    Check(!plain.Block && plain.Damage == 37 && !plain.SecondChanceUsed && !plain.OneHit, "no boosts: the hit as the game made it");
    Near(Rules.Hit(H(100, t4, Boost.None)).Damage, 120, "+20% damage dealt", 1e-3);
    Near(Rules.Hit(H(100, Boost.None, t7, health: 500)).Damage, 70, "-30% damage taken", 1e-3);
    Near(Rules.Hit(H(100, Boost.None, leader)).Damage, 120, "the leader's +20% bounty", 1e-3);
    Near(Rules.Hit(H(100, t4, leader)).Damage, 144, "boosted damage on the leader with a bounty", 1e-3);
    Near(Rules.Hit(H(100, t2, t7, health: 500)).Damage, 100 * 1.1 * 0.7, "both boosted", 1e-3);
    Check(Rules.Hit(H(50, Boost.None, t4, prot: true)).Block, "extra spawn protection blocks the hit");

    var knife = Rules.Hit(H(40, t7, Boost.None, health: 100, armor: 100, melee: Melee.Knife));
    Check(knife.OneHit && knife.Damage >= 4 * 200, "a 7+ knife kills through armour");
    var zeusOnVampire = Rules.Hit(H(40, t7, Boost.None, health: 1000, armor: 100, melee: Melee.Taser));
    Check(zeusOnVampire.OneHit && zeusOnVampire.Damage >= 4 * 1100, "a 7+ Zeus kills a 1,000-health player");
    Check(!Rules.Hit(H(40, t4, Boost.None, melee: Melee.Knife)).OneHit, "a 4+ knife is a normal knife");
    Check(!Rules.Hit(H(40, t7, Boost.None, melee: Melee.None)).OneHit, "a 7+ gun is not one-hit");

    var saved = Rules.Hit(H(80, Boost.None, t4, health: 50));
    Check(saved.SecondChanceUsed && saved.Damage == 49, "second chance: a lethal hit leaves 1 health");
    var notLethal = Rules.Hit(H(30, Boost.None, t4, health: 50));
    Check(!notLethal.SecondChanceUsed && Math.Abs(notLethal.Damage - 30 * 0.8) < 1e-3, "second chance: not spent on a hit that does not kill");
    Check(!Rules.Hit(H(80, Boost.None, t4, health: 50, left: false)).SecondChanceUsed, "second chance: once per life");
    Check(!Rules.Hit(H(80, Boost.None, t2, health: 50)).SecondChanceUsed, "second chance: not below 4 behind");
    var one = Rules.Hit(H(5, Boost.None, t4, health: 1));
    Check(one.SecondChanceUsed && one.Damage == 0, "second chance at 1 health: the hit does nothing");
    var duel = Rules.Hit(H(40, t7, t7, health: 175, melee: Melee.Knife));
    Check(duel.OneHit && duel.SecondChanceUsed && Math.Abs(duel.Damage - 174) < 1e-3, "a one-hit knife against a second chance: they survive at 1");
    var zeroed = Rules.Hit(H(200, t7, t4, health: 50, zero: true));
    Check(!zeroed.SecondChanceUsed && !zeroed.Block && zeroed.Damage == 200, "headshots only: a body bullet is the game's to zero, no second chance spent");
    Check(Rules.Hit(H(0, t7, t4, melee: Melee.Knife)).Damage == 0, "a hit the game already zeroed stays zero, even a one-hit knife");

    Eq(Rules.MeleeOf("weapon_knife_t", null, false), Melee.Knife, "melee: the damage names a knife");
    Eq(Rules.MeleeOf(null, "weapon_taser", false), Melee.Taser, "melee: a Zeus in hand");
    Eq(Rules.MeleeOf("", "weapon_knifegg", false), Melee.Knife, "melee: the golden knife in hand");
    Eq(Rules.MeleeOf("weapon_ak47", "weapon_knife", false), Melee.None, "melee: the damage names a gun");
    Eq(Rules.MeleeOf("weapon_knife", "weapon_knife", true), Melee.None, "melee: a blast is never a knife");
    Eq(Rules.GameZeroes(true, true, 2), true, "headshots only: a chest bullet is zeroed");
    Eq(Rules.GameZeroes(true, true, 7), true, "headshots only: a leg bullet is zeroed");
    Eq(Rules.GameZeroes(true, true, 1), false, "headshots only: the head is not");
    Eq(Rules.GameZeroes(true, true, 8), false, "headshots only: the neck is not taken as proof");
    Eq(Rules.GameZeroes(true, true, 0), false, "headshots only: an unknown hit group is not proof");
    Eq(Rules.GameZeroes(true, true, 0xFFFFFFFF), false, "headshots only: an invalid hit group is not proof");
    Eq(Rules.GameZeroes(true, false, 2), false, "headshots only: not a bullet");
    Eq(Rules.GameZeroes(false, true, 2), false, "headshots only off");

    Eq(Rules.SpawnHealth(100, t4), 140, "+40% on 100");
    Eq(Rules.SpawnHealth(100, t7), 175, "+75% on 100");
    Eq(Rules.SpawnHealth(1000, t4), 1400, "+40% on a 1,000 base (Vampire)");
    Eq(Rules.SpawnHealth(100, Boost.None), 100, "no boost: the base");
    Eq(Rules.Lifesteal(t4, 50, 100), 10, "20% lifesteal of 50");
    Eq(Rules.Lifesteal(t4, 400, 100), 20, "lifesteal never more than the victim's whole health");
    Eq(Rules.Lifesteal(Boost.None, 50, 100), 0, "no lifesteal without the boost");
    Eq(Rules.Lifesteal(t7, 1000, 1000), 300, "30% lifesteal of a 1,000-health victim");

    double carry = 0;
    Eq(Rules.Regen(t2, 4.9, 0.5, 100, ref carry), 0, "regeneration waits 5 s at 2+");
    var healed = 0;
    for (var i = 0; i < 4; i++) healed += Rules.Regen(t2, 5 + i * 0.5, 0.5, 100, ref carry);
    Eq(healed, 4, "2+: 2% of 100 a second is 4 in 2 s");
    carry = 0;
    healed = 0;
    for (var i = 0; i < 2; i++) healed += Rules.Regen(t7, 3 + i * 0.5, 0.5, 1000, ref carry);
    Eq(healed, 60, "7+: 6% of 1,000 a second is 60 in 1 s");
    carry = 0.7;
    Eq(Rules.Regen(t4, 1, 0.5, 100, ref carry), 0, "hurt again: nothing");
    Near(carry, 0, "hurt again: the fraction is dropped");

    Near(Rules.ProtectedUntil(100, 5, t4), 107, "4+: the game's 5 s and 2 s more");
    Near(Rules.ProtectedUntil(100, 5, t7), 108, "7+: 3 s more");
    Near(Rules.ProtectedUntil(100, 5, t2), 100, "2+: no extra protection");
}

// ---- weapons ----

{
    foreach (var gun in new[] { "g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9", "revolver", "hkp2000",
                                "glock", "elite", "sawedoff", "mag7", "weapon_ak47", "weapon_m4a1_silencer_off", "usp_silencer", "cz75a" })
        Eq(Weapons.Kind(gun), WeaponKind.Gun, $"gun {gun}");
    foreach (var knife in new[] { "knife", "knife_t", "knifegg", "weapon_knife_karambit", "weapon_bayonet", "weapon_knife" })
        Eq(Weapons.Kind(knife), WeaponKind.Knife, $"knife {knife}");
    Eq(Weapons.Kind("taser"), WeaponKind.Taser, "taser");
    Eq(Weapons.Kind("weapon_taser"), WeaponKind.Taser, "weapon_taser");
    foreach (var nade in new[] { "hegrenade", "inferno", "molotov", "hegrenade_projectile", "weapon_flashbang" })
        Eq(Weapons.Kind(nade), WeaponKind.Grenade, $"grenade {nade}");
    foreach (var other in new[] { "", null, "world", "worldspawn", "prop_physics" })
        Eq(Weapons.Kind(other), WeaponKind.Other, $"other {other ?? "null"}");
    Eq(Weapons.Name("WEAPON_M4A1_SILENCER_OFF"), "m4a1_silencer", "one spelling");
}

// ---- the kill counter: the rules one by one (each level its own gun, as on a real ladder) ----

{
    string[] gun = ["g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9"];
    var p = new ProgressConfig();
    var l = new Ladder(p);
    string? GunOf(ulong id) => gun[l.State(id).Level];
    Check(!l.Tracking, "a new counter waits for a match start");
    l.StartMatch(new ulong[] { 1, 2, 3 });
    Check(l.Tracking, "counting after a match start");
    l.Kill(1, 2, GunOf(1), true);
    Eq(l.State(1), (0, 1, true), "one kill on the level");
    l.Kill(1, 2, GunOf(1), true);
    Eq(l.State(1), (1, 0, true), "two kills: the next level");
    Eq(l.Progress(1), 2, "progress counts kills");
    l.Kill(1, 2, "knife_t", true);
    Eq(l.State(1), (2, 0, true), "a knife kill: straight to the next level");
    l.Kill(1, 3, GunOf(1), true);
    l.Kill(3, 1, "knife", true);
    Eq(l.State(1), (1, 0, true), "knifed: down a level, the kill on it gone");
    l.Kill(3, 2, "knife", true);
    Eq(l.State(2), (0, 0, true), "knifed at the bottom: nothing to lose");
    l.Kill(1, 2, GunOf(1), true);
    l.Kill(2, 1, "taser", true);
    Eq(l.State(1), (1, 0, true), "a Zeus death costs one kill");
    l.Kill(2, 1, "taser", true);
    Eq(l.State(1), (0, 1, true), "a Zeus death with none on the level: back a level, one kill short");
    Eq(l.State(2), (1, 0, true), "Zeus kills count for the killer");
    l.Kill(3, 2, "hegrenade", true);
    Eq(l.State(3).Kills, 1, "a grenade kill counts");
    Eq(l.State(1).Trusted && l.State(2).Trusted && l.State(3).Trusted, true, "the rules alone never untrust anybody");

    var last = new Ladder(p);
    last.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 4; i++) last.Kill(1, 2, gun[i / 2], true); // up to level 2
    last.Kill(1, 2, "hegrenade", false);
    Eq(last.State(1), (2, 0, true), "no gun and no kill on the level: the golden knife, a grenade kill does not count");
    last.Kill(1, 2, "taser", false);
    Eq(last.State(1), (2, 0, true), "...nor a Zeus kill");
    last.Kill(1, 2, gun[2], true);
    last.Kill(1, 2, "taser", false);
    Eq(last.State(1), (3, 0, true), "one kill on the level, then no gun: that kill finished the level, it counts");
    last.Kill(1, 2, "knife", false);
    Eq(last.State(1), (4, 0, true), "a knife kill on the knife level: the match-winning kill, one level past");

    var s = new Ladder(p);
    s.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 5; i++) s.Kill(1, 2, gun[i / 2], true);
    Eq(s.State(1), (2, 1, true), "five kills: level 2 and one");
    s.Suicide(1);
    Eq(s.State(1), (1, 0, false), "a suicide: down a level, the kills on it gone, and placed by guns from now");

    // A team kill under friendly fire (the analysis of 2026-11-03: two teammates counted at 38 and 40 of 32 with friendly fire and Instakill on).
    var tk = new Ladder(p);
    tk.StartMatch(new ulong[] { 1, 2, 3 });
    for (var i = 0; i < 6; i++) tk.Kill(1, 3, gun[i / 2], true); // 1 reaches level 3, learning three rungs
    for (var i = 0; i < 5; i++) tk.Kill(2, 3, gun[i / 2], true); // 2 reaches level 2 and one kill
    var tkNote = tk.TeamKill(2, gun[2]);
    Eq(tk.State(2), (1, 0, false), "a team kill: the killer down a level, the kills on it gone, and placed by guns from now");
    Check(tkNote != null && tkNote.Contains("down from level 2 + 1 to level 1"), $"...and the log says so ({tkNote})");
    Eq(tk.State(1), (3, 0, true), "...nobody else moves: the victim keeps their place");
    var back = tk.Kill(2, 3, gun[2], true);
    Check(back != null && back.Contains("placed on level 2") && tk.State(2) == (2, 1, false), $"the killer's next gun kill on a learned rung places them where the game says ({back})");
    var noDemote = new ProgressConfig { TeamKillDemotes = false };
    var kept = new Ladder(noDemote);
    kept.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 3; i++) kept.Kill(1, 2, gun[i / 2], true);
    kept.TeamKill(1, gun[1]);
    Eq(kept.State(1), (1, 1, false), "with TeamKillDemotes off a team kill keeps the count, but still places the killer by their next gun kill");
    Check(new ProgressConfig().TeamKillDemotes, "team kills take the killer down a level by default");

    var g = new Ladder(p);
    g.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 6; i++) g.Kill(1, 2, gun[i / 2], true);
    Eq(g.RungOf("awp"), 1, "a gun kill counted from the start names its rung");
    Eq(g.Rungs, 3, "three rungs learned");
    var note = g.Kill(9, 2, "awp", true); // 9 joined after the start: counted 0
    Check(note != null && note.Contains("placed on level 1"), $"a late joiner's gun kill places them on its rung ({note})");
    Eq(g.State(9), (1, 1, false), "...and the kill counts there");
    Check(g.Kill(9, 2, "awp", true) == null && g.State(9).Level == 2, "...and on from there as normal");
    Eq(g.Kill(9, 2, "ak47", true), null, "a gun nobody counted from the start used yet places nobody");
    g.Untrust(1);
    Eq(g.State(1).Trusted, false, "a team change: placed by guns from now");
    g.Leave(9);
    Eq(g.Progress(9), 0, "gone: forgotten");
    g.Stop();
    Check(!g.Tracking && g.Rungs == 0, "stop: nothing counts until the next match");

    // A gun on two rungs (or a count gone wrong): it stops placing anybody, and the player counted since the start is not moved.
    var twice = new Ladder(p);
    twice.StartMatch(new ulong[] { 1, 2, 3 });
    twice.Kill(1, 2, "ak47", true); // ak47 learned on level 0
    for (var i = 0; i < 4; i++) twice.Kill(3, 2, gun[i / 2], true); // 3 reaches level 2
    var clash = twice.Kill(3, 2, "ak47", true);
    Check(clash != null && clash.Contains("two rungs"), $"a counted player's kill on another rung with a learned gun is noted ({clash})");
    Eq(twice.State(3), (2, 1, false), "...they keep their count, and are placed by guns from now");
    Eq(twice.RungOf("ak47"), -1, "...and that gun is forgotten");
    Eq(twice.Kill(8, 2, "ak47", true), null, "...and places nobody else this match");
    Eq(twice.State(8).Level, 0, "...so a late joiner who uses it stays where counted");
}

// ---- the kill counter against a whole real match ----

{
    var lines = File.ReadAllLines(Path.Combine(here, "armsrace-2026-09-19.txt"));
    var ladder = lines.Where(l => l.StartsWith('#')).SelectMany(l => l.Split(' ')).SkipWhile(w => w != "winner:").Skip(1)
        .Concat(Array.Empty<string>()).ToList();
    var rungs = new[] { "g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9", "revolver", "hkp2000",
                        "glock", "elite", "sawedoff", "mag7", "knife" };
    var kills = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split(' ')).ToList();
    Eq(kills.Count, 119, "the match has 119 kills");
    var knifeRung = rungs.Length - 1;
    foreach (var gunSwappedFirst in new[] { false, true })
    {
        var counter = new Ladder(new ProgressConfig());
        var ids = kills.SelectMany(k => new[] { k[0], k[2] }).Distinct().ToDictionary(n => n, n => (ulong)(n[1] - '0'));
        counter.StartMatch(ids.Values);
        int gunKills = 0, onRung = 0, notes = 0;
        foreach (var k in kills)
        {
            if (k[1] == k[3]) continue; // no team kills in this match, and they would move nobody
            ulong killer = ids[k[0]], victim = ids[k[2]];
            var weapon = k[4];
            var before = counter.State(killer);
            if (Weapons.Kind(weapon) == WeaponKind.Gun)
            {
                gunKills++;
                if (Array.IndexOf(rungs, weapon) == before.Level) onRung++;
                else Console.WriteLine($"      off the rung: {k[0]} {weapon} counted on {rungs[Math.Min(before.Level, knifeRung)]}");
            }
            // Whether the game reports the killer with a gun: never on the knife rung. With gunSwappedFirst, also not when this very
            // kill finished the last gun rung (the game handed the knife out before it reported the kill).
            var finishes = before.Kills == 1 && before.Level == knifeRung - 1 && Weapons.Kind(weapon) != WeaponKind.Knife;
            var hasGun = before.Level < knifeRung && !(gunSwappedFirst && finishes);
            if (counter.Kill(killer, victim, weapon, hasGun) != null) notes++;
        }
        Eq(gunKills, 111, $"the match's gun kills (swap first: {gunSwappedFirst})");
        Eq(onRung, 111, $"every gun kill was on the rung the counter said (swap first: {gunSwappedFirst})");
        Eq(notes, 0, $"the counter never had to place anybody (swap first: {gunSwappedFirst})");
        var final = counter.Standings(ids.Values);
        Eq(final.Top, 2 * (knifeRung + 1), "the winner ends one level past the knife: the match-winning knife kill");
        Eq(final.Leader, ids[kills[^1][0]], "the winner is the last killer");
    }
    Check(ladder.Count >= 0, "the fixture's header names the ladder");
}

// ---- the message ----

{
    var c = Reference();
    var m = c.Message;
    var worstLines = 0;
    var worstWidth = 0;
    var configs = new[] { c, Open() };
    foreach (var cfg in configs)
        for (var top = 2; top <= 60; top++)
            for (var lead = 0; lead <= 8; lead++)
                for (var mine = 0; mine <= top - 2; mine++)
                {
                    var b = Rules.ForGap(cfg, top - mine, top, mine, Rules.BountyFor(cfg.Bounty, lead));
                    var ls = Words.Lines(b, m);
                    if (!b.IsBoost) { Eq(ls.Length, 0, "no boost: no message"); continue; }
                    worstLines = Math.Max(worstLines, ls.Length);
                    worstWidth = Math.Max(worstWidth, ls.Max(x => x.Length));
                    Check(ls.Length <= m.MaxLines && ls.All(x => x.Length <= m.LineWidth),
                        $"{top - mine} behind (top {top}): fits {m.MaxLines} lines of {m.LineWidth}: {string.Join(" | ", ls)}");
                    Check(ls[0].StartsWith($"{top - mine} kill"), $"{top - mine} behind: says how many kills behind first");
                }
    Check(worstLines <= 5 && worstWidth <= 28, $"the tallest message {worstLines} lines, the widest line {worstWidth}");
    for (var lead = 2; lead <= 30; lead++)
    {
        var ls = Words.Lines(new Boost { Lead = lead, Bounty = Rules.BountyFor(c.Bounty, lead) }, m);
        Check(ls.Length is >= 1 and <= 5 && ls.All(x => x.Length <= 28) && ls[0] == $"You lead by {lead} kills:", $"the leader's message at a lead of {lead}");
    }

    var five = Words.Lines(Rules.ForGap(Open(), 5, 5, 0, 0), m);
    Eq(string.Join(" | ", five), "5 kills behind the leader: | +40% HP, -20% dmg taken, | +20% dmg, 20% lifesteal, | regen, 2nd chance, | +2s protection",
        "5 behind, uncapped: the panel");
    var two = Words.Lines(Rules.ForGap(Open(), 2, 2, 0, 0), m);
    Eq(string.Join(" | ", two), "2 kills behind the leader: | +20% health, | -10% damage taken, | +10% damage, 10% lifesteal, | regeneration",
        "2 behind, uncapped: the panel in long words");
    var seven = Words.Lines(Rules.ForGap(Open(), 7, 7, 0, 0), m);
    Eq(string.Join(" | ", seven), "7 kills behind: +75% HP, | -30% dmg taken, +30% dmg, | 30% lifesteal, regen, | 2nd chance, +3s protection, | no reloads, 1-hit knife/zeus",
        "7 behind, uncapped: the panel, short words, the reason inline");
    Eq(string.Join(" | ", Words.Lines(new Boost { Lead = 4, Bounty = 0.2 }, m)), "You lead by 4 kills: | +20% damage taken (a bounty)", "the leader's panel");

    var html = Words.Html(five, Rules.ForGap(Open(), 5, 5, 0, 0));
    Check(html.StartsWith("<font color='#FFD700'><b>5 kills behind the leader:</b></font><br>"), "the reason gold and bold");
    Eq(html.Split("<br>").Length, five.Length, "one <br> between lines");
    var inline = Words.Html(seven, Rules.ForGap(Open(), 7, 7, 0, 0));
    Check(inline.StartsWith("<font color='#FFD700'><b>7 kills behind:</b></font> +75% HP,"), "an inline reason is gold and bold too");
    var leader = Words.Html(Words.Lines(new Boost { Lead = 6, Bounty = 0.3 }, m), new Boost { Lead = 6, Bounty = 0.3 });
    Check(leader.StartsWith("<font color='#FF6347'><b>You lead by 6 kills:</b></font>"), "the leader's message in red");
    Check(!html.Contains('&') && !html.Contains("<script"), "nothing in it to escape");
}

// ---- when the message comes ----

{
    var m = Defaults().Message;
    Near(Rules.MessageDelay(m, false, null), 0.25, "a spawn: right after the boost");
    Near(Rules.MessageDelay(m, true, null), 3.0, "the first spawn after joining: after the join tagline (3 s)");
    Near(Rules.MessageDelay(m, true, 2.5), 2.75, "the host said its tagline ends in 2.5 s: just after");
    Near(Rules.MessageDelay(m, true, 6), 6.25, "a long tagline: after it");
    Near(Rules.MessageDelay(m, false, -40), 0.25, "a tagline long gone: no wait");
    Near(Rules.MessageDelay(m, true, -1), 0.25, "the host's tagline already over at the first spawn: no wait");
}

// ---- the table and the cap, as built (printed with --table) ----

if (printTable)
{
    var c = Defaults();
    Console.WriteLine("Tier table (catchup.json):");
    Console.WriteLine("  behind | health | dmg taken | dmg dealt | lifesteal | regen        | 2nd chance | +protection | inf. ammo | 1-hit knife/zeus | duel factor");
    foreach (var t in c.Tiers)
        Console.WriteLine($"  {t.MinBehind,4}+  | +{t.Health * 100,3:0}%  | {t.DamageTaken * 100,5:0}%    | +{t.DamageDealt * 100,3:0}%     | {t.Lifesteal * 100,3:0}%      | {t.RegenPerSecond * 100:0}%/s after {t.RegenDelaySeconds:0}s | {(t.SecondChance ? "yes" : "no"),-10} | {t.ExtraSpawnProtectionSeconds,2:0} s        | {(t.InfiniteAmmo ? "yes" : "no"),-9} | {(t.OneHitKnifeAndTaser ? "yes" : "no"),-16} | {Rules.DuelFactor(t.Health, t.Lifesteal, t.DamageDealt, t.DamageTaken, 0, true):0.00}");
    Console.WriteLine("Bounty: lead 0-1 none, " + string.Join(", ", new[] { 2, 4, 6 }.Select(l => $"{l}+ +{Rules.BountyFor(c.Bounty, l) * 100:0}%")));
    Console.WriteLine("The cap at work (health / damage taken / damage dealt / lifesteal given; the leader's lead over second place in brackets):");
    foreach (var (top, mine, lead) in new[] { (4, 0, 2), (4, 2, 2), (8, 1, 2), (8, 4, 4), (12, 5, 2), (16, 8, 3), (16, 11, 3), (16, 13, 2), (24, 14, 4), (30, 23, 6), (33, 20, 2) })
    {
        var b = Rules.ForGap(c, top - mine, top, mine, Rules.BountyFor(c.Bounty, lead));
        Console.WriteLine($"  leader {top,2}, player {mine,2} ({top - mine,2} behind, lead {lead}, bounty {Rules.BountyFor(c.Bounty, lead) * 100:0}%): allowed {b.Allowed:0.00}, raw {b.RawFactor:0.00} -> "
                          + $"+{b.Health * 100:0}% / {b.DamageTaken * 100:0}% / +{b.DamageDealt * 100:0}% / {b.Lifesteal * 100:0}% = {b.Factor:0.00}{(b.Capped ? " capped" : "")}   \"{string.Join(" | ", Words.Lines(b, c.Message))}\"");
    }
}

Console.WriteLine(failures == 0 ? $"CatchUp self-test: all {checks} checks passed" : $"CatchUp self-test: {failures} of {checks} checks FAILED");
return failures == 0 ? 0 : 1;

static CatchUpConfig Open()
{
    var c = Reference();
    c.Balance.ScoreboardCap = false;
    c.Balance.MaxDuelFactor = 100;
    return c;
}
