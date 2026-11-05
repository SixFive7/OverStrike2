// --selftest, part fourteen: every combination of the
// fun settings - the page's rows with every value each takes, the grenade drops and catch-up among them in Arms Race -
// walked through, the way the page, chat and Random make them. A combination is the union of its choices' convar writes; the walk sets each one on a stock
// server's values and checks that it reads back as made, that the page calls idle exactly what does nothing then, that it holds a Clash
// exactly where Fun.Clashes says (the one rule Random obeys), that Random hands out every clash-free combination and no clashing one, and
// that the page, chat and gg_fun refuse exactly the clicks that would make a clash. The live check on a test copy (every setting read back,
// the engine, bots playing) is the release's, in the client test's notes.
using System.Collections.Concurrent;

static partial class Program
{
    static void FunCombosSelfTest(Action<string, bool> Check)
    {
        // no two settings touch one convar: a combination never sets a convar twice, and each setting reads back by its own convars alone
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string>();
        foreach (var s in Fun.Settings)
            foreach (var c in Fun.ConVarsOf(s))
                if (!owner.TryAdd(c, s.Id)) shared.Add($"{c} ({owner[c]} and {s.Id})");
        Check($"Fun combinations: no two settings touch one convar ({owner.Count} convars{(shared.Count > 0 ? "; shared: " + string.Join(", ", shared) : "")}), so no combination writes one twice",
            shared.Count == 0 && Fun.Settings.All(s => s.Stock.Select(x => x.ConVar).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Fun.ConVarsOf(s))));

