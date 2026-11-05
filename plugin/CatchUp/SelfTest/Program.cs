using System.Globalization;
using CatchUp;

// CatchUp.SelfTest - every decision of the catch-up module, without a game server: the config and its file, the tiers and the
// bounty, the balance cap, each hit, the healing, the Arms Race kill counter (its rules case by case, nine real players' runs, and five
// whole real matches replayed), the boost decided again in the middle of a life, the message and its timing. CatchUpConfig.cs and
// CatchUpRules.cs are compiled in as source; CatchUpModule.cs (the game half) is not, and is held to these by the compile check
// (../CatchUp.CompileCheck.csproj).
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

// A fixed set of numbers, not the defaults. The mechanics below - the bounty steps, the cap,
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
    (c.Bounty.Max, c.Balance.MaxDuelFactor, c.Balance.ScoreboardCap, c.Balance.ScoreboardCapFactor) = (0.30, 3.0, true, 1.0);
    c.Normalize(new List<string>());
    return c;
}

// Three tiers, the scoreboard cap on at twice its ratio and
// MaxDuelFactor 3.5. The cap binds there, so its rules for the leader's teammates and enemies are checked on these.
static CatchUpConfig ThreeTiers()
{
    var c = new CatchUpConfig
    {
        Tiers =
        [
            new() { MinBehind = 2, Health = 0.30, DamageTaken = -0.15, DamageDealt = 0.15, Lifesteal = 0.15, RegenPerSecond = 0.03, RegenDelaySeconds = 4 },
            new() { MinBehind = 4, Health = 0.60, DamageTaken = -0.25, DamageDealt = 0.25, Lifesteal = 0.25, RegenPerSecond = 0.05, RegenDelaySeconds = 3,
                    SecondChance = true, ExtraSpawnProtectionSeconds = 2 },
            new() { MinBehind = 7, Health = 1.00, DamageTaken = -0.35, DamageDealt = 0.40, Lifesteal = 0.35, RegenPerSecond = 0.08, RegenDelaySeconds = 2,
                    SecondChance = true, ExtraSpawnProtectionSeconds = 3, InfiniteAmmo = true, OneHitKnifeAndTaser = true },
        ],
        RecalculateOnKill = false,
    };
    (c.Balance.MaxDuelFactor, c.Balance.ScoreboardCap, c.Balance.ScoreboardCapFactor) = (3.5, true, 2.0);
    c.Normalize(new List<string>());
    return c;
}

Standings Stand(params (ulong Id, int Progress)[] players) => new(players.ToDictionary(p => p.Id, p => p.Progress));

// The kill counter's place for a player, as the rules write it: "L+K@H" - level L with K kills on it, holding the gun of rung H - and
// " placed" once the counter no longer follows them from the match's start.
string Place(Ladder l, ulong id)
{
    var (level, kills, trusted) = l.State(id);
    return $"{level}+{kills}@{l.Held(id)}{(trusted ? "" : " placed")}";
}

// One player's run on a fresh counter: X the player, counted from the match's start; E an enemy and M a teammate, who both joined after it,
// so their own kills teach the counter nothing. X first climbs to the start with the ladder's guns. Each step is "event=X's place after it":
//   gun:<gun>        X kills E with that gun, first checked against the rung of the gun the counter has in X's hands
//   he, zeus, knife  X kills E with an HE grenade, a Zeus, the knife
//   tk:<weapon>      X kills M, a team kill (a gun checked like an enemy kill)
//   killed:<weapon>  E kills X: a knife or a taser is the knife or Zeus death, any other weapon only a respawn
//   teamkilled       M kills X
//   world            X dies without an enemy
//   win              X's knife kill on the golden knife: the match's last
const int GoldenKnife = 16;
void Run(string what, string[] ladder, string start, params string[] steps)
{
    const ulong X = 1, E = 2, M = 3;
    var l = new Ladder(new ProgressConfig());
    l.StartMatch([X]);
    var at = start.Split('+').Select(int.Parse).ToArray();
    for (var i = 0; i < at[0] * 2 + at[1]; i++) l.Kill(X, E, ladder[l.Held(X)], true);
    Eq(Place(l, X), $"{start}@{at[0]}", $"{what}: the start");
    var n = 0;
    foreach (var step in steps)
    {
        n++;
        var parts = step.Split('=');
        var ev = parts[0];
        var after = parts.Length > 1 ? parts[1] : null;
        var verb = ev.Split(':')[0];
        var weapon = ev.Contains(':') ? ev[(ev.IndexOf(':') + 1)..] : "";
        var hasGun = l.Held(X) < GoldenKnife;
        var label = $"{what}, step {n} ({ev})";
        switch (verb)
        {
            case "gun":
                Eq(l.Held(X), Array.IndexOf(ladder, weapon), $"{label}: the {weapon}'s rung is the one the counter has in their hands");
                l.Kill(X, E, weapon, hasGun);
                break;
            case "he": l.Kill(X, E, "hegrenade", hasGun); break;
            case "zeus": l.Kill(X, E, "taser", hasGun); break;
            case "knife": l.Kill(X, E, "knife", hasGun); break;
            case "tk":
                if (Weapons.Kind(weapon) == WeaponKind.Gun) Eq(l.Held(X), Array.IndexOf(ladder, weapon), $"{label}: the {weapon}'s rung is the one in their hands");
                l.TeamKill(X, M, weapon);
                break;
            case "killed": l.Kill(E, X, weapon, true); break;
            case "teamkilled": l.TeamKill(M, X, "knife"); break;
            case "world": l.Suicide(X); break;
            case "win":
                Check(l.State(X).Level == GoldenKnife && !hasGun, $"{label}: made on the golden knife ({Place(l, X)})");
                l.Kill(X, E, "knife", false);
                after ??= $"{GoldenKnife + 1}+0@{GoldenKnife + 1}"; // the counter's own record of the win: one level past the knife
                break;
            default: Check(false, $"{label}: no such step"); break;
        }
        if (after != null) Eq(Place(l, X), after, label);
    }
}

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
    Eq(d.Tiers.Count, 7, "seven tiers by default (curve B): one per gap from 2 to 8+");
    Eq(string.Join(",", d.Tiers.Select(t => t.MinBehind)), "2,3,4,5,6,7,8", "tiers from 2, 3, 4, 5, 6, 7 and 8 behind");
    Check(d.RecalculateOnKill, "the boost is decided again after every kill by default");

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

    var loose = CatchUpConfig.Parse("{ // comments and trailing commas are allowed\n \"enabled\": false, \"recalculateonkill\": false, \"bounty\": { \"max\": 0.5, }, }");
    loose.Normalize(new List<string>());
    Eq(loose.Enabled, false, "property names in any case");
    Eq(loose.RecalculateOnKill, false, "RecalculateOnKill switches off: decided at spawn only");
    Near(loose.Bounty.Max, 0.5, "a nested value read");
    Eq(loose.Tiers.Count, 7, "a missing section keeps its defaults");

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
    Eq(nulls.Tiers.Count, 7, "null tiers: the defaults");
    Eq(nulls.Bounty.MinLead, 2, "a null section: its defaults");
}

// ---- tiers and bounty ----

