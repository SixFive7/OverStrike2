using System.Globalization;
using System.Text.Json;
using CatchUp;

// CatchUp.SelfTest - every decision of the catch-up module, without a game server: the config and its file (and the players' copy of its
// numbers in docs/reference.md), a hit's boost from the gap between its two players and plain Counter-Strike between equals, each player's
// own extras and the leader's bounty, the duel factor, each hit, the healing, the worked examples of 2026-11-08 and Vampire's numbers in hits
// to kill (Cs2Hits, a model of CS2's armour), the Arms Race kill counter (its rules case by case, nine real players' runs, five whole real
// matches replayed, and a sixth whose count goes wrong and comes back), the boost decided again after every kill, the message and its
// timing, and a life's line in the log. CatchUpConfig.cs, CatchUpRules.cs, Ladder.cs, CatchUpWords.cs and bridge/Shared/Vampire.cs are
// compiled in as source; CatchUpModule.cs (the game half) is not: the plugin's build compiles it.
//
// Run with `dotnet run -c Release`; exit code 0 means every check passed. `--table` also prints the pair table, the panels and the cards.

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

// A fixed set of numbers, not the defaults. The mechanics below - the bounty steps,
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
    c.Bounty.Max = 0.30;
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
    var l = new Ladder();
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

// ---- config: the defaults, the shipped file, and what a bad file does ----