        var stock = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var names = stock.Keys.ToArray();
        var slot = names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var armsRace in new[] { true, false })
        {
            var rows = Fun.Settings.Where(s => armsRace || !Fun.ArmsRaceOnly(s)).ToArray();
            var lengths = rows.Select(s => s.Choices.Length).ToArray();
            var total = lengths.Aggregate(1L, (n, k) => n * k);
            var row = rows.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
            long walked = 0, clashing = 0, idleSeen = 0, refusals = 0;
            var faults = new ConcurrentQueue<string>();
            Parallel.For(0, total, () => (Values: new string?[names.Length], Pick: new int[lengths.Length], Counts: new long[4]), (n, _, local) =>
            {
                var (values, pick, counts) = local;
                var rest = n;
                for (var i = 0; i < lengths.Length; i++) { pick[i] = (int)(rest % lengths[i]); rest /= lengths[i]; }
                for (var i = 0; i < names.Length; i++) values[i] = stock[names[i]];
                for (var i = 0; i < rows.Length; i++)
                    foreach (var (convar, v) in rows[i].Choices[pick[i]].Sets) values[slot[convar]] = v;
                string? Value(string convar) => slot.TryGetValue(convar, out var k) ? values[k] : null;
                string Of(string id) => row.TryGetValue(id, out var k) ? rows[k].Choices[pick[k]].Id : Fun.Find(id)?.Normal.Id ?? "";
                string Combo() => string.Join(" ", rows.Select((s, i) => $"{s.Id} {s.Choices[pick[i]].Id}"));
                void Fault(string what) { if (faults.Count < 12) faults.Enqueue($"{what}: {Combo()}"); }
                counts[0]++;
                // it reads back as made: every row shows the choice made, and the live values are exactly the union of the choices' writes
                for (var i = 0; i < rows.Length; i++)
                    if (Fun.Showing(rows[i], Value) != rows[i].Choices[pick[i]]) Fault($"{rows[i].Id} reads back as {Fun.Showing(rows[i], Value).Id}");
                // the page calls idle exactly what does nothing: friendly fire's three while free-for-all is on, Instakill and Grenades x10 while friendly fire is off
                var ffaOn = Of("ffa") == "on";
                var ffOff = Of("ff") == "off";
                for (var i = 0; i < rows.Length; i++)
                {
                    var idle = Fun.IdleBecause(rows[i], Value) != null;
                    var nothing = rows[i].Id switch { "ff" => ffaOn, "instakill" or "ffnades" => ffaOn || ffOff, _ => false };
                    if (idle != nothing) Fault($"{rows[i].Id} idle {idle}, does nothing {nothing}");
                    if (idle) counts[2]++;
                }
                // the one rule: a Clash exactly where Moon gravity meets Super jump High
                var clash = Fun.ClashIn(Of);
                var moonHigh = Of("gravity") == "moon" && Of("jump") == "high";
                if ((clash != null) != moonHigh) Fault($"clash {clash != null}, moon with high jump {moonHigh}");
                if (clash != null) counts[1]++;
                // Random, its dice made to draw this combination: it hands it out as drawn unless it clashes, and then with the clash's other setting back to Normal.
                // Catch-up is drawn one in four off: its die says 0 for off, any of 1, 2, 3 for on.
                var d = 0;
                var draws = rows.Select((s, i) => s.Id == "catchup" ? (s.Choices[pick[i]].Id == "off" ? 0 : 1 + (int)(n % 3)) : pick[i]).ToArray();
                var got = Fun.Randomized(k => draws[d++], armsRace);
                if (got.Count != rows.Length) Fault($"Random drew {got.Count} settings of {rows.Length}");
                for (var i = 0; i < rows.Length && i < got.Count; i++)
                {
                    var want = clash != null && rows[i].Id == clash.Other ? rows[i].Normal : rows[i].Choices[pick[i]];
                    if (got[i].Setting != rows[i] || got[i].Choice != want) Fault($"Random drew {got[i].Setting.Id} {got[i].Choice.Id}");
                }
                if (Fun.ClashIn(id => got.FirstOrDefault(x => x.Setting.Id == id) is { Setting: not null } g ? g.Choice.Id : Fun.Find(id)?.Normal.Id ?? "") != null) Fault("Random handed out a clash");
                // a click from here (a clash-free state): refused exactly when it would make a clash, saying why
                if (clash == null)
                    for (var i = 0; i < rows.Length; i++)
                        foreach (var c in rows[i].Choices)
                        {
                            if (c == rows[i].Choices[pick[i]]) continue; // "already": no click
                            var k = i;
                            var makes = Fun.ClashIn(id => id == rows[k].Id ? c.Id : Of(id)) != null;
                            var why = Fun.Refusal(rows[i], c, Value);
                            if ((why != null) != makes || (why != null && !why.Contains("throw players out"))) Fault($"{rows[i].Id} {c.Id} refused {why != null}, makes a clash {makes}");
                            if (why != null) counts[3]++;
                        }
                return local;
            }, local =>
            {
                Interlocked.Add(ref walked, local.Counts[0]);
                Interlocked.Add(ref clashing, local.Counts[1]);
                Interlocked.Add(ref idleSeen, local.Counts[2]);
                Interlocked.Add(ref refusals, local.Counts[3]);
            });
            var mode = armsRace ? "Arms Race" : "Deathmatch and Wingman";
            Check($"Fun combinations, {mode}: all {walked} of {total} ({string.Join(" x ", lengths.Where(k => k > 1))}) read back as made, idle exactly where they do nothing ({idleSeen} idle rows), "
                + $"clash only as the rule says ({clashing} with Moon and High), Random hands out each clash-free one and no clash, and {refusals} clicks that would clash are refused"
                + (faults.IsEmpty ? "" : "; first faults: " + string.Join(" | ", faults)),
                walked == total && faults.IsEmpty && clashing == total / (3 * 2) && refusals > 0);
        }
        Check("The refusal says what, while what, and why: Super jump cannot be High while Gravity is Moon",
            Fun.Refusal(Fun.Find("jump")!, Fun.Find(Fun.Find("jump")!, "high")!, c => c == "sv_gravity" ? "200" : null) == "Super jump cannot be High while Gravity is Moon: together they throw players out of most maps"
            && Fun.Refusal(Fun.Find("gravity")!, Fun.Find(Fun.Find("gravity")!, "moon")!, c => c == "sv_jump_impulse" ? "450" : null) == "Gravity cannot be Moon while Super jump is High: together they throw players out of most maps"
            && Fun.Refusal(Fun.Find("gravity")!, Fun.Find(Fun.Find("gravity")!, "low")!, c => c == "sv_jump_impulse" ? "450" : null) == null);
        var jumpBack = Fun.InSafeOrder([(Fun.Find("gravity")!, Fun.Find(Fun.Find("gravity")!, "moon")!), (Fun.Find("jump")!, Fun.Find("jump")!.Normal)]);
        Check("Random makes its changes back to Normal first, so gg_fun meets no clash on the way (Super jump down before the gravity goes to Moon)",
            jumpBack[0].Setting.Id == "jump" && jumpBack[1].Setting.Id == "gravity");
        var defaults = Fun.Defaults(true);
        Check("Default: every row to its first choice - Normal, or the On of friendly fire's three, the grenade drops and catch-up - and in another mode only the rows it shows",
            defaults.Count == Fun.Settings.Length && defaults.All(x => x.Choice == x.Setting.Normal) && Fun.Find("drops")!.Normal.Id == "on" && Fun.Find("catchup")!.Normal.Id == "on"
            && Fun.Defaults(false).All(x => !Fun.ArmsRaceOnly(x.Setting)) && Fun.Defaults(false).Count < defaults.Count);
        Check("The grenade drops and catch-up are fun settings of their own: effects on by default, Arms Race's alone, their fixed rows lit from them",
            Fun.Find("drops") is { } dr && Fun.ArmsRaceOnly(dr) && Fun.IsEffect(Fun.Drops) && Fun.Find("catchup") is { } cu && Fun.ArmsRaceOnly(cu) && Fun.IsEffect(Fun.CatchUp)
            && Fun.EffectsOn.Any(e => e.Effect == Fun.Drops && e.Value == "1") && Fun.EffectsOn.Any(e => e.Effect == Fun.CatchUp && e.Value == "1")
            && HudVote.FixedRowClasses(c => c == Fun.Drops ? "0" : "1", true) is var fr && fr.Single(x => x.Class == "nd-off").On && fr.Single(x => x.Class == "cu-on").On
            && HudVote.FixedRowClasses(c => "1", false).All(x => !x.On)
            && HudMenu.FunClick(HudMenu.NadesOff) is { Setting.Id: "drops", Choice.Id: "off" } && HudMenu.FunClick(HudMenu.CatchUpOn) is { Setting.Id: "catchup", Choice.Id: "on" }
            && HudMenu.FunClick("gg_f17_1") == null);
    }
}