{
    var c = Reference();
    foreach (var (behind, tier) in new[] { (0, 0), (1, 0), (2, 2), (3, 2), (4, 4), (5, 4), (6, 4), (7, 7), (8, 7), (30, 7) })
        Eq(Rules.TierFor(c.Tiers, behind)?.MinBehind ?? 0, tier, $"{behind} behind: tier");
    foreach (var (lead, bounty) in new[] { (0, 0.0), (1, 0.0), (2, 0.1), (3, 0.1), (4, 0.2), (5, 0.2), (6, 0.3), (7, 0.3), (20, 0.3) })
        Near(Rules.BountyFor(c.Bounty, lead), bounty, $"lead {lead}: bounty");
    foreach (var (behind, tier) in new[] { (0, 0), (1, 0), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7), (8, 8), (9, 8), (30, 8) })
        Eq(Rules.TierFor(Defaults().Tiers, behind)?.MinBehind ?? 0, tier, $"{behind} behind: curve B's tier");

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
    // A whole sentence, word for word, on these numbers.
    Eq(Words.Sentence(Rules.ForGap(open, 5, 5, 0, 0)),
        "You are 5 kills behind the leader: +40% health, -20% damage taken, +20% damage, 20% lifesteal, regeneration, a second chance, +2 s spawn protection.",
        "the whole sentence");
    // ...and on the defaults: curve B's 5+ tier, which the cap never touches
    Eq(Words.Sentence(Rules.ForGap(Defaults(), 5, 5, 0, 0)),
        "You are 5 kills behind the leader: +50% health, -25% damage taken, +20% damage, 10% lifesteal, regeneration, a second chance, +2 s spawn protection.",
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
    // Worked examples from a real match's log, on the two sets of numbers: the cap's result on each.
    var threeTiers = ThreeTiers();
    Near(Rules.Allowed(c.Balance, 13, 3), 3.0, "3 vs leader 13: allowed 3.0 before (MaxDuelFactor 3.0, under the ratio 3.5)");
    Near(Rules.Allowed(threeTiers.Balance, 13, 3), 3.5, "...and min(3.5, 2.0 x 14/4) = 3.5 on the three tiers");
    Near(Rules.Allowed(c.Balance, 14, 7), 1.875, "7 vs 14: allowed 1.875 before (the ratio 15/8)");
    Near(Rules.Allowed(threeTiers.Balance, 14, 7), 3.5, "...and min(3.5, 2.0 x 15/8) = 3.5 then");
    Near(Rules.Allowed(threeTiers.Balance, 30, 23), 2.0 * 31.0 / 24.0, "a late 23 vs 30: 2.0 x 31/24 = 2.58");
    Near(Rules.BountyFor(threeTiers.Bounty, 8), 0.30, "a lead of 8: the bounty stays at most 30%");
    // The leader's bounty and his own teammates: a teammate's cap is computed without it, an enemy's counts it first.
    var standings = Stand((1, 20), (2, 12), (3, 10), (4, 8)); // 1 leads by 8: a 30% bounty; 3 is 10 behind (tier 7+)
    var asEnemy = Rules.Decide(threeTiers, standings, 3);
    var asMate = Rules.Decide(threeTiers, standings, 3, teammateOfLeader: true);
    Near(asEnemy.Allowed, 3.5, "10 vs 20: allowed min(3.5, 2.0 x 21/11) = 3.5, the same for the leader's enemy and teammate");
    Near(asMate.Allowed, 3.5, "...");
    Check(asEnemy.Capped && asMate.Capped && asMate.Health > asEnemy.Health && asMate.DamageDealt >= asEnemy.DamageDealt,
        $"...the enemy's own boosts share 3.5 / 1.3 (the bounty first), the teammate's the whole 3.5: health +{asEnemy.Health:0.00} for the enemy, +{asMate.Health:0.00} for the teammate");
    var mateOwn = Rules.DuelFactor(asMate.Health, asMate.Lifesteal, asMate.DamageDealt, asMate.DamageTaken, 0, true);
    var enemyOwn = Rules.DuelFactor(asEnemy.Health, asEnemy.Lifesteal, asEnemy.DamageDealt, asEnemy.DamageTaken, 0, true);
    Check(mateOwn <= 3.5 + 1e-9 && enemyOwn <= 3.5 / 1.3 + 1e-9 && Math.Abs(asMate.Factor - mateOwn) < 1e-9 && asEnemy.Factor <= 3.5 + 1e-9,
        $"...the teammate's own factor {mateOwn:0.00} within 3.5, the enemy's {enemyOwn:0.00} within 3.5 / 1.3 and {asEnemy.Factor:0.00} with the bounty");
    var tight = Stand((1, 9), (2, 3), (3, 7)); // 1 leads by 2 over 3: a 10% bounty; 2 is 6 behind
    Check(Rules.Decide(threeTiers, tight, 2, teammateOfLeader: true).Health >= Rules.Decide(threeTiers, tight, 2).Health, "a teammate never gets less than an enemy in the same place");
    Check(Rules.Decide(threeTiers, standings, 1, teammateOfLeader: true) is { IsBounty: true, Bounty: 0.30 }, "the leader's own bounty does not depend on the flag");
    var offCap = ThreeTiers();
    offCap.Balance.ScoreboardCap = false;
    Near(Rules.Allowed(offCap.Balance, 14, 7), 3.5, "the scoreboard cap switches off: MaxDuelFactor alone");
    var worstThreeTiers = 0.0;
    for (var top = 2; top <= 40; top++)
        for (var lead = 0; lead <= 10; lead++)
            for (var mine = 0; mine <= top - 2; mine++)
                worstThreeTiers = Math.Max(worstThreeTiers, Rules.ForGap(threeTiers, top - mine, top, mine, Rules.BountyFor(threeTiers.Bounty, lead)).Factor);
    Check(worstThreeTiers <= 3.5 + 1e-9, $"on the three tiers the duel factor never goes above 3.5 (worst {worstThreeTiers:0.00})");

    // Curve B, the defaults: seven tiers, no scoreboard cap, MaxDuelFactor 8.
    var d = Defaults();
    Check(d.Bounty is { MinLead: 2, StepKills: 2, PerStep: 0.10, Max: 0.30 } && d.Balance is { MaxDuelFactor: 8.0, ScoreboardCap: false, LifestealCounts: true, RoundTo: 0.05 },
        "the defaults: the bounty (+10% per 2 kills of lead from 2, up to 30%), no scoreboard cap, MaxDuelFactor 8");
    Check(d.Tiers.Select(t => (t.MinBehind, t.Health, t.DamageTaken, t.DamageDealt, t.Lifesteal)).SequenceEqual(
        [(2, 0.05, 0.00, 0.00, 0.05), (3, 0.15, -0.10, 0.05, 0.05), (4, 0.35, -0.15, 0.15, 0.05), (5, 0.50, -0.25, 0.20, 0.10),
         (6, 0.75, -0.30, 0.25, 0.20), (7, 0.95, -0.35, 0.35, 0.25), (8, 1.20, -0.40, 0.40, 0.35)]),
        "curve B's health, damage taken, damage dealt and lifesteal for 2, 3, 4, 5, 6, 7 and 8+ behind");
    var ownB = d.Tiers.Select(t => Math.Round(Rules.Own(t.Health, t.Lifesteal, t.DamageDealt, t.DamageTaken, true), 2)).ToList();
    Eq(string.Join(" ", ownB.Select(x => x.ToString("0.00"))), "1.10 1.40 1.89 2.56 3.48 4.57 5.95", "...their own factors, the steep quadratic 1 + 0.10 (gap - 1)^2 in 5% steps");
    Check(d.Tiers.Select(t => (t.RegenPerSecond, t.RegenDelaySeconds)).SequenceEqual([(0.03, 4.0), (0.03, 4.0), (0.05, 3.0), (0.05, 3.0), (0.05, 3.0), (0.08, 2.0), (0.08, 2.0)]),
        "...regeneration 3%/s after 4 s at 2-3 behind, 5%/s after 3 s at 4-6, 8%/s after 2 s from 7");
    Check(d.Tiers.Select(t => (t.SecondChance, t.ExtraSpawnProtectionSeconds, t.InfiniteAmmo, t.OneHitKnifeAndTaser)).SequenceEqual(
        [(false, 0.0, false, false), (false, 0.0, false, false), (true, 2.0, false, false), (true, 2.0, false, false), (true, 2.0, false, false),
         (true, 3.0, true, true), (true, 3.0, true, true)]),
        "...a second chance and 2 s more spawn protection from 4 behind; 3 s more, infinite ammo and the one-hit knife and Zeus from 7");
    var capped = 0;
    var notTable = 0;
    var worstB = 0.0;
    for (var top = 2; top <= 60; top++)
        for (var lead = 0; lead <= 12; lead++)
            for (var mine = 0; mine <= top - 2; mine++)
                foreach (var mate in new[] { false, true })
                {
                    var bounty = mate ? 0 : Rules.BountyFor(d.Bounty, lead);
                    var b = Rules.ForGap(d, top - mine, top, mine, bounty);
                    var t = Rules.TierFor(d.Tiers, top - mine)!;
                    if (b.Capped) capped++;
                    if (b.Health != t.Health || b.DamageTaken != t.DamageTaken || b.DamageDealt != t.DamageDealt || b.Lifesteal != t.Lifesteal
                        || b.BountyAllowed < 1 + bounty - 1e-9) notTable++;
                    worstB = Math.Max(worstB, b.Factor);
                }
    Eq(capped, 0, "with curve B the cap never binds, as the leader's enemy or his teammate");
    Eq(notTable, 0, "...so every tier gives exactly its row (no 'up to'), and the leader's whole bounty on every hit on him");
    Check(Math.Abs(worstB - 5.95 * 1.3) < 0.01 && worstB < 8, $"...the most against the leader is 5.95 x the 30% bounty = 7.74 (worst {worstB:0.00}), under MaxDuelFactor 8");
    Near(Rules.Allowed(d.Balance, 30, 0), 8, "allowed with curve B: 8, whatever the scoreboard");

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
    Eq(Rules.AfterMaximumCut(1000, 1400, 100), (100, 100), "Vampire off mid-life: the life's base and ceiling follow the pawn's 100, so nothing heals past it");
    Eq(Rules.AfterMaximumCut(100, 140, 140), (100, 140), "a pawn at the life's own ceiling changes nothing");
    Eq(Rules.AfterMaximumCut(100, 140, 2000), (100, 140), "nor does a higher maximum: Vampire on mid-life, a mode's own");
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

// ---- the kill counter: CS2's rules case by case (a ladder of 16 guns, then the golden knife on level 16) ----

{
    string[] gun = ["g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9", "revolver", "hkp2000", "glock", "elite", "sawedoff", "mag7"];
    Run("T1 two gun kills", gun, "0+0", "gun:g3sg1=0+1@0", "gun:g3sg1=1+0@1");
    Run("T2 an HE kill finishes a level", gun, "3+1", "he=4+0@4");
    Run("T2 ...and a Zeus kill", gun, "3+1", "zeus=4+0@4");
    Run("T3a a knife kill: up a level, the kill on it kept", gun, "5+1", "knife=6+1@6");
    Run("T3b ...with none on it", gun, "5+0", "knife=6+0@6");
    Run("T4a knifed: down a level, the kill on it kept", gun, "7+1", "killed:knife=6+1@6");
    Run("T4b knifed at level 0: the kill on it goes", gun, "0+1", "killed:knife=0+0@0");
    Run("T5a a Zeus death: a kill back", gun, "7+1", "killed:taser=7+0@7");
    Run("T5b ...from 0 kills, down a level with one", gun, "7+0", "killed:taser=6+1@6");
    Run("T5c ...at 0 + 0 nothing", gun, "0+0", "killed:taser=0+0@0");
    Run("T6a/b a team kill: down a level, the kill kept, the old gun in hand; a kill with it counts on the lower level", gun, "9+1",
        "tk:mp9=8+1@9", "gun:mp9=9+0@9");
    Run("T6c ...until he dies: the respawn gives the lower level's gun", gun, "9+1", "tk:mp9=8+1@9", "killed:ak47=8+1@8", "gun:mp7=9+0@9");
    Run("T6d a team kill with none on the level, then a kill with the old gun", gun, "9+0", "tk:mp9=8+0@9", "gun:mp9=8+1@9");
    Run("T6e a team kill at level 0: the kill on it goes", gun, "0+1", "tk:g3sg1=0+0@0");
    Run("T6f the victim of a team kill loses nothing, and respawns with the level's gun", gun, "9+1", "tk:mp9=8+1@9", "teamkilled=8+1@8");
    Run("T7 a death without an enemy at level 0: the kill on it goes, still followed", gun, "0+1", "world=0+0@0");
    Run("T7 ...above level 0 (never seen in CS2): CS:GO's rule, and placed by the next gun kill", gun, "3+1", "world=2+0@2 placed");
    Run("T8a/b the last gun level done: the golden knife; a knife kill there wins", gun, "15+1", "gun:mag7=16+0@16", "win");
    Run("T8c knifed on the golden knife: down to the last gun", gun, "16+0", "killed:knife=15+0@15");
    Run("T8d team-killed on the golden knife: nothing", gun, "16+0", "teamkilled=16+0@16");
    Run("T8 ...an HE or Zeus kill on the golden knife counts for nothing", gun, "16+0", "he=16+0@16", "zeus=16+0@16");
    Run("T8 ...an HE kill that finishes the last gun level counts", gun, "15+1", "he=16+0@16");
    var swapped = new Ladder(new ProgressConfig());
    swapped.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 31; i++) swapped.Kill(1, 2, gun[swapped.Held(1)], true);
    swapped.Kill(1, 2, "hegrenade", false);
    Eq(Place(swapped, 1), "16+0@16", "T8 ...even when the game hands out the golden knife before it reports that kill (no gun, one kill on the level)");

    // T9: a player who joins during a match starts at level 0 with the level-0 gun; their kills place nobody while that holds
    var j = new Ladder(new ProgressConfig());
    j.StartMatch(new ulong[] { 1, 2 });
    j.Kill(1, 2, gun[0], true);
    Eq(Place(j, 9), "0+0@0 placed", "T9 joined mid-match: level 0, the level-0 gun, placed by guns");
    Eq(j.Kill(9, 2, gun[0], true), null, "T9 ...their first kill, with the level-0 gun, moves nobody");
    Eq(Place(j, 9), "0+1@0 placed", "T9 ...and counts");
    Eq(j.Notes, 0, "T9 ...no placement");

    // The counter's own mechanics, besides the game's rules.
    var p = new ProgressConfig();
    var l = new Ladder(p);
    Check(!l.Tracking, "a new counter waits for a match start");
    l.StartMatch(new ulong[] { 1, 2, 3 });
    Check(l.Tracking, "counting after a match start");
    for (var i = 0; i < 4; i++) l.Kill(1, 3, gun[l.Held(1)], true);
    l.Kill(1, 3, "hegrenade", true);
    Eq(l.Progress(1), 5, "progress counts kills: level x 2 + the kills on it");
    Eq(l.RungOf("aug"), -1, "the level-2 gun not used yet");
    var tkLine = l.TeamKill(1, 2, gun[l.Held(1)]);
    Eq(tkLine, ("team-killed with aug: down from level 2 + 1 to level 1 + 1, the level-2 gun in hand until death", (string?)null), "the team kill's log line, and no note");
    Eq(l.Progress(1), 3, "...a level of progress lost");
    Eq(Place(l, 1), "1+1@2", "...the killer still followed, holding the old gun");
    Eq(l.RungOf("aug"), 2, "...and the team kill's gun names the rung in hand, as an enemy kill's does");
    Eq(Place(l, 2), "0+0@0", "...the victim keeps their place");
    l.Kill(3, 1, gun[0], true);
    Eq(Place(l, 1), "1+1@1", "the team-killer dies: the level's gun again");
    Eq(l.Notes, 0, "the rules alone never place or untrust anybody counted from the start");
    var first = new Ladder(p);
    first.StartMatch(new ulong[] { 1, 2, 3 });
    for (var i = 0; i < 4; i++) first.Kill(1, 2, "hegrenade", true); // level 2 with no gun used yet
    first.TeamKill(1, 3, "hegrenade");
    first.Kill(1, 2, gun[2], true);
    Eq(first.RungOf(gun[2]), 2, "a gun first used after a team kill is learned on the rung in hand, not on the lower level");
    Eq(Place(first, 1), "1+1@2", "...and the kill counts on the lower level");
    var noDemote = new Ladder(new ProgressConfig { TeamKillDemotes = false });
    noDemote.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 3; i++) noDemote.Kill(1, 2, gun[noDemote.Held(1)], true);
    Eq(noDemote.TeamKill(1, 2, gun[1]).Line, "team-killed with awp: the count kept", "TeamKillDemotes off: the count kept");
    Eq(Place(noDemote, 1), "1+1@1", "...and still followed");
    Check(new ProgressConfig().TeamKillDemotes, "team kills take the killer down a level by default");

    var g = new Ladder(p);
    g.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 6; i++) g.Kill(1, 2, gun[g.Held(1)], true);
    Eq(g.RungOf("awp"), 1, "a gun kill counted from the start names its rung");
    Eq(g.Rungs, 3, "three rungs learned");
    var note = g.Kill(9, 2, "awp", true); // 9 joined after the start: counted 0
    Check(note != null && note.Contains("placed on level 1"), $"a late joiner's gun kill on a known rung that is not theirs places them on it ({note})");
    Eq(Place(g, 9), "1+1@1 placed", "...and the kill counts there");
    Check(g.Kill(9, 2, "awp", true) == null && g.State(9).Level == 2, "...and on from there as normal");
    Eq(g.Kill(9, 2, "ak47", true), null, "a gun nobody counted from the start used yet places nobody");
    Eq(g.Notes, 1, "one placement noted");
    var tkSame = g.TeamKill(9, 1, "aug");
    Check(tkSame.Line.StartsWith("team-killed with aug") && tkSame.Note == null, $"a team kill's gun on the rung in hand places nobody ({tkSame})");
    var tkPlaced = g.TeamKill(7, 1, "awp"); // 7 joined late, counted 0, holds the awp of rung 1
    Eq(tkPlaced, ("team-killed with awp: down from level 1 + 0 to level 0 + 0, the level-1 gun in hand until death", "placed on level 1 by a awp kill (counted 0)"),
        "a late joiner's team kill with a known gun places them, then takes them down - the note apart from the line, as the log writes them");
    Eq(Place(g, 7), "0+0@1 placed", "...holding that gun until death");
    g.Untrust(1);
    Eq(g.State(1).Trusted, false, "a team change: placed by guns from now");
    g.Leave(9);
    Eq(g.Progress(9), 0, "gone: forgotten");
    g.Stop();
    Check(!g.Tracking && g.Rungs == 0 && g.Notes == 0, "stop: nothing counts until the next match");

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