{
    var notes = new List<string>();
    var d = new CatchUpConfig();
    d.Normalize(notes);
    Eq(notes.Count, 0, "the defaults need no correction");
    Eq(d.Tiers.Count, 7, "seven tiers by default (curve B): one per gap from 2 to 8+");
    Eq(string.Join(",", d.Tiers.Select(t => t.MinBehind)), "2,3,4,5,6,7,8", "tiers from 2, 3, 4, 5, 6, 7 and 8 behind");

    var shipped = Path.Combine(here, CatchUpConfig.FileName);
    Check(File.Exists(shipped), "catchup.json is copied beside the test");
    var fileNotes = new List<string>();
    var f = CatchUpConfig.Load(shipped, fileNotes);
    Eq(fileNotes.Count, 0, $"catchup.json needs no correction ({string.Join("; ", fileNotes)})");
    Eq(f.ToJson(), d.ToJson(), "catchup.json holds exactly the built-in defaults");
    // ...and names every setting and nothing else: a key the file lacks takes its default and a key the module does not read is ignored,
    // so neither shows above.
    static IEnumerable<string> Keys(JsonElement e, string at) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Keys(p.Value, $"{at}.{p.Name}").Prepend($"{at}.{p.Name}")),
        JsonValueKind.Array => e.EnumerateArray().SelectMany((x, i) => Keys(x, $"{at}[{i}]")),
        _ => [],
    };
    using (var file = JsonDocument.Parse(File.ReadAllText(shipped), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
    using (var built = JsonDocument.Parse(d.ToJson()))
    {
        var (inFile, inCode) = (Keys(file.RootElement, "").ToHashSet(), Keys(built.RootElement, "").ToHashSet());
        Check(inFile.SetEquals(inCode), $"...and names every setting, nothing else (only in the file: {string.Join(" ", inFile.Except(inCode))}; only in the code: {string.Join(" ", inCode.Except(inFile))})");
    }
    // An older file still loads: what it holds beside the tiers and the bounty - the switches, the counter's rules, the message's timing,
    // the cap - is the code's now, and means nothing in the file.
    var older = new List<string>();
    var o = CatchUpConfig.Parse("""
        { "Enabled": false, "RecalculateOnKill": false,
          "Balance": { "LifestealCounts": false, "RoundTo": 0.5, "MaxDuelFactor": 3.0, "ScoreboardCap": true, "ScoreboardCapFactor": 2.0 },
          "Progress": { "KillsPerLevel": 3, "KnifeKillLevelsUp": false, "KnifeDeathDemotes": false, "TaserDeathCostsKill": false, "SuicideDemotes": false,
                        "TeamKillDemotes": false, "LastLevelOnlyKnife": false },
          "Message": { "ToBoosted": false, "ToLeader": false, "Seconds": 9, "DelaySeconds": 5, "FirstSpawnDelaySeconds": 9, "LineWidth": 70, "MaxLines": 9 },
          "Effects": { "ApplyDelaySeconds": 1, "RegenTickSeconds": 3 } }
        """);
    o.Normalize(older);
    Check(older.Count == 0 && o.ToJson() == d.ToJson(), "an older file still loads: its switches, the counter's rules, the message's timing and the cap mean nothing now");

    // ---- the players' tables in docs/reference.md (Catch-up) hold the code's tiers, and its bounty sentence the code's bounty: read back
    // number by number, so the page cannot drift from catchup.json. Their words may change; their numbers may not ----
    var docs = Path.Combine(here, "reference.md");
    Check(File.Exists(docs), "docs/reference.md is copied beside the test");
    if (File.Exists(docs))
    {
        var text = File.ReadAllText(docs).Replace("\r", "");
        var section = text.IndexOf("\n## Catch-up\n", StringComparison.Ordinal) is >= 0 and var s0 ? text[s0..] : "";
        section = section.IndexOf("\n## ", 5, StringComparison.Ordinal) is > 0 and var s1 ? section[..s1] : section;
        var lines = section.Split('\n').ToList();
        static double Pct(string cell) => cell == "" ? 0 : double.Parse(cell.TrimStart('+', '-', '\u2212').TrimEnd('%'), CultureInfo.InvariantCulture) / 100;
        // The table rows under a header, until the first line that is no table row.
        List<string[]> Rows(string header)
        {
            var at = lines.FindIndex(l => l == header);
            Check(at >= 0, $"docs: the catch-up section has the table '{header}'");
            var rows = new List<string[]>();
            for (var i = at + 2; at >= 0 && i < lines.Count && lines[i].StartsWith("| ", StringComparison.Ordinal); i++)
                rows.Add(lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray());
            return rows;
        }
        // The fight's table: one row a gap between the two players, from 2, the last "and more".
        var fight = Rows("| Kills between you | Your damage on them | Their damage on you | Lifesteal on them | Also, against them |");
        var tiers = f.Tiers;
        Eq(fight.Count, tiers.Count, "docs: the fight's table has a row a tier");
        var also = (Second: false, Knife: false);
        for (var i = 0; i < Math.Min(fight.Count, tiers.Count); i++)
        {
            var (cells, t) = (fight[i], tiers[i]);
            if (cells.Length != 5) { Check(false, $"docs: a fight row has 5 cells ({string.Join("|", cells)})"); continue; }
            if (cells[4] != "the same") also = (cells[4].Contains("a second chance"), cells[4].Contains("a one-hit knife and Zeus"));
            var gap = int.Parse(cells[0].Split(' ')[0], CultureInfo.InvariantCulture);
            var taken = (int)Math.Round((Rules.Taken(t) - 1) * 100);
            Check(gap == t.MinBehind && Math.Abs(Pct(cells[1]) - t.DamageDealt) < 1e-9 && (int)Math.Round(-Pct(cells[2]) * 100) == taken
                  && Math.Abs(Pct(cells[3]) - t.Lifesteal) < 1e-9 && also == (t.SecondChance, t.OneHitKnifeAndTaser),
                $"docs: the fight's row for {t.MinBehind} holds catchup.json's - its damage +{t.DamageDealt * 100:0}%, theirs {taken}%, lifesteal {t.Lifesteal * 100:0}%, the second chance and the one-hit knife ({string.Join(" | ", cells)})");
        }
        Check(fight.Count > 0 && fight[^1][0] == $"{tiers[^1].MinBehind} and more" && fight.SkipLast(1).All(r => !r[0].Contains("and more")),
            "docs: ...the last row, and only it, reads 'and more'");
        // The player's own table: one row a band of tiers alike, by kills behind the leader ("2-3", "7 and more").
        var own = Rows("| Kills behind the leader | Regeneration | Also |");
        var covered = new List<int>();
        foreach (var cells in own)
        {
            if (cells.Length != 3) { Check(false, $"docs: an own row has 3 cells ({string.Join("|", cells)})"); continue; }
            var range = System.Text.RegularExpressions.Regex.Match(cells[0], @"^(\d+)(?:-(\d+)| and more)?$");
            var lo = int.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            var hi = range.Groups[2].Success ? int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture) : cells[0].EndsWith("and more") ? int.MaxValue : lo;
            var regen = System.Text.RegularExpressions.Regex.Match(cells[1], @"^(\d+)% a second, after (\d+) s");
            var protection = System.Text.RegularExpressions.Regex.Match(cells[2], @"(\d+) s more spawn protection");
            foreach (var t in tiers.Where(t => t.MinBehind >= lo && t.MinBehind <= hi))
            {
                covered.Add(t.MinBehind);
                Check(regen.Success && Math.Abs(double.Parse(regen.Groups[1].Value, CultureInfo.InvariantCulture) / 100 - t.RegenPerSecond) < 1e-9
                      && Math.Abs(double.Parse(regen.Groups[2].Value, CultureInfo.InvariantCulture) - t.RegenDelaySeconds) < 1e-9
                      && Math.Abs((protection.Success ? double.Parse(protection.Groups[1].Value, CultureInfo.InvariantCulture) : 0) - t.ExtraSpawnProtectionSeconds) < 1e-9
                      && cells[2].Contains("infinite ammo") == t.InfiniteAmmo,
                    $"docs: the own row '{cells[0]}' holds tier {t.MinBehind}+'s regeneration, spawn protection and infinite ammo");
            }
        }
        Eq(string.Join(",", covered), string.Join(",", tiers.Select(t => t.MinBehind)), "docs: the own table's bands cover every tier once, in order");
        var bounty = System.Text.RegularExpressions.Regex.Match(section, @"A leader (\d+) kills or more ahead of second place takes more damage from enemies: (\d+)% for every (\d+) kills of lead, up to (\d+)%");
        Check(bounty.Success && int.Parse(bounty.Groups[1].Value) == f.Bounty.MinLead && Math.Abs(int.Parse(bounty.Groups[2].Value) / 100.0 - f.Bounty.PerStep) < 1e-9
              && int.Parse(bounty.Groups[3].Value) == f.Bounty.StepKills && Math.Abs(int.Parse(bounty.Groups[4].Value) / 100.0 - f.Bounty.Max) < 1e-9,
            "docs: the bounty's sentence holds catchup.json's bounty - from what lead, how much for how many kills of lead, the most");
        var vampire = System.Text.RegularExpressions.Regex.Match(text, @"\| `!vampire` \| Vampire: every hit on an enemy heals (\d+)% of its damage, every kill of one (\d+) more, up to (\d+) health \|");
        Check(vampire.Success && int.Parse(vampire.Groups[1].Value) == Vampire.HitPercent && int.Parse(vampire.Groups[2].Value) == Vampire.KillHeal
              && int.Parse(vampire.Groups[3].Value) == Vampire.Cap, "docs: the Fun table's Vampire row holds Vampire's numbers");
    }

    var missing = new List<string>();
    var m = CatchUpConfig.Load(Path.Combine(here, "no-such-file.json"), missing);
    Check(missing.Count == 1 && missing[0].StartsWith("no no-such-file.json"), "a missing file is noted");
    Eq(m.ToJson(), d.ToJson(), "a missing file means the defaults");

    var badPath = Path.Combine(Path.GetTempPath(), $"catchup-selftest-{Environment.ProcessId}.json");
    File.WriteAllText(badPath, "{ \"Tiers\": tru");
    var bad = new List<string>();
    var b = CatchUpConfig.Load(badPath, bad);
    File.Delete(badPath);
    Check(bad.Count == 1 && bad[0].Contains("unreadable"), "bad JSON is noted");
    Eq(b.ToJson(), d.ToJson(), "bad JSON means the defaults");

    var loose = CatchUpConfig.Parse("{ // comments and trailing commas are allowed\n \"bounty\": { \"max\": 0.5, }, }");
    loose.Normalize(new List<string>());
    Near(loose.Bounty.Max, 0.5, "property names in any case, a nested value read");
    Eq(loose.Bounty.MinLead, 2, "a value missing from a section keeps its default");
    Eq(loose.Tiers.Count, 7, "a missing section keeps its defaults");

    var wild = CatchUpConfig.Parse("""
        { "Tiers": [ { "MinBehind": 9, "Health": 9, "DamageTaken": 0.5 }, { "MinBehind": 3, "DamageTaken": -2, "Lifesteal": 2 },
                     { "MinBehind": 3, "Health": 0.1 } ],
          "Bounty": { "StepKills": 0, "Max": 9 } }
        """);
    var wildNotes = new List<string>();
    wild.Normalize(wildNotes);
    Eq(string.Join(",", wild.Tiers.Select(t => t.MinBehind)), "3,9", "tiers sorted, a duplicate MinBehind once");
    Near(wild.Tiers[0].Health, 0.1, "the later of two tiers with one MinBehind wins");
    Near(wild.Tiers[1].Health, 5, "health clamped to 5");
    Near(wild.Tiers[1].DamageTaken, 0, "damage taken cannot be raised by a tier");
    Eq(wild.Bounty.StepKills, 1, "the bounty's StepKills at least 1");
    Near(wild.Bounty.Max, 2, "the bounty at most 2");
    Eq(wildNotes.Count, 7, $"every correction noted: {string.Join("; ", wildNotes)}");

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

    // The table as configured: every value reaches the boost - the fight's part as against a player the whole gap ahead, the leader.
    foreach (var t in c.Tiers)
    {
        var bo = Rules.ForGap(c, t.MinBehind, 0);
        Check(bo.Tier == t.MinBehind && bo.DamageDealt == t.DamageDealt && Math.Abs(bo.DamageTaken - (Rules.Taken(t) - 1)) < 1e-12 && bo.Lifesteal == t.Lifesteal
              && bo.RegenPerSecond == t.RegenPerSecond && bo.SecondChance == t.SecondChance && bo.ExtraProtection == t.ExtraSpawnProtectionSeconds
              && bo.InfiniteAmmo == t.InfiniteAmmo && bo.OneHitMelee == t.OneHitKnifeAndTaser,
            $"tier {t.MinBehind}+: the table's values");
    }
    // A whole sentence, word for word, on these numbers: tier 4+'s health and damage taken together, 0.80 / 1.40, 43% less.
    Eq(Words.Sentence(Rules.ForGap(c, 5, 0)),
        "You are 5 kills behind the leader: against players ahead of you, up to +20% damage, -43% damage taken, 20% lifesteal, a second chance; regeneration, +2 s spawn protection.",
        "the whole sentence");
    // ...and on the defaults: curve B's 5+ tier, 0.75 / 1.50 = half the damage from the players ahead
    Eq(Words.Sentence(Rules.ForGap(Defaults(), 5, 0)),
        "You are 5 kills behind the leader: against players ahead of you, up to +20% damage, -50% damage taken, 10% lifesteal, a second chance; regeneration, +2 s spawn protection.",
        "the same sentence on today's numbers");
    Eq(Words.Sentence(new Boost { Lead = 4, Bounty = 0.2 }), "You lead by 4 kills: +20% damage taken (a bounty).", "the leader's sentence");
    Eq(Words.Sentence(Boost.None), "", "nothing to say without a boost");
}

// ---- the duel factor, the leader's teammates, the bounty on a hit, and curve B ----

{
    var c = Reference();
    var d = Defaults();
    Near(Rules.DuelFactor(0.20, 0.10, 0.10, -0.10, 0), 1.3 * 1.1 / 0.9, "tier 2+ duel factor: lifesteal counts as health");
    Near(Rules.DuelFactor(0.40, 0.20, 0.20, -0.20, 0), 2.4, "tier 4+ duel factor");
    Near(Rules.DuelFactor(0.75, 0.30, 0.30, -0.30, 0), 2.05 * 1.3 / 0.7, "tier 7+ duel factor");
    Near(Rules.DuelFactor(0, 0, 0, 0, 0.3), 1.3, "the bounty alone");

    // The leader's teammates never fight him: they get what his enemies get, and the duel factor the log shows for them leaves his bounty out.
    var standings = Stand((1, 20), (2, 12), (3, 10), (4, 8)); // 1 leads by 8: a 30% bounty; 3 is 10 behind (curve B's 8+)
    var asEnemy = Rules.Decide(d, standings, 3);
    var asMate = Rules.Decide(d, standings, 3, teammateOfLeader: true);
    Check(asEnemy.Tier == 8 && (asEnemy with { Factor = 0 }) == (asMate with { Factor = 0 }), "the leader's teammate gets what his enemy in the same place gets: tier 8+");
    Near(asEnemy.Factor, 2.55 * 1.4 / 0.6 * 1.3, "...the enemy's duel factor with the leader's 30% bounty: 5.95 x 1.3");
    Near(asMate.Factor, 2.55 * 1.4 / 0.6, "...the teammate's without it: 5.95");
    Check(Rules.Decide(d, standings, 1, teammateOfLeader: true) is { IsBounty: true, Bounty: 0.30 }, "the leader's own bounty does not depend on the flag");

    // Every enemy's hit on the leader gets his whole bounty on top of the pair's own boost.
    Near(Rules.PairFor(c, 7, 0.3).Multiplier, 1.3 * 1.3, "a hit by a player 7 behind the leader: +30% damage and the whole 30% bounty", 1e-9);
    Near(Rules.ForGap(c, 7, 0.3).Factor, 2.05 * 1.3 / 0.7 * 1.3, "...and the duel factor the log shows counts it all: 3.81 x 1.3");

    // Curve B, the defaults: seven tiers.
    Check(d.Bounty is { MinLead: 2, StepKills: 2, PerStep: 0.10, Max: 0.30 }, "the defaults: the bounty, +10% per 2 kills of lead from 2, up to 30%");
    Check(d.Tiers.Select(t => (t.MinBehind, t.Health, t.DamageTaken, t.DamageDealt, t.Lifesteal)).SequenceEqual(
        [(2, 0.05, 0.00, 0.00, 0.05), (3, 0.15, -0.10, 0.05, 0.05), (4, 0.35, -0.15, 0.15, 0.05), (5, 0.50, -0.25, 0.20, 0.10),
         (6, 0.75, -0.30, 0.25, 0.20), (7, 0.95, -0.35, 0.35, 0.25), (8, 1.20, -0.40, 0.40, 0.35)]),
        "curve B's health, damage taken, damage dealt and lifesteal for 2, 3, 4, 5, 6, 7 and 8+ between two players");
    var ownB = d.Tiers.Select(t => Math.Round(Rules.Own(t), 2)).ToList();
    Eq(string.Join(" ", ownB.Select(x => x.ToString("0.00"))), "1.10 1.40 1.89 2.56 3.48 4.57 5.95", "...their own factors, the steep quadratic 1 + 0.10 (gap - 1)^2 in 5% steps");
    Eq(string.Join(" ", d.Tiers.Select(t => Math.Round((Rules.Taken(t) - 1) * 100).ToString("0"))), "-5 -22 -37 -50 -60 -67 -73",
        "...the damage a player ahead does to them, health and damage taken together: 5% less at 2 kills, 73% less at 8 and more");
    Check(d.Tiers.Select(t => (t.RegenPerSecond, t.RegenDelaySeconds)).SequenceEqual([(0.03, 4.0), (0.03, 4.0), (0.05, 3.0), (0.05, 3.0), (0.05, 3.0), (0.08, 2.0), (0.08, 2.0)]),
        "...regeneration 3%/s after 4 s at 2-3 behind the leader, 5%/s after 3 s at 4-6, 8%/s after 2 s from 7");
    Check(d.Tiers.Select(t => (t.SecondChance, t.ExtraSpawnProtectionSeconds, t.InfiniteAmmo, t.OneHitKnifeAndTaser)).SequenceEqual(
        [(false, 0.0, false, false), (false, 0.0, false, false), (true, 2.0, false, false), (true, 2.0, false, false), (true, 2.0, false, false),
         (true, 3.0, true, true), (true, 3.0, true, true)]),
        "...a second chance from 4 kills between, 2 s more spawn protection from 4 behind; 3 s more and infinite ammo from 7 behind, the one-hit knife and Zeus from 7 between");
    // Every gap to the leader gives exactly its tier's row, as the leader's enemy or his teammate.
    var notTable = 0;
    for (var behind = 2; behind <= 60; behind++)
        for (var lead = 0; lead <= 12; lead++)
            foreach (var mate in new[] { false, true })
            {
                var b = Rules.ForGap(d, behind, mate ? 0 : Rules.BountyFor(d.Bounty, lead));
                var t = Rules.TierFor(d.Tiers, behind)!;
                if (b.DamageDealt != t.DamageDealt || Math.Abs(b.DamageTaken - (Rules.Taken(t) - 1)) > 1e-12 || b.Lifesteal != t.Lifesteal || b.RegenPerSecond != t.RegenPerSecond) notTable++;
            }
    Eq(notTable, 0, "with curve B every gap gives exactly its tier's row, as the leader's enemy or his teammate");
}

// ---- a hit's boost from the gap between its two players (curve B) ----