// ---- the kill counter: nine real players' runs (the sequences that prove each rule, from the matches below) ----

{
    string[] combat = ["g3sg1", "awp", "aug", "famas", "m4a1_silencer", "ak47", "m249", "ump45", "bizon", "mac10", "ssg08", "usp_silencer", "hkp2000", "fiveseven", "mag7", "nova"];
    string[] vince = ["g3sg1", "awp", "sg556", "ak47"];
    string[] moon = ["g3sg1", "awp", "m4a1_silencer", "m4a1", "ak47", "famas", "m249", "mp9", "mp5sd", "ump45", "ssg08", "tec9", "glock", "fiveseven", "xm1014", "nova"];
    string[] stalker = ["scar20", "awp", "aug", "galilar", "sg556", "m4a1_silencer", "negev", "mp7", "mac10", "p90", "revolver", "tec9", "hkp2000", "fiveseven", "nova", "mag7"];
    Run("R1 a team kill keeps the kill, and the respawn gives the lower gun (gg_ar_combat_zone)", combat, "1+1",
        "killed:taser=1+0@1", "gun:awp=1+1@1", "killed:awp=1+1@1", "tk:awp=0+1@1", "killed:aug=0+1@0", "killed:famas=0+1@0", "gun:g3sg1=1+0@1",
        "gun:awp=1+1@1", "killed:m4a1_silencer=1+1@1", "gun:awp=2+0@2", "gun:aug=2+1@2");
    Run("R2 after a team kill the old gun stays in hand, and its kills count on the lower level (gg_moonglass)", moon, "5+1",
        "gun:famas=6+0@6", "killed:g3sg1=6+0@6", "tk:m249=5+0@6", "gun:m249=5+1@6", "gun:m249=6+0@6", "gun:m249=6+1@6", "killed:awp=6+1@6",
        "killed:awp=6+1@6", "killed:m4a1_silencer=6+1@6", "gun:m249=7+0@7", "tk:mp9=6+0@7", "killed:m4a1_silencer=6+0@6", "killed:awp=6+0@6",
        "killed:m4a1_silencer=6+0@6", "gun:m249=6+1@6");
    Run("R3 a knife kill keeps the kill on the level (gg_vinceville)", vince, "0+0",
        "gun:g3sg1=0+1@0", "knife=1+1@1", "killed:g3sg1=1+1@1", "gun:awp=2+0@2", "killed:g3sg1=2+0@2", "gun:sg556=2+1@2", "gun:sg556=3+0@3");
    Run("R4 a knife death keeps the kill on the level (gg_moonglass)", moon, "1+0",
        "killed:m4a1=1+0@1", "gun:awp=1+1@1", "killed:knife=0+1@0", "killed:m4a1_silencer=0+1@0", "killed:famas=0+1@0", "gun:g3sg1=1+0@1",
        "killed:m4a1=1+0@1", "killed:m249=1+0@1", "killed:m249=1+0@1", "gun:awp=1+1@1", "killed:knife=0+1@0", "gun:g3sg1=1+0@1", "zeus=1+1@1",
        "gun:awp=2+0@2");
    Run("R5 Zeus deaths, a knife kill, the golden knife and the win (gg_ar_combat_zone)", combat, "12+0",
        "killed:ump45=12+0@12", "killed:taser=11+1@11", "killed:usp_silencer=11+1@11", "killed:bizon=11+1@11", "killed:mac10=11+1@11",
        "killed:mac10=11+1@11", "knife=12+1@12", "teamkilled=12+1@12", "gun:hkp2000=13+0@13", "teamkilled=13+0@13", "killed:mac10=13+0@13",
        "killed:ssg08=13+0@13", "killed:taser=12+1@12", "killed:usp_silencer=12+1@12", "zeus=13+0@13", "gun:fiveseven=13+1@13",
        "killed:hkp2000=13+1@13", "gun:fiveseven=14+0@14", "gun:mag7=14+1@14", "killed:fiveseven=14+1@14", "gun:mag7=15+0@15",
        "killed:mag7=15+0@15", "killed:usp_silencer=15+0@15", "killed:hkp2000=15+0@15", "knife=16+0@16", "killed:nova=16+0@16",
        "killed:fiveseven=16+0@16", "killed:mag7=16+0@16", "killed:nova=16+0@16", "killed:nova=16+0@16", "win");
    Run("R6 a world death at level 0 takes the kill (gg_vinceville)", vince, "0+0",
        "gun:g3sg1=0+1@0", "world=0+0@0", "gun:g3sg1=0+1@0", "world=0+0@0", "gun:g3sg1=0+1@0", "zeus=1+0@1", "teamkilled=1+0@1",
        "killed:awp=1+0@1", "killed:sg556=1+0@1", "killed:sg556=1+0@1", "gun:awp=1+1@1", "gun:awp=2+0@2");
    Run("R7 a demotion at level 0 takes the kill (gg_vinceville)", vince, "0+0",
        "gun:g3sg1=0+1@0", "killed:g3sg1=0+1@0", "killed:g3sg1=0+1@0", "killed:knife=0+0@0", "gun:g3sg1=0+1@0", "tk:g3sg1=0+0@0",
        "gun:g3sg1=0+1@0", "killed:g3sg1=0+1@0", "gun:g3sg1=1+0@1", "gun:awp=1+1@1", "gun:awp=2+0@2");
    Run("R8 a chain: a team kill, a Zeus death, a team kill (stalkerarena)", stalker, "8+1",
        "gun:mac10=9+0@9", "killed:mp7=9+0@9", "gun:p90=9+1@9", "tk:p90=8+1@9", "killed:taser=8+0@8", "gun:mac10=8+1@8", "killed:mac10=8+1@8",
        "gun:mac10=9+0@9", "killed:negev=9+0@9", "tk:hegrenade=8+0@9", "killed:mp7=8+0@8", "killed:taser=7+1@7", "tk:mp7=6+1@7",
        "gun:mp7=7+0@7", "gun:mp7=7+1@7", "gun:mp7=8+0@8", "gun:mac10=8+1@8", "killed:mac10=8+1@8", "gun:mac10=9+0@9");
    Run("R9 the golden knife: team kills and deaths leave it alone; the knife wins (stalkerarena)", stalker, "15+0",
        "gun:mag7=15+1@15", "gun:mag7=16+0@16", "teamkilled=16+0@16", "teamkilled=16+0@16", "killed:mag7=16+0@16", "killed:fiveseven=16+0@16",
        "killed:mag7=16+0@16", "teamkilled=16+0@16", "killed:nova=16+0@16", "win");
}