{
    var d = Defaults();
    // Within a kill of each other: plain Counter-Strike, whatever either's gap to the leader.
    foreach (var gap in new[] { -1, 0, 1 })
        Eq(Rules.PairFor(d, gap, 0), Pair.Even with { Gap = gap }, $"{gap} kills between them: plain Counter-Strike");
    // The one behind hits harder: their row's damage, its lifesteal, the one-hit knife and Zeus from 7 - and no second chance on that side.
    foreach (var (gap, mult, ls, knife) in new[] { (2, 1.00, 0.05, false), (3, 1.05, 0.05, false), (4, 1.15, 0.05, false), (5, 1.20, 0.10, false),
                                                  (6, 1.25, 0.20, false), (7, 1.35, 0.25, true), (8, 1.40, 0.35, true), (20, 1.40, 0.35, true) })
    {
        var p = Rules.PairFor(d, gap, 0);
        Check(Math.Abs(p.Multiplier - mult) < 1e-9 && Math.Abs(p.Lifesteal - ls) < 1e-9 && p.OneHitMelee == knife && !p.SecondChance && p.Row == Math.Min(gap, 8),
            $"the attacker {gap} behind: x{mult:0.00}, {ls * 100:0}% lifesteal{(knife ? ", the one-hit knife" : "")} ({p})");
    }
    // The one ahead hits softer: the row's health and damage taken together; the one behind keeps a second chance from 4 kills.
    foreach (var (gap, mult, second) in new[] { (2, 1 / 1.05, false), (3, 0.90 / 1.15, false), (4, 0.85 / 1.35, true), (5, 0.75 / 1.50, true),
                                               (6, 0.70 / 1.75, true), (7, 0.65 / 1.95, true), (8, 0.60 / 2.20, true), (20, 0.60 / 2.20, true) })
    {
        var p = Rules.PairFor(d, -gap, 0);
        Check(Math.Abs(p.Multiplier - mult) < 1e-9 && p.Lifesteal == 0 && !p.OneHitMelee && p.SecondChance == second && p.Row == -Math.Min(gap, 8),
            $"the attacker {gap} ahead: x{mult:0.000}{(second ? ", the victim's second chance" : "")} ({p})");
    }
    // A duel between two players g kills apart is their tier's own factor: the one behind's hits over the one ahead's, lifesteal counted as health.
    foreach (var t in d.Tiers)
    {
        var (up, down) = (Rules.PairFor(d, t.MinBehind, 0), Rules.PairFor(d, -t.MinBehind, 0));
        Near(up.Multiplier / down.Multiplier * (1 + t.Lifesteal / (1 + t.Health)), Rules.Own(t), $"{t.MinBehind} kills between: the duel is the tier's own factor {Rules.Own(t):0.00}", 1e-9);
    }
    // The leader's bounty: on every enemy hit on him, on top of the pair.
    Near(Rules.PairFor(d, 8, 0.3).Multiplier, 1.4 * 1.3, "a player 8 behind on the leader with a 30% bounty: x1.82", 1e-9);
    Near(Rules.PairFor(d, 2, 0.1).Multiplier, 1.1, "...2 behind on a leader with 10%: the bounty alone", 1e-9);
    Check(Enumerable.Range(-30, 61).All(g => !(Rules.PairFor(d, g, 0) is { OneHitMelee: true, SecondChance: true })), "no pair has both the one-hit knife and a second chance: one is behind the other");
}

// ---- equal ranks: plain Counter-Strike between any two players within a kill of each other ----

{
    var d = Defaults();
    var rng = new Random(229);
    int pairs = 0, near = 0, wrong = 0;
    for (var run = 0; run < 3000; run++)
    {
        var count = rng.Next(2, 9);
        var progress = Enumerable.Range(1, count).ToDictionary(i => (ulong)i, _ => rng.Next(0, 33));
        var s = new Standings(progress);
        foreach (var a in progress.Keys)
            foreach (var v in progress.Keys)
            {
                if (a == v) continue;
                pairs++;
                var bounty = v == s.Leader ? Rules.BountyFor(d.Bounty, s.Lead) : 0;
                var gap = progress[v] - progress[a];
                var p = Rules.PairFor(d, gap, bounty);
                if (Math.Abs(gap) > 1) continue;
                near++;
                var lethal = Rules.Hit(new HitInput(250, p, 40, 100, Melee.Knife, false, true, false));
                var plain = Rules.Hit(new HitInput(27, p, 100, 100, Melee.None, false, true, false));
                if (p.Multiplier != 1 || p.Lifesteal != 0 || p.OneHitMelee || p.SecondChance || lethal.Damage != 250 || lethal.SecondChanceUsed || lethal.OneHit || plain.Damage != 27) wrong++;
            }
    }
    Eq(wrong, 0, $"{near} of {pairs} pairs in random standings within a kill of each other: every hit exactly as the game made it - no boost, no bounty, no second chance, no one-hit knife");
}

// ---- one hit ----

{
    var c = Reference();
    var even = Pair.Even;
    var up4 = Rules.PairFor(c, 4, 0);    // the attacker 4 behind the victim: tier 4+'s +20% damage, 20% lifesteal
    var up7 = Rules.PairFor(c, 7, 0);    // 7 behind: +30% damage, the one-hit knife and Zeus
    var down4 = Rules.PairFor(c, -4, 0); // the attacker 4 ahead: x0.80 / 1.40, and the victim's second chance
    var down2 = Rules.PairFor(c, -2, 0); // 2 ahead: x0.90 / 1.20, no second chance
    HitInput H(float damage, Pair pair, int health = 100, int armor = 0, Melee melee = Melee.None, bool prot = false, bool left = true, bool zero = false) =>
        new(damage, pair, health, armor, melee, prot, left, zero);

    var plain = Rules.Hit(H(37, even));
    Check(!plain.Block && plain.Damage == 37 && !plain.SecondChanceUsed && !plain.OneHit, "no gap: the hit as the game made it");
    Near(Rules.Hit(H(100, up4)).Damage, 120, "+20% damage by the one 4 behind", 1e-3);
    Near(Rules.Hit(H(100, down4, health: 500)).Damage, 100 * 0.8 / 1.4, "the one 4 ahead: the tier's health and damage taken together", 1e-3);
    Near(Rules.Hit(H(100, Rules.PairFor(c, 4, 0.2))).Damage, 144, "the one 4 behind on the leader with a +20% bounty", 1e-3);
    Near(Rules.Hit(H(100, Rules.PairFor(c, 1, 0))).Damage, 100, "a kill apart: as the game made it", 1e-3);
    Check(Rules.Hit(H(50, down4, prot: true)).Block, "extra spawn protection blocks the hit");

    var knife = Rules.Hit(H(40, up7, health: 100, armor: 100, melee: Melee.Knife));
    Check(knife.OneHit && knife.Damage >= 4 * 200, "a knife 7 behind kills through armour");
    var zeus = Rules.Hit(H(40, up7, health: 1000, armor: 100, melee: Melee.Taser));
    Check(zeus.OneHit && zeus.Damage >= 4 * 1100, "a Zeus 7 behind kills a player with a map's own 1,000 health");
    Check(!Rules.Hit(H(40, up4, melee: Melee.Knife)).OneHit, "a knife 4 behind is a normal knife");
    Check(!Rules.Hit(H(40, Rules.PairFor(c, -7, 0), melee: Melee.Knife)).OneHit, "the one 7 ahead has no one-hit knife on the one behind");
    Check(!Rules.Hit(H(40, up7, melee: Melee.None)).OneHit, "a gun 7 behind is not one-hit");

    var saved = Rules.Hit(H(200, down4, health: 50));
    Check(saved.SecondChanceUsed && saved.Damage == 49, "second chance: a lethal hit by a player 4 ahead leaves 1 health");
    var notLethal = Rules.Hit(H(30, down4, health: 50));
    Check(!notLethal.SecondChanceUsed && Math.Abs(notLethal.Damage - 30 * 0.8 / 1.4) < 1e-3, "second chance: not spent on a hit that does not kill");
    Check(!Rules.Hit(H(200, down4, health: 50, left: false)).SecondChanceUsed, "second chance: once per life");
    Check(!Rules.Hit(H(200, down2, health: 50)).SecondChanceUsed, "second chance: not against a player only 2 ahead");
    Check(!Rules.Hit(H(200, up4, health: 50)).SecondChanceUsed, "second chance: never for the one ahead");
    var one = Rules.Hit(H(5, down4, health: 1));
    Check(one.SecondChanceUsed && one.Damage == 0, "second chance at 1 health: the hit does nothing");
    var zeroed = Rules.Hit(H(200, up7, health: 50, zero: true));
    Check(!zeroed.SecondChanceUsed && !zeroed.Block && zeroed.Damage == 200, "headshots only: a body bullet is the game's to zero, untouched");
    Check(Rules.Hit(H(0, up7, melee: Melee.Knife)).Damage == 0, "a hit the game already zeroed stays zero, even a one-hit knife");

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

    Eq(Rules.Lifesteal(up4.Lifesteal, 50, 100), 10, "20% lifesteal of 50");
    Eq(Rules.Lifesteal(up4.Lifesteal, 400, 100), 20, "lifesteal never more than the victim's whole health");
    Eq(Rules.Lifesteal(down4.Lifesteal, 50, 100), 0, "no lifesteal for the one ahead");
    Eq(Rules.Lifesteal(0.3, 1000, 1000), 300, "30% lifesteal of a 1,000-health victim");
    Near(Rules.PairFor(CatchUpConfig.Parse("{ \"Tiers\": [ { \"MinBehind\": 2, \"Lifesteal\": 0.5 } ] }"), 2, 0).Lifesteal, 0.5, "a tier's lifesteal up to half the damage");

    var t2 = Rules.ForGap(c, 2, 0);
    var t4 = Rules.ForGap(c, 4, 0);
    var t7 = Rules.ForGap(c, 7, 0);
    double carry = 0;
    Eq(Rules.Regen(t2, 4.9, 0.5, 100, ref carry), 0, "regeneration waits 5 s at 2+ behind the leader");
    var healed = 0;
    for (var i = 0; i < 4; i++) healed += Rules.Regen(t2, 5 + i * 0.5, 0.5, 100, ref carry);
    Eq(healed, 4, "2+: 2% of 100 a second is 4 in 2 s");
    carry = 0;
    healed = 0;
    for (var i = 0; i < 2; i++) healed += Rules.Regen(t7, 3 + i * 0.5, 0.5, 1000, ref carry);
    Eq(healed, 60, "7+: 6% of a map's own 1,000 a second is 60 in 1 s");
    carry = 0.7;
    Eq(Rules.Regen(t4, 1, 0.5, 100, ref carry), 0, "hurt again: nothing");
    Near(carry, 0, "hurt again: the fraction is dropped");

    Near(Rules.ProtectedUntil(100, 5, t4), 107, "4+: the game's 5 s and 2 s more");
    Near(Rules.ProtectedUntil(100, 5, t7), 108, "7+: 3 s more");
    Near(Rules.ProtectedUntil(100, 5, t2), 100, "2+: no extra protection");
}

// ---- the worked examples of 2026-11-08 (E1-E5): hits to kill from full health under the pair rule (Cs2Hits: armour and helmet on) ----

{
    var d = Defaults();
    void Example(string what, int killer, int victim, double victimBounty, string gun, int body, int head, bool secondChance = false)
    {
        var pair = Rules.PairFor(d, victim - killer, victimBounty);
        Eq((Cs2Hits.ToKill(gun, false, pair, secondChance: secondChance && pair.SecondChance), Cs2Hits.ToKill(gun, true, pair, secondChance: secondChance && pair.SecondChance)),
            (body, head), $"{what}: hits to the body and to the head");
    }
    // Two players far behind the leader and level with each other: until 5.8.0 each carried 220 health, took and dealt x0.84 and had a second
    // chance and 35% lifesteal - 12 M4A4 hits to the body and 4 to the head, 18 and 5 with a Glock. Now plain Counter-Strike.
    Example("E1 Ann and Ben, both 10 behind the leader, level (7 and 7): an M4A4 as in plain Counter-Strike", 7, 7, 0, "m4a4", 5, 2);
    Example("E2 Ben 8 behind on Cal 9 behind (23 and 22): a Glock as in plain Counter-Strike", 23, 22, 0, "glock", 8, 2);
    Example("E5 Dee 7 behind on Cal 9 behind (8 and 6): Cal's row 2 - a twentieth less - and a Galil still takes 5 and 2", 8, 6, 0, "galilar", 5, 2);
    // The leader and a player 8 behind him: as until 5.8.0 - the one behind is hard for the leader to kill, the leader easy for them.
    Example("E3 the leader, 7 ahead of second place (a 30% bounty), on Eve 8 behind (16 and 8): Eve's row 8 and her second chance, a UMP-45", 16, 8, 0, "ump45", 17, 5, secondChance: true);
    Example("E4 Dee 8 behind on the leader (9 and 17, his 30% bounty): her row 8, a Galil", 9, 17, 0.30, "galilar", 3, 1);
}

// ---- Vampire's numbers (bridge/Shared/Vampire.cs): its heal, and hits to kill with it, alone and with the pair rule ----

{
    var d = Defaults();
    Near(Vampire.ShareBeside(0), 0.10, "Vampire: a tenth of every hit");
    Near(Vampire.ShareBeside(0.35), 0.10, "...beside catch-up's 35% lifesteal still a tenth: 45% together");
    Near(Vampire.ShareBeside(0.45), 0.05, "...beside 45%, what is left of half");
    Near(Vampire.ShareBeside(0.5), 0, "...beside half, nothing");
    Near(Vampire.ShareBeside(0.7), 0, "...nothing below zero");
    Near(CatchUpConfig.MaxLifesteal, Vampire.HealCap, "catch-up's lifesteal has the same ceiling as the two together: half a hit");
    double carry = 0;
    var h = 50;
    for (var i = 0; i < 10; i++) h = Vampire.Hit(h, 14, Vampire.HitShare, ref carry);
    Eq(h, 64, "ten hits that each take 14 heal 14: the fractions carried");
    carry = 0;
    Eq(Vampire.Hit(145, 100, Vampire.HitShare, ref carry), 150, "never above the ceiling");
    Eq(Vampire.Hit(160, 100, Vampire.HitShare, ref carry), 160, "never a cut of a health above it");
    Eq((Vampire.Kill(100), Vampire.Kill(130), Vampire.Kill(150), Vampire.Kill(170)), (140, 150, 150, 170), "a kill: 40 more, up to 150, never a cut");
    Check(Vampire.SpawnHealth == 100 && Vampire.Cap == 150 && Vampire.HitPercent == 10 && Vampire.KillHeal == 40 && Vampire.HealCapPercent == 50,
        "Vampire: the game's 100 at spawn, a tenth of every hit, 40 a kill, up to 150, half a hit at most with lifesteal");

    (int, int, int, int) Four(Func<string, bool, int> hits) => (hits("ak47", false), hits("ak47", true), hits("glock", false), hits("glock", true));
    Cs2Hits.Fighter Fresh(bool vampire) => new() { Vampire = vampire };
    // A fresh player dies as in plain Counter-Strike: the AK-47 4 to the body or 1 to the head, the Glock 8 or 2. Until 5.8.0: 34, 9, 40 and 11.
    Eq(Four((gun, head) => Cs2Hits.ToKill(gun, head, Pair.Even)), (4, 1, 8, 2), "Vampire: a fresh player, AK body/head, Glock body/head");
    // Two players trading hits evenly: still 4, 1, 8, 2 - a tenth back does not save a life. Until 5.8.0 that fight never ended.
    Eq(Four((gun, head) => Cs2Hits.Trade(Fresh(true), Fresh(true), gun, head, Pair.Even, Pair.Even)), (4, 1, 8, 2), "Vampire: an even trade, both on Vampire");
    // A player Vampire fed to its ceiling lives through one headshot: 6, 2, 11, 3.
    Eq(Four((gun, head) => Cs2Hits.ToKill(gun, head, Pair.Even, health: Vampire.Cap)), (6, 2, 11, 3), "Vampire: a player fed to 150");
    // With the pair rule, the leader 8 ahead with a 30% bounty: he needs 14, 5, 27, 7 on the player behind (her row 8 and second chance), she 2, 1, 4, 1 on him.
    // (The fifth AK headshot is her second chance's: the third does 39 before armour against her 40 whole points, so it does not count as lethal
    // and the fourth is the one it saves - an analysis that compared fractions of a point counted 4.)
    Eq(Four((gun, head) => Cs2Hits.ToKill(gun, head, Rules.PairFor(d, -8, 0), secondChance: true)), (14, 5, 27, 7), "Vampire with catch-up: the leader on a player 8 behind");
    Eq(Four((gun, head) => Cs2Hits.ToKill(gun, head, Rules.PairFor(d, 8, 0.3))), (2, 1, 4, 1), "...the player 8 behind on the leader");
    // ...and in their trade, her 35% lifesteal and Vampire's tenth together stay within half a hit.
    var her = Rules.PairFor(d, 8, 0.3);
    Check(her.Lifesteal + Vampire.ShareBeside(her.Lifesteal) <= Vampire.HealCap + 1e-12, "...her heals together: 45%, within half");
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
    var swapped = new Ladder();
    swapped.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 31; i++) swapped.Kill(1, 2, gun[swapped.Held(1)], true);
    swapped.Kill(1, 2, "hegrenade", false);
    Eq(Place(swapped, 1), "16+0@16", "T8 ...even when the game hands out the golden knife before it reports that kill (no gun, one kill on the level)");

    // T9: a player who joins during a match starts at level 0 with the level-0 gun; their kills place nobody while that holds
    var j = new Ladder();
    j.StartMatch(new ulong[] { 1, 2 });
    j.Kill(1, 2, gun[0], true);
    Eq(Place(j, 9), "0+0@0 placed", "T9 joined mid-match: level 0, the level-0 gun, placed by guns");
    Eq(j.Kill(9, 2, gun[0], true), null, "T9 ...their first kill, with the level-0 gun, moves nobody");
    Eq(Place(j, 9), "0+1@0 placed", "T9 ...and counts");
    Eq(j.Notes, 0, "T9 ...no placement");

    // The counter's own mechanics, besides the game's rules.
    var l = new Ladder();
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
    var first = new Ladder();
    first.StartMatch(new ulong[] { 1, 2, 3 });
    for (var i = 0; i < 4; i++) first.Kill(1, 2, "hegrenade", true); // level 2 with no gun used yet
    first.TeamKill(1, 3, "hegrenade");
    first.Kill(1, 2, gun[2], true);
    Eq(first.RungOf(gun[2]), 2, "a gun first used after a team kill is learned on the rung in hand, not on the lower level");
    Eq(Place(first, 1), "1+1@2", "...and the kill counts on the lower level");

    var g = new Ladder();
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
    Eq(tkPlaced, ("team-killed with awp: down from level 1 + 0 to level 0 + 0, the level-1 gun in hand until death", "placed on level 1 by a awp kill (counted 0 + 0)"),
        "a late joiner's team kill with a known gun places them, then takes them down - the note apart from the line, as the log writes them");
    Eq(Place(g, 7), "0+0@1 placed", "...holding that gun until death");
    g.Untrust(1);
    Eq(g.State(1).Trusted, false, "a team change: placed by guns from now");
    g.Leave(9);
    Eq(g.Progress(9), 0, "gone: forgotten");
    g.Stop();
    Check(!g.Tracking && g.Rungs == 0 && g.Notes == 0, "stop: nothing counts until the next match");

    // A gun on two rungs (or a count gone wrong): it stops placing anybody, and the player counted since the start is not moved.
    var twice = new Ladder();
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

// ---- the counter's placement: the count nearest the one it had, and a rung counted players confirmed ----

{
    string[] gun = ["g3sg1", "awp", "aug", "ak47", "famas", "m4a1_silencer", "negev", "mp5sd", "mp7", "mp9", "revolver", "hkp2000", "glock", "elite", "sawedoff", "mag7"];
    Eq((Ladder.Place(5, 4), Ladder.Place(13, 4), Ladder.Place(8, 4), Ladder.Place(9, 4)), (0, 1, 0, 1),
        "placed on rung 4: none on it when counted below (2 + 1), the last before the level-up when above (6 + 1), the count's own when on it");
    var l = new Ladder();
    l.StartMatch(new ulong[] { 1, 2 });
    for (var i = 0; i < 10; i++) l.Kill(1, 2, gun[l.Held(1)], true); // 1 teaches rungs 0 to 4
    for (var i = 0; i < 5; i++) l.Kill(9, 2, "hegrenade", true);      // 9 joined late: counted 2 + 1
    var below = l.Kill(9, 2, gun[4], true);
    Check(below == "placed on level 4 by a famas kill (counted 2 + 1)" && Place(l, 9) == "4+1@4 placed",
        $"counted a kill below the game: placed on level 4 with none, then the kill counts - 4 + 1 ({below}; {Place(l, 9)})");
    for (var i = 0; i < 6; i++) l.Kill(8, 2, "hegrenade", true);      // 8 joined late: counted 3 + 0
    l.Kill(8, 2, "hegrenade", true);
    for (var i = 0; i < 6; i++) l.Kill(8, 2, "hegrenade", true);      // ...6 + 1
    var above = l.Kill(8, 2, gun[4], true);
    Check(above == "placed on level 4 by a famas kill (counted 6 + 1)" && Place(l, 8) == "5+0@5 placed",
        $"counted a kill above the game: placed on 4 with one, the kill finishing it - 5 + 0 ({above}; {Place(l, 8)})");

    // A gun counted players used twice on its rung: a counted player's kill with it on another rung is their count being off.
    var cf = new Ladder();
    cf.StartMatch(new ulong[] { 1, 2, 3 });
    for (var i = 0; i < 4; i++) cf.Kill(1, 2, gun[cf.Held(1)], true); // 1: rung 0's gun twice, rung 1's twice
    cf.Kill(3, 2, gun[0], true);                                       // 3 counted at 0 + 1 - and the game gives them a kill the log never shows
    var off = cf.Kill(3, 2, gun[1], true);                             // 3 kills with rung 1's gun: the game has them on rung 1
    Check(off != null && off.StartsWith("placed on level 1 by a awp kill, a rung 2 counted kills confirmed (counted 0 + 1)") && Place(cf, 3) == "1+1@1 placed" && cf.RungOf(gun[1]) == 1,
        $"...placed on that rung, the rung kept ({off}; {Place(cf, 3)})");
    // With one kill on it only, the rung and the count weigh the same: the gun is forgotten, as before (see 'A gun on two rungs').
    var one = new Ladder();
    one.StartMatch(new ulong[] { 1, 2, 3 });
    one.Kill(1, 2, gun[0], true);
    one.Kill(1, 2, gun[0], true);
    one.Kill(1, 2, gun[1], true);                                      // rung 1's gun learned on one kill
    one.Kill(3, 2, gun[0], true);
    var clash = one.Kill(3, 2, gun[1], true);
    Check(clash != null && clash.Contains("two rungs") && one.RungOf(gun[1]) == -1 && Place(one, 3) == "1+0@1 placed", $"...one kill on the rung: forgotten ({clash})");
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
        var counter = new Ladder();
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

// ---- the counter against the match of 2026-11-08 (armsrace-2026-11-08.txt): one player's count a kill short from early on, brought back ----

{
    var lines = File.ReadAllLines(Path.Combine(here, "armsrace-2026-11-08.txt")).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => x.Split(' ')).ToList();
    var head = lines[0];
    var events = lines.Skip(1).ToList();
    Check(head[0] == "match" && head[1] == "cs2_mansion", "2026-11-08: one match");
    var ladder = head[2].Split(',');
    var counted = head[3].Split(',');
    Eq(events.Count, 246, "2026-11-08: 246 kills");
    Check(events.All(e => e.Length == 5 && e[0] is "P1" or "P2" or "P3" or "P4" or "P5" or "P6" && e[2] is "P1" or "P2" or "P3" or "P4" or "P5" or "P6"),
        "the fixture names nobody: players are P1-P6");
    Eq(events.Count(e => e[1] == e[3]), 14, "...with 14 team kills");
    foreach (var everybody in new[] { false, true })
    {
        var names = events.SelectMany(e => new[] { e[0], e[2] }).Distinct().ToList();
        ulong Id(string n) => (ulong)(names.IndexOf(n) + 1);
        var counter = new Ladder();
        counter.StartMatch(names.Where(n => everybody || counted.Contains(n)).Select(Id));
        int guns = 0, onRung = 0, firstOff = -1, lastOff = -1;
        string? firstOffBy = null;
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            ulong killer = Id(e[0]), victim = Id(e[2]);
            var kind = Weapons.Kind(e[4]);
            var held = counter.Held(killer);
            if (kind == WeaponKind.Gun)
            {
                guns++;
                if (Array.IndexOf(ladder, e[4]) == held) onRung++;
                else
                {
                    if (firstOff < 0) (firstOff, firstOffBy) = (i, e[0]);
                    lastOff = i;
                }
            }
            if (e[1] == e[3]) counter.TeamKill(killer, victim, e[4]);
            else counter.Kill(killer, victim, e[4], held < GoldenKnife);
        }
        var how = everybody ? "everybody counted from the start" : "as the plugin followed them";
        Eq(guns, 223, $"2026-11-08: its gun kills ({how})");
        // The one gun kill off its rung is the one that shows the kill the log lacks: P3's awp kill, the counter still at level 0. Every gun kill after
        // it lands, where keeping the kills on the level put 30 of them a rung off, half too low and half too high.
        Eq(onRung, 222, $"2026-11-08: 222 of 223 gun kills made with the gun the counter has in the killer's hands ({how})");
        Check(firstOff == lastOff && firstOffBy == "P3" && events[firstOff][4] == "awp", $"...the one that is not: P3's awp kill that shows the missing kill ({how}; event {firstOff + 1})");
        var standings = counter.Standings(names.Select(Id));
        Check(events[^1][0] == head[4] && events[^1][4].StartsWith("knife") && standings.Leader == Id(head[4]) && standings.Top == 2 * (GoldenKnife + 1),
            $"...and the match ends on {head[4]}'s knife kill on the golden knife, the counter one level past it ({how})");
    }
}