// ---- the kill counter against five whole real matches: every gun kill on the rung of the gun in the killer's hands ----

{
    var matches = new List<(string Name, string[] Ladder, string[] Counted, string Winner, List<string[]> Events)>();
    var sep19 = File.ReadAllLines(Path.Combine(here, "armsrace-2026-09-19.txt")).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => x.Split(' ')).ToList();
    Eq(sep19.Count, 119, "2026-09-19: 119 kills");
    matches.Add(("2026-09-19", ["g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9", "revolver", "hkp2000",
                                "glock", "elite", "sawedoff", "mag7"], ["P1", "P2", "P3", "P4"], sep19[^1][0], sep19));
    foreach (var line in File.ReadAllLines(Path.Combine(here, "armsrace-2026-11-04.txt")))
    {
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var f = line.Split(' ');
        if (f[0] == "match") matches.Add(($"2026-11-04 {f[1]}", f[2].Split(','), f[3].Split(','), f[4], new List<string[]>()));
        else matches[^1].Events.Add(f);
    }
    Eq(string.Join(" ", matches.Select(m => m.Events.Count)), "119 148 29 149 162", "the five matches' kills and deaths without an enemy, in order");
    Check(matches.SelectMany(m => m.Events).All(e => e.Length == 5 && e[0] is "-" or "P1" or "P2" or "P3" or "P4" && e[2] is "P1" or "P2" or "P3" or "P4"),
        "the fixtures name nobody: players are P1-P4");
    Eq(matches.SelectMany(m => m.Events).Count(e => e[0] != "-" && e[1] == e[3]), 26, "...with 26 team kills");
    Eq(matches.SelectMany(m => m.Events).Count(e => e[0] == "-"), 4, "...and 4 deaths without an enemy");

    // everybody: every player counted from the match's start; else only those the plugin followed from it, the rest joined later.
    // swapFirst: the game hands out the golden knife before it reports the kill that finished the last gun level.
    (int Guns, int OnRung, int Notes, bool Won, int Top) Replay((string Name, string[] Ladder, string[] Counted, string Winner, List<string[]> Events) m, bool everybody, bool swapFirst)
    {
        var names = m.Events.SelectMany(e => new[] { e[0], e[2] }).Where(n => n != "-").Distinct().ToList();
        ulong Id(string n) => (ulong)(names.IndexOf(n) + 1);
        var counter = new Ladder(new ProgressConfig());
        counter.StartMatch(names.Where(n => everybody || m.Counted.Contains(n)).Select(Id));
        int guns = 0, onRung = 0, knifeWin = -1;
        for (var i = 0; i < m.Events.Count; i++)
        {
            var e = m.Events[i];
            if (e[0] == "-")
            {
                counter.Suicide(Id(e[2]));
                continue;
            }
            ulong killer = Id(e[0]), victim = Id(e[2]);
            var weapon = e[4];
            var kind = Weapons.Kind(weapon);
            var held = counter.Held(killer);
            var before = counter.State(killer);
            if (kind == WeaponKind.Gun)
            {
                guns++;
                if (Array.IndexOf(m.Ladder, weapon) == held) onRung++;
                else Console.WriteLine($"      off the rung: {m.Name}, event {i + 1}: {e[0]} {weapon} with the counter's rung {held} in hand (level {before.Level} + {before.Kills})");
            }
            if (e[1] == e[3])
            {
                counter.TeamKill(killer, victim, weapon);
                continue;
            }
            if (kind == WeaponKind.Knife && before.Level >= GoldenKnife && knifeWin < 0) knifeWin = i;
            var finishes = before.Kills == 1 && before.Level == GoldenKnife - 1 && kind != WeaponKind.Knife;
            counter.Kill(killer, victim, weapon, held < GoldenKnife && !(swapFirst && finishes));
        }
        var standings = counter.Standings(names.Select(Id));
        var won = m.Winner == "-" ? knifeWin < 0 : knifeWin == m.Events.Count - 1 && m.Events[^1][0] == m.Winner && standings.Leader == Id(m.Winner);
        return (guns, onRung, counter.Notes, won, standings.Top);
    }

    var gunKills = new[] { 111, 131, 22, 139, 140 };
    foreach (var everybody in new[] { true, false })
        foreach (var swapFirst in new[] { false, true })
        {
            var how = $"{(everybody ? "everybody counted from the start" : "as the plugin followed them")}, {(swapFirst ? "the knife handed out first" : "the kill reported first")}";
            var total = 0;
            for (var i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var r = Replay(m, everybody, swapFirst);
                total += r.OnRung;
                Eq(r.Guns, gunKills[i], $"{m.Name}: its gun kills ({how})");
                Eq(r.OnRung, r.Guns, $"{m.Name}: every gun kill made with the gun the counter has in the killer's hands ({how})");
                Eq(r.Notes, 0, $"{m.Name}: the counter never had to place anybody ({how})");
                Check(r.Won, $"{m.Name}: {(m.Winner == "-" ? "cut short, nobody won on the knife" : $"the match ends on {m.Winner}'s knife kill on the golden knife, the first made there")} ({how})");
                if (m.Winner != "-") Eq(r.Top, 2 * (GoldenKnife + 1), $"{m.Name}: the winner ends one level past the knife ({how})");
            }
            Eq(total, 543, $"543 of 543 gun kills on their rung ({how})");
        }
}