// ---- each player's own boost decided again after every kill ----

{
    var c = Defaults();
    // A life that spawned 8 behind (leader 1 at 20, 5 ahead of second: a 20% bounty): curve B's 8+.
    var spawn = Rules.Decide(c, Stand((1, 20), (2, 15), (3, 12), (4, 10)), 3);
    Check(spawn is { Tier: 8, InfiniteAmmo: true, ExtraProtection: 3, RegenPerSecond: 0.08, RegenDelaySeconds: 2, SecondChance: true, OneHitMelee: true },
        "spawned 8 behind: tier 8+, its extras and, against the leader, its fight");
    // It shrinks after the player's own kill: 7 behind. Their own extras follow; the spawn protection stays the spawn's.
    var (b7, ch7) = Rules.Recalculate(c, Stand((1, 20), (2, 15), (3, 13), (4, 10)), 3, false, spawn);
    Check(b7 is { Tier: 7, InfiniteAmmo: true, ExtraProtection: 3, RegenPerSecond: 0.08 } && ch7, "their own kill, 7 behind: tier 7+, logged");
    var (b5, ch5) = Rules.Recalculate(c, Stand((1, 20), (3, 15)), 3, false, b7);
    Check(b5 is { Tier: 5, InfiniteAmmo: false, ExtraProtection: 3, RegenPerSecond: 0.05, RegenDelaySeconds: 3 } && ch5,
        "5 behind: tier 5+, no infinite ammo, regeneration 5%/s after 3 s, the spawn's protection kept");
    var (lead2, chl) = Rules.Recalculate(c, Stand((1, 20), (3, 22)), 3, false, b5);
    Check(lead2 is { IsBounty: true, Bounty: 0.10, Lead: 2, Tier: 0, RegenPerSecond: 0, InfiniteAmmo: false } && chl && lead2.Step == "bounty +10%",
        "the lead by 2: the leader's +10% bounty, nothing else");
    var (tie, cht) = Rules.Recalculate(c, Stand((1, 22), (3, 22)), 3, false, lead2);
    Check(!tie.IsBounty && !tie.IsBoost && tie.Step == "no boost" && cht, "a tie at the top: no boost, no bounty, logged");
    var grow = Rules.Decide(c, Stand((1, 14), (2, 6)), 2);
    var (same, chs) = Rules.Recalculate(c, Stand((1, 15), (2, 6)), 2, false, grow);
    Check(grow.Tier == 8 && same.Tier == 8 && !chs, "9 behind after 8: still tier 8+, nothing to log");
    double carry = 0;
    Eq(Rules.Regen(lead2, 10, 1, 100, ref carry), 0, "the leader regenerates nothing");
    Eq(Rules.Regen(b7, 10, 1, 100, ref carry), 8, "...7 behind, 8% a second of the spawn health");

    // The log's lines: a spawn's, and one only at a change of tier or of the bounty's step.
    Eq(Words.Decision("P3", spawn, Stand((1, 20), (2, 15), (3, 12), (4, 10)), "P1"),
        "P3 spawns 8 kills behind (leader P1 20, them 12, 4 players): tier 8+: against players ahead up to +40% damage, -73% damage taken, 35% lifesteal, second chance, one-hit knife and Zeus; regeneration 8%/s after 2 s, +3 s spawn protection, infinite ammo; duel factor 7.14",
        "a spawn's line: the fight against the players ahead, the player's own extras, and the duel factor with the leader's 20% bounty (5.95 x 1.2)");
    Eq(Words.Change("P3", spawn, b7, Stand((1, 20), (2, 15), (3, 13), (4, 10)), "P1"),
        "P3's boost changes mid-life, tier 8+ -> tier 7+: against players ahead up to +35% damage, -67% damage taken, 25% lifesteal, second chance, one-hit knife and Zeus; regeneration 8%/s after 2 s, infinite ammo; duel factor 5.48 - 7 kills behind (leader P1 20, them 13, 4 players)",
        "the line for a boost that shrinks (4.57 x 1.2)");
    var lead4 = Rules.Recalculate(c, Stand((1, 20), (3, 24)), 3, false, b7).Boost;
    Eq(Words.Change("P3", b7, lead4, Stand((1, 20), (3, 24)), null),
        "P3's boost changes mid-life, tier 7+ -> bounty +20%: bounty +20% damage taken from enemies - leads by 4 kills (24 to 20, 2 players)",
        "...for a boosted player who takes the lead");
    Eq(Words.Change("P3", Boost.None, lead4, Stand((1, 20), (3, 24)), null),
        "P3's boost changes mid-life, no boost -> bounty +20%: bounty +20% damage taken from enemies - leads by 4 kills (24 to 20, 2 players)",
        "...and for one with nothing before: the standing last, never the shape of a spawn's line");
    var lead5 = Rules.Recalculate(c, Stand((1, 20), (3, 25)), 3, false, lead4);
    Check(!lead5.TierChanged, "a lead of 5: still +20%, nothing to log");
    Check(Rules.Recalculate(c, Stand((1, 20), (3, 26)), 3, false, lead5.Boost).TierChanged, "a lead of 6: +30%, logged");
}