// ---- the boost decided again after every kill (RecalculateOnKill) ----

{
    var c = Defaults();
    var lsc = c.Balance.LifestealCounts;
    bool Same(Boost a, Boost b) =>
        a.Tier == b.Tier && a.Health == b.Health && a.DamageTaken == b.DamageTaken && a.DamageDealt == b.DamageDealt && a.Lifesteal == b.Lifesteal
        && a.RegenPerSecond == b.RegenPerSecond && a.RegenDelaySeconds == b.RegenDelaySeconds && a.SecondChance == b.SecondChance
        && a.InfiniteAmmo == b.InfiniteAmmo && a.OneHitMelee == b.OneHitMelee && a.Bounty == b.Bounty;
    string Values(Boost b) =>
        string.Create(CultureInfo.InvariantCulture, $"{b.Health:0.00} {b.DamageTaken:0.00} {b.DamageDealt:0.00} {b.Lifesteal:0.00}");

    // A life of player 3 that spawned 8 behind (leader 1 at 20): curve B's 8+ tier, +120% health (100 -> 220).
    var spawn = Rules.Decide(c, Stand((1, 20), (2, 15), (3, 12), (4, 10)), 3);
    Check(spawn is { Tier: 8, InfiniteAmmo: true, ExtraProtection: 3 } && Values(spawn) == "1.20 -0.40 0.40 0.35", $"spawned 8 behind: tier 8+ ({Values(spawn)})");
    var life = new MidLife(spawn, spawn.Health, Rules.SpawnHealth(100, spawn), false);
    Eq(life.MaxHealth, 220, "...220 health");

    // It shrinks after the player's own kill: 7 behind. The health stays +120%; damage taken, damage dealt and lifesteal are cut so that
    // the own factor is tier 7+'s 4.57: (1 + 1.20 + 0.25) x 1.30 / 0.70 = 4.55, and one more 5% step of any of them would pass it.
    life = Rules.Recalculate(c, Stand((1, 20), (2, 15), (3, 13), (4, 10)), 3, false, life.Boost, life.Health, 100, life.MaxHealth);
    var b7 = life.Boost;
    var own7 = Rules.Own(Rules.Decide(c, Stand((1, 20), (3, 13)), 3), lsc);
    Check(b7 is { Tier: 7, HealthHeld: true } && life.TierChanged && life.Health == 1.20 && life.MaxHealth == 220,
        $"their own kill, 7 behind: tier 7+, the +120% health held, 220 kept ({Values(b7)})");
    Eq(Values(b7), "1.20 -0.30 0.30 0.25", "...the other three cut: -30% damage taken, +30% damage, 25% lifesteal");
    Check(Rules.Own(b7, lsc) <= own7 + 1e-9 && Math.Abs(own7 - 4.569) < 1e-3,
        $"...the own factor {Rules.Own(b7, lsc):0.000} within tier 7+'s {own7:0.000}");
    var t7 = c.Tiers.Single(t => t.MinBehind == 7);
    Check(Rules.Own(1.20, b7.Lifesteal, b7.DamageDealt, b7.DamageTaken - 0.05, lsc) > own7 && Rules.Own(1.20, b7.Lifesteal, b7.DamageDealt + 0.05, b7.DamageTaken, lsc) > own7
          && (b7.Lifesteal + 0.05 > t7.Lifesteal + 1e-9 || Rules.Own(1.20, b7.Lifesteal + 0.05, b7.DamageDealt, b7.DamageTaken, lsc) > own7),
        "...and no 5% step of them left unused");
    Check(b7 is { SecondChance: true, InfiniteAmmo: true, OneHitMelee: true, RegenPerSecond: 0.08, RegenDelaySeconds: 2, ExtraProtection: 3 },
        "...the extras are tier 7+'s; the spawn protection stays the spawn's");

    // More kills: 5 behind (the own factor 2.56: the held health is 2.20 of it), then 2 behind (1.10: the health alone is past it).
    life = Rules.Recalculate(c, Stand((1, 20), (3, 15)), 3, false, life.Boost, life.Health, 100, life.MaxHealth);
    Check(life.Boost is { Tier: 5, HealthHeld: true, SecondChance: true, InfiniteAmmo: false, OneHitMelee: false, RegenPerSecond: 0.05 } && Values(life.Boost) == "1.20 -0.05 0.10 0.00",
        $"5 behind: tier 5+, the health held, -5% damage taken and +10% damage beside it, no lifesteal; no infinite ammo, no one-hit knife ({Values(life.Boost)})");
    Check(Rules.Own(life.Boost, lsc) <= 2.56 + 1e-9, $"...the own factor {Rules.Own(life.Boost, lsc):0.000} within 2.56");
    life = Rules.Recalculate(c, Stand((1, 20), (3, 18)), 3, false, life.Boost, life.Health, 100, life.MaxHealth);
    Check(life.Boost is { Tier: 2, HealthHeld: true, SecondChance: false, RegenPerSecond: 0.03, RegenDelaySeconds: 4 } && Values(life.Boost) == "1.20 0.00 0.00 0.00" && life.MaxHealth == 220,
        $"2 behind: the held health alone is past tier 2+'s 1.10 - the other three zero, regeneration 3%/s after 4 s, no second chance ({Values(life.Boost)})");
    // ...it takes the lead by 2 (a 10% bounty), then 1 catches up to a tie at the top
    life = Rules.Recalculate(c, Stand((1, 20), (3, 22)), 3, false, life.Boost, life.Health, 100, life.MaxHealth);
    Check(life.Boost is { IsBounty: true, Bounty: 0.10, Lead: 2, Tier: 0, HealthHeld: true, SecondChance: false, RegenPerSecond: 0 } && Values(life.Boost) == "1.20 0.00 0.00 0.00"
          && life.TierChanged && life.Boost.Step == "bounty +10%",
        "the lead by 2: the leader's +10% bounty, the +120% health still held, nothing else");
    Near(Rules.Multiplier(Boost.None, life.Boost), 1.1, "...hits on them take the bounty", 1e-5);
    var asLeader = life.Boost;
    life = Rules.Recalculate(c, Stand((1, 22), (3, 22)), 3, false, life.Boost, life.Health, 100, life.MaxHealth);
    Check(life.Boost is { IsBounty: false, IsBoost: false, Tier: 0, HealthHeld: true } && life.Boost.Health == 1.20 && life.Boost.Step == "no boost" && life.TierChanged,
        "a tie at the top: no boost, no bounty, the health held");

    // It grows after the leader's kill: a life that spawned 4 behind (+35%, 135 health) is now 5 behind, then 8.
    var s4 = Rules.Decide(c, Stand((1, 10), (2, 6)), 2);
    var grow = new MidLife(s4, s4.Health, Rules.SpawnHealth(100, s4), false);
    Eq(grow.MaxHealth, 135, "spawned 4 behind: 135 health");
    grow = Rules.Recalculate(c, Stand((1, 11), (2, 6)), 2, false, grow.Boost, grow.Health, 100, grow.MaxHealth);
    var d5 = Rules.Decide(c, Stand((1, 11), (2, 6)), 2);
    Check(Same(grow.Boost, d5) && !grow.Boost.HealthHeld && grow.Health == 0.50 && grow.MaxHealth == 150 && grow.TierChanged,
        $"the leader's kill, 5 behind: tier 5+ exactly, the maximum raised to 150 (the current health left alone) ({Values(grow.Boost)})");
    grow = Rules.Recalculate(c, Stand((1, 14), (2, 6)), 2, false, grow.Boost, grow.Health, 100, grow.MaxHealth);
    Check(grow.Boost.Tier == 8 && grow.Health == 1.20 && grow.MaxHealth == 220 && !grow.Boost.HealthHeld, "8 behind: tier 8+, the maximum up to 220");
    var same = Rules.Recalculate(c, Stand((1, 15), (2, 6)), 2, false, grow.Boost, grow.Health, 100, grow.MaxHealth);
    Check(!same.TierChanged && same.MaxHealth == 220, "9 behind: still tier 8+, nothing to log");
    var back = Rules.Recalculate(c, Stand((1, 15), (2, 9)), 2, false, same.Boost, same.Health, 100, same.MaxHealth);
    Check(back.Boost is { Tier: 6, HealthHeld: true } && back.MaxHealth == 220 && back.Health == 1.20, "back to 6 behind: the 220 kept, the rest cut");
    var vampire = Rules.Recalculate(c, Stand((1, 14), (2, 6)), 2, false, s4, 0.35, 1000, 2000);
    Eq(vampire.MaxHealth, 2200, "under Vampire (1,000 at spawn, at most 2,000): +120% raises the maximum to 2,200");
    var vampireLow = Rules.Recalculate(c, Stand((1, 11), (2, 6)), 2, false, s4, 0.35, 1000, 2000);
    Eq(vampireLow.MaxHealth, 2000, "...+50% (1,500) leaves Vampire's own 2,000 alone");

    // The second chance: granted when the gap reaches 4 and the life has none used, taken back when it closes below 4, once a life.
    var s3 = Rules.Decide(c, Stand((1, 10), (2, 7)), 2);
    Check(!s3.SecondChance, "3 behind: no second chance");
    var at4 = Rules.Recalculate(c, Stand((1, 11), (2, 7)), 2, false, s3, s3.Health, 100, Rules.SpawnHealth(100, s3)).Boost;
    var lethal = new HitInput(80, Boost.None, at4, 50, 0, Melee.None, false, true, false);
    Check(at4.SecondChance && Rules.Hit(lethal).SecondChanceUsed, "4 behind mid-life: the second chance granted, and it saves");
    Check(!Rules.Hit(lethal with { SecondChanceLeft = false }).SecondChanceUsed, "...but not when the life used one already");
    var at3 = Rules.Recalculate(c, Stand((1, 11), (2, 8)), 2, false, at4, at4.Health, 100, 135).Boost;
    Check(!at3.SecondChance && !Rules.Hit(lethal with { Victim = at3 }).SecondChanceUsed, "back to 3 behind: an unused second chance taken back");
    Check(at3.ExtraProtection == 0 && at4.ExtraProtection == 0 && Rules.Decide(c, Stand((1, 11), (2, 7)), 2).ExtraProtection == 2,
        "the spawn protection stays the spawn's: none here, though 4 behind at a spawn gives 2 s");

    // Everything follows the new decision both ways, per hit and per tick: damage, resistance, lifesteal, regeneration, ammo, the one-hit knife.
    Near(Rules.Multiplier(b7, Boost.None), 1.30, "the cut damage applies at once", 1e-5);
    Eq(Rules.Lifesteal(b7, 100, 100), 25, "...the cut lifesteal");
    double carry = 0;
    Eq(Rules.Regen(asLeader, 10, 1, 100, ref carry), 0, "the leader regenerates nothing");
    Eq(Rules.Regen(b7, 10, 1, 100, ref carry), 8, "...7 behind, 8% a second of the spawn health");
    Check(!Rules.Hit(new HitInput(40, at3, Boost.None, 100, 0, Melee.Knife, false, true, false)).OneHit, "a 3-behind knife is a normal knife");

    // The log's one line, only at a change of tier or of the bounty's step.
    Eq(Words.Change("P3", spawn, b7, Stand((1, 20), (2, 15), (3, 13), (4, 10)), "P1", 100, 220),
        "P3's boost changes mid-life, tier 8+ -> tier 7+: +120% health held (100, at most 220), -30% damage taken, +30% damage, 25% lifesteal, regeneration 8%/s after 2 s, second chance, infinite ammo, one-hit knife and Zeus; duel factor 5.46 - 7 kills behind (leader P1 20, them 13, 4 players)",
        "the line for a boost that shrinks (the duel factor with the leader's 20% bounty: 4.55 x 1.2)");
    Eq(Words.Change("P2", s4, d5, Stand((1, 11), (2, 6)), "P1", 100, 150),
        "P2's boost changes mid-life, tier 4+ -> tier 5+: +50% health (100, at most 150), -25% damage taken, +20% damage, 10% lifesteal, regeneration 5%/s after 3 s, second chance; duel factor 3.07 - 5 kills behind (leader P1 11, them 6, 2 players)",
        "...for one that grows (2.56 x 1.2)");
    var lead4 = Rules.Recalculate(c, Stand((1, 20), (3, 24)), 3, false, b7, 1.20, 100, 220).Boost;
    Eq(Words.Change("P3", b7, lead4, Stand((1, 20), (3, 24)), null, 100, 220),
        "P3's boost changes mid-life, tier 7+ -> bounty +20%: +120% health held (100, at most 220), bounty +20% damage taken from enemies - leads by 4 kills (24 to 20, 2 players)",
        "...for a boosted player who takes the lead");
    var plainLeader = Rules.Recalculate(c, Stand((1, 20), (3, 24)), 3, false, Boost.None, 0, 100, 100).Boost;
    Eq(Words.Change("P3", Boost.None, plainLeader, Stand((1, 20), (3, 24)), null, 100, 100),
        "P3's boost changes mid-life, no boost -> bounty +20%: bounty +20% damage taken from enemies - leads by 4 kills (24 to 20, 2 players)",
        "...and for one with nothing held: the standing last, never the shape of a spawn's line");
    var lead5 = Rules.Recalculate(c, Stand((1, 20), (3, 25)), 3, false, lead4, 1.20, 100, 220);
    Check(!lead5.TierChanged, "a lead of 5: still +20%, nothing to log");
    Check(Rules.Recalculate(c, Stand((1, 20), (3, 26)), 3, false, lead5.Boost, 1.20, 100, 220).TierChanged, "a lead of 6: +30%, logged");

    // Random lives: whatever the standings do, health and its maximum never go down, an unheld boost is exactly the decision, a held one
    // never gives more of the other three than the decision and never more than the decision's own factor beside the held health.
    var rng = new Random(70);
    int lives = 0, changes = 0, held = 0, broken = 0;
    for (var run = 0; run < 2000; run++)
    {
        var progress = new Dictionary<ulong, int> { [1] = rng.Next(0, 30), [2] = rng.Next(0, 30), [3] = rng.Next(0, 30), [4] = rng.Next(0, 30) };
        var mate = rng.Next(4) == 0;
        var first = Rules.Decide(c, new Standings(progress), 2, mate);
        var now = new MidLife(first, first.IsBoost ? first.Health : 0, Rules.SpawnHealth(100, first.IsBoost ? first : Boost.None), false);
        lives++;
        for (var k = 0; k < 12; k++)
        {
            var who = (ulong)rng.Next(1, 5);
            progress[who] = Math.Max(0, progress[who] + (rng.Next(5) == 0 ? -2 : 1));
            var s = new Standings(progress);
            var next = Rules.Recalculate(c, s, 2, mate, now.Boost, now.Health, 100, now.MaxHealth);
            var decided = Rules.Decide(c, s, 2, mate);
            changes++;
            var ok = next.Health >= now.Health - 1e-12 && next.MaxHealth >= now.MaxHealth && next.Boost.ExtraProtection == first.ExtraProtection
                     && next.TierChanged == (now.Boost.Step != next.Boost.Step) && Math.Abs(next.Boost.Health - next.Health) < 1e-9
                     && next.MaxHealth >= Rules.SpawnHealth(100, next.Boost);
            if (next.Boost.HealthHeld)
            {
                held++;
                ok &= Math.Abs(next.Boost.Health - now.Health) < 1e-12 && next.Boost.Health > decided.Health
                      && -next.Boost.DamageTaken <= -decided.DamageTaken + 1e-9 && next.Boost.DamageDealt <= decided.DamageDealt + 1e-9 && next.Boost.Lifesteal <= decided.Lifesteal + 1e-9
                      && Rules.Own(next.Boost, lsc) <= Math.Max(Rules.Own(decided, lsc), 1 + next.Boost.Health) + 1e-9
                      && next.Boost.SecondChance == decided.SecondChance && next.Boost.InfiniteAmmo == decided.InfiniteAmmo && next.Boost.OneHitMelee == decided.OneHitMelee
                      && next.Boost.RegenPerSecond == decided.RegenPerSecond && next.Boost.Bounty == decided.Bounty && next.Boost.Tier == decided.Tier;
            }
            else ok &= Same(next.Boost, decided);
            if (!ok)
            {
                broken++;
                if (broken <= 5) Console.WriteLine($"      broken: run {run} step {k}: {Values(now.Boost)} -> {Values(next.Boost)} (decided {Values(decided)}, held {now.Health:0.00})");
            }
            now = next;
        }
    }
    Eq(broken, 0, $"{lives} random lives, {changes} recalculations ({held} with the health held): health and its maximum only ever up, the rest the decision's");
}