// ---- the message: the card's rows, and the centre panel of an older HUD ----

{
    var c = Reference();
    var d = Defaults();
    var worstLines = 0;
    var worstWidth = 0;
    foreach (var cfg in new[] { c, d })
        for (var behind = 2; behind <= 60; behind++)
        {
            var b = Rules.ForGap(cfg, behind, 0);
            var ls = Words.Lines(b);
            var card = Words.Card(b);
            if (!b.IsBoost) { Check(ls.Length == 0 && card.Length == 0, "no boost: no message"); continue; }
            worstLines = Math.Max(worstLines, ls.Length);
            worstWidth = Math.Max(worstWidth, ls.Max(x => x.Length));
            Check(ls.Length <= Words.MaxLines && ls.All(x => x.Length <= Words.LineWidth),
                $"{behind} behind: the panel fits {Words.MaxLines} lines of {Words.LineWidth}: {string.Join(" | ", ls)}");
            Check(ls[0].StartsWith($"{behind} "), $"{behind} behind: the panel says how many kills behind first");
            Check(card.Length is > 0 and <= Words.CardRows && card.All(x => x.Length <= Words.CardWidth) && card[0] == Words.Against,
                $"{behind} behind: the card fits {Words.CardRows} rows of {Words.CardWidth}, against the players ahead first: {string.Join(" | ", card)}");
        }
    Check(worstLines <= 5 && worstWidth <= 28, $"the tallest panel {worstLines} lines, the widest line {worstWidth}");
    for (var lead = 2; lead <= 30; lead++)
    {
        var boost = new Boost { Lead = lead, Bounty = Rules.BountyFor(c.Bounty, lead) };
        var ls = Words.Lines(boost);
        Check(ls.Length is >= 1 and <= 5 && ls.All(x => x.Length <= 28) && ls[0] == $"You lead by {lead} kills:" && Words.Card(boost).Length == 1,
            $"the leader's message at a lead of {lead}");
    }

    Eq(string.Join(" | ", Words.Card(Rules.ForGap(d, 8, 0))),
        "Against players ahead of you, up to: | +40% damage, -73% damage taken | 35% lifesteal, a second chance | One-hit knife and Zeus | Regeneration, +3 s spawn protection | Infinite ammo",
        "curve B, 8 behind: the card");
    Eq(string.Join(" | ", Words.Card(Rules.ForGap(d, 2, 0))), "Against players ahead of you, up to: | -5% damage taken, 5% lifesteal | Regeneration",
        "curve B, 2 behind: the card");
    Eq(string.Join(" | ", Words.Card(new Boost { Lead = 6, Bounty = 0.3 })), "+30% damage taken (a bounty)", "the leader's card");
    Eq(string.Join(" | ", Words.Lines(Rules.ForGap(d, 8, 0))), "8 behind, vs players ahead: | +40% dmg, -73% dmg taken, | 35% lifesteal, 2nd chance, | 1-hit knife/zeus, regen, | +3s protection, no reloads",
        "curve B, 8 behind: the panel, short words, the reason inline");
    Eq(string.Join(" | ", Words.Lines(Rules.ForGap(d, 2, 0))), "2 kills behind the leader: | against players ahead: | -5% damage taken, | 5% lifesteal, regeneration",
        "curve B, 2 behind: the panel in long words");
    Eq(string.Join(" | ", Words.Lines(new Boost { Lead = 4, Bounty = 0.2 })), "You lead by 4 kills: | +20% damage taken (a bounty)", "the leader's panel");

    var two = Words.Lines(Rules.ForGap(d, 2, 0));
    var html = Words.Html(two, Rules.ForGap(d, 2, 0));
    Check(html.StartsWith("<font color='#FFD700'><b>2 kills behind the leader:</b></font><br>"), "the reason gold and bold");
    Eq(html.Split("<br>").Length, two.Length, "one <br> between lines");
    var inline = Words.Html(Words.Lines(Rules.ForGap(d, 8, 0)), Rules.ForGap(d, 8, 0));
    Check(inline.StartsWith("<font color='#FFD700'><b>8 behind, vs players ahead:</b></font><br>+40% dmg,"), "an inline reason is gold and bold too");
    var leader = Words.Html(Words.Lines(new Boost { Lead = 6, Bounty = 0.3 }), new Boost { Lead = 6, Bounty = 0.3 });
    Check(leader.StartsWith("<font color='#FF6347'><b>You lead by 6 kills:</b></font>"), "the leader's message in red");
    Check(!html.Contains('&') && !html.Contains("<script"), "nothing in it to escape");
}

// ---- a life's line in the log, so the next fight can be checked ----

{
    var d = Defaults();
    var t = new LifeTally { Shots = 12, HitsLanded = 5, Lifesteal = 10, Regen = 4, Vampire = 8, SecondChance = true };
    t.Took(3, "Ann", true, 47, 7, 1.0);
    t.Took(3, "Ann", false, 14, 8, 1.0);
    t.Took(5, "Ben", false, 23, 4, 0.27);
    Eq(Words.Life("Cat", "CT", 34.2, Rules.ForGap(d, 8, 0), t, "Ann", "glock", true),
        "Cat (CT, 34.2 s, tier 8+, 12 shots, 5 hits on enemies) took 3 hits (1 to the head): 84 health and 19 armour - Ann 2 hits (1 to the head), 61 health and 15 armour, x1.00; Ben 1 hit, 23 health and 4 armour, x0.27; healed 22 (lifesteal 10, regeneration 4, Vampire 8); second chance used; killed by Ann with glock",
        "a life's line: every attacker's hits, head, health and armour, catch-up's multiplier; the heals; how it ended");
    var moved = new LifeTally();
    moved.Took(7, "Dee", false, 20, 5, 1.0);
    moved.Took(7, "Dee", false, 27, 0, 1.4);
    Eq(Words.Life("Eve", "T", 2.0, Boost.None, moved, null, "world", false),
        "Eve (T, 2.0 s, 0 shots, 0 hits on enemies) took 2 hits: 47 health and 5 armour - Dee 2 hits, 47 health and 5 armour, x1.00-1.40; died without an enemy (world)",
        "...a multiplier that moved during the life, and a death without an enemy");
    Eq(Words.Life("Eve", "T", 1.5, new Boost { Lead = 4, Bounty = 0.2 }, new LifeTally(), "Fay", "awp", false),
        "Eve (T, 1.5 s, bounty +20%, 0 shots, 0 hits on enemies) took 0 hits: 0 health and 0 armour; team-killed by Fay with awp",
        "...a leader's life, ended by a teammate");
}