// ---- the message ----

{
    var c = Reference();
    var m = c.Message;
    var worstLines = 0;
    var worstWidth = 0;
    var configs = new[] { c, Open(), Defaults() };
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
    var d = Defaults();
    Eq(string.Join(" | ", Words.Lines(Rules.ForGap(d, 2, 2, 0, 0), d.Message)), "2 kills behind the leader: | +5% health, 5% lifesteal, | regeneration",
        "curve B, 2 behind: the panel");
    Eq(string.Join(" | ", Words.Lines(Rules.ForGap(d, 8, 8, 0, 0), d.Message)), "8 kills behind: +120% HP, | -40% dmg taken, +40% dmg, | 35% lifesteal, regen, | 2nd chance, +3s protection, | no reloads, 1-hit knife/zeus",
        "curve B, 8 behind: the panel");

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
    Console.WriteLine("The panel per tier:");
    foreach (var t in c.Tiers)
        Console.WriteLine($"  {t.MinBehind}+: \"{string.Join(" | ", Words.Lines(Rules.ForGap(c, t.MinBehind, t.MinBehind, 0, 0), c.Message))}\"");
    Console.WriteLine("The cap at work (health / damage taken / damage dealt / lifesteal given; the leader's lead over second place in brackets):");
    foreach (var (top, mine, lead) in new[] { (4, 0, 2), (4, 2, 2), (8, 1, 2), (8, 4, 4), (12, 5, 2), (16, 8, 3), (16, 11, 3), (16, 13, 2), (24, 14, 4), (30, 23, 6), (33, 20, 2) })
    {
        var b = Rules.ForGap(c, top - mine, top, mine, Rules.BountyFor(c.Bounty, lead));
        Console.WriteLine($"  leader {top,2}, player {mine,2} ({top - mine,2} behind, lead {lead}, bounty {Rules.BountyFor(c.Bounty, lead) * 100:0}%): allowed {b.Allowed:0.00}, raw {b.RawFactor:0.00} -> "
                          + $"+{b.Health * 100:0}% / {b.DamageTaken * 100:0}% / +{b.DamageDealt * 100:0}% / {b.Lifesteal * 100:0}% = {b.Factor:0.00}{(b.Capped ? " capped" : "")}   \"{string.Join(" | ", Words.Lines(b, c.Message))}\"");
    }
    Console.WriteLine("Mid-life, a life that holds +120% health (spawned 8+ behind) as its gap closes:");
    foreach (var gap in new[] { 8, 7, 6, 5, 4, 3, 2, 1 })
    {
        var b = Rules.Redecide(c, Stand((1, 20), (2, 20 - gap)), 2, false, 1.20, 3);
        Console.WriteLine($"  {gap} behind: +{b.Health * 100:0}% / {b.DamageTaken * 100:0}% / +{b.DamageDealt * 100:0}% / {b.Lifesteal * 100:0}%, own factor {Rules.Own(b, true):0.00} (the tier's {Rules.Own(Rules.Decide(c, Stand((1, 20), (2, 20 - gap)), 2), true):0.00})");
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