// ---- when the message comes ----

{
    Near(Rules.MessageDelay(false), 0.25, "a spawn: right after the boost");
    Near(Rules.MessageDelay(true), 3.0, "the first spawn after joining: after the join tagline (3 s)");
}

// ---- the table, as built (printed with --table) ----

if (printTable)
{
    var c = Defaults();
    Console.WriteLine("The fight between two players g kills apart (catchup.json): the one behind's hits, the one ahead's, lifesteal, the extras");
    Console.WriteLine("  gap | behind on ahead | ahead on behind | lifesteal | 2nd chance | 1-hit knife/zeus | duel factor");
    foreach (var t in c.Tiers)
    {
        var (up, down) = (Rules.PairFor(c, t.MinBehind, 0), Rules.PairFor(c, -t.MinBehind, 0));
        Console.WriteLine($"  {t.MinBehind,2}+ | x{up.Multiplier:0.00}           | x{down.Multiplier:0.000}          | {up.Lifesteal * 100,3:0}%      | {(down.SecondChance ? "yes" : "no"),-10} | {(up.OneHitMelee ? "yes" : "no"),-16} | {Rules.Own(t):0.00}");
    }
    Console.WriteLine("Each player's own, by kills behind the leader: regeneration, extra spawn protection, infinite ammo");
    foreach (var t in c.Tiers)
        Console.WriteLine($"  {t.MinBehind,2}+ | {t.RegenPerSecond * 100:0}%/s after {t.RegenDelaySeconds:0} s | +{t.ExtraSpawnProtectionSeconds:0} s | {(t.InfiniteAmmo ? "infinite ammo" : "")}");
    Console.WriteLine("Bounty: lead 0-1 none, " + string.Join(", ", new[] { 2, 4, 6 }.Select(l => $"{l}+ +{Rules.BountyFor(c.Bounty, l) * 100:0}%")));
    Console.WriteLine("The card and the panel per tier:");
    foreach (var t in c.Tiers)
    {
        Console.WriteLine($"  {t.MinBehind}+ card: \"{string.Join(" | ", Words.Card(Rules.ForGap(c, t.MinBehind, 0)))}\"");
        Console.WriteLine($"  {t.MinBehind}+ panel: \"{string.Join(" | ", Words.Lines(Rules.ForGap(c, t.MinBehind, 0)))}\"");
    }
}

Console.WriteLine(failures == 0 ? $"CatchUp self-test: all {checks} checks passed" : $"CatchUp self-test: {failures} of {checks} checks FAILED");
return failures == 0 ? 0 : 1;
