// --selftest, part fourteen: every combination of the
// fun settings - the page's rows with every value each takes, the grenade drops and catch-up among them in Arms Race -
// walked through, the way the page, chat and Random make them. A combination is the union of its choices' convar writes; the walk sets each one on a stock
// server's values and checks that it reads back as made, that the page calls idle exactly what does nothing then, that it holds a Clash
// exactly where Fun.Clashes says (the one rule Random obeys), that Random hands out every clash-free combination and no clashing one, and
// that the page, chat and gg_fun refuse exactly the clicks that would make a clash. The live check on a test copy (every setting read back,
// the engine, bots playing) is the release's, in the client test's notes. FunPageSelfTest then holds what the page and the messages say about it:
// every section's capital, one message for a change, impossible choices greyed, a refusal at the button that was clicked.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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
                            if (Fun.Impossible(rows[i], c, Value) != (why != null)) Fault($"{rows[i].Id} {c.Id} greyed {why == null}, refused {why != null}");
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
                + $"clash only as the rule says ({clashing} with Moon and High), Random hands out each clash-free one and no clash, and {refusals} clicks that would clash are refused and greyed"
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
        Check("The first choice of the grenade drops and of catch-up is On", Fun.Find("drops")!.Normal.Id == "on" && Fun.Find("catchup")!.Normal.Id == "on");
        Check("The grenade drops and catch-up are fun settings of their own: effects on by default, Arms Race's alone, rows of the page like any other; an older content's fixed rows lit from them, its buttons the same choices",
            Fun.Find("drops") is { } dr && Fun.ArmsRaceOnly(dr) && Fun.IsEffect(Fun.Drops) && Fun.Find("catchup") is { } cu && Fun.ArmsRaceOnly(cu) && Fun.IsEffect(Fun.CatchUp)
            && Fun.EffectsOn.Any(e => e.Effect == Fun.Drops && e.Value == "1") && Fun.EffectsOn.Any(e => e.Effect == Fun.CatchUp && e.Value == "1")
            && HudMenu.FixedRowClasses(c => c == Fun.Drops ? "0" : "1", true) is var fr && fr.Single(x => x.Class == "nd-off").On && fr.Single(x => x.Class == "cu-on").On
            && HudMenu.FixedRowClasses(c => "1", false).All(x => !x.On)
            && HudMenu.FunClick(HudMenu.DropsOff) is { Setting.Id: "drops", Choice.Id: "off" } && HudMenu.FunClick(HudMenu.CatchUpOn) is { Setting.Id: "catchup", Choice.Id: "on" }
            && HudMenu.FunClick(HudMenu.FunButton(17, 1)) is { Setting.Id: "drops", Choice.Id: "on" } && HudMenu.FunClick(HudMenu.FunButton(18, 2)) is { Setting.Id: "catchup", Choice.Id: "off" }
            && HudMenu.FunClick(HudMenu.FunButton(17, 3)) == null && HudMenu.FunClick(HudMenu.FunButton(19, 1)) == null);
        FunPageSelfTest(Check);
    }

    /// <summary>The Fun page and its messages: one message for a change, every section's capital, impossible choices greyed, a refusal at the button that was clicked.</summary>
    static void FunPageSelfTest(Action<string, bool> Check)
    {
        var stock = Fun.Settings.SelectMany(s => s.Stock).GroupBy(x => x.ConVar, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        Func<string, string?> With(params (string ConVar, string Value)[] sets)
        {
            var live = new Dictionary<string, string>(stock, StringComparer.OrdinalIgnoreCase);
            foreach (var (convar, value) in sets) live[convar] = value;
            return convar => live.GetValueOrDefault(convar);
        }
        var (stockValues, moonValues, highValues, bothValues) = (With(), With(("sv_gravity", "200")), With(("sv_jump_impulse", "450")), With(("sv_gravity", "200"), ("sv_jump_impulse", "450")));
        var (gravity, jump) = (Fun.Find("gravity")!, Fun.Find("jump")!);
        var (moon, high) = (Fun.Find(gravity, "moon")!, Fun.Find(jump, "high")!);
        var (gravityRow, jumpRow) = (Array.IndexOf(Fun.Settings, gravity) + 1, Array.IndexOf(Fun.Settings, jump) + 1);
        var (moonButton, highButton) = (HudMenu.FunButton(gravityRow, 3), HudMenu.FunButton(jumpRow, 2));

        // ---- one message for a change, the same for every setting ----
        string Line(string id, string choice) => M.Plain(Fun.ChangedLine("Remy", Fun.Find(id)!, Fun.Find(Fun.Find(id)!, choice)!));
        Check("A fun change's message, the same for every setting: who, what, and for how long - with a capital after the dash; a setting back at its default has nothing to wait for",
            Line("gravity", "moon") == "Remy set Gravity to Moon - Until the map changes" && Line("gravity", "normal") == "Remy put Gravity back to normal" && Line("jump", "high") == "Remy set Super jump to High - Until the map changes"
            && Line("jump", "normal") == "Remy put Super jump back to normal" && Line("vampire", "on") == "Remy turned Vampire on - Until the map changes" && Line("vampire", "off") == "Remy turned Vampire off"
            && Line("catchup", "off") == "Remy turned Catch-up off - Until the map changes" && Line("catchup", "on") == "Remy turned Catch-up on"
            && Line("drops", "off") == "Remy turned Grenade drops off - Until the map changes" && Line("drops", "on") == "Remy turned Grenade drops on");
        var palette = new M.Palette('p', 'c', 'a', 'v', 'r', 'd', name => name == "Remy" ? 't' : null);
        Check("...and in chat its parts take their functional colours: the name its team's, the setting an argument's, the choice a value's, the rest plain",
            M.Render(Fun.ChangedLine("Remy", gravity, moon), palette) == "tRemyp set aGravityp to vMoonp - Until the map changes"
            && M.Render(Fun.RandomLine("Remy", [(gravity, moon)]), palette) == "tRemyp rolled: agravityp vmoonp" && M.Render(Fun.DefaultLine("Remy"), palette) == "tRemyp put every fun setting back to normal");
        Check("Random's message says who and what: Bob rolled: gravity low, bunny hopping on, ice floor on, infinite ammo on",
            M.Plain(Fun.RandomLine("Bob", [(gravity, Fun.Find(gravity, "low")!), (Fun.Find("bhop")!, Fun.Find(Fun.Find("bhop")!, "on")!), (Fun.Find("ice")!, Fun.Find(Fun.Find("ice")!, "on")!), (Fun.Find("ammo")!, Fun.Find(Fun.Find("ammo")!, "on")!)]))
                == "Bob rolled: gravity low, bunny hopping on, ice floor on, infinite ammo on");
        Check("The fun lines' kinds in the catalogue: the top line, chat and the board, for everybody, in Fun's colour; a refused click has no line of its own on the page - chat and the board, for that player alone",
            new[] { Messages.FunChanged, Messages.FunRandom, Messages.FunDefault }.All(k => k is { Surface: Surface.Top, On: Who.Everybody, Chat: Who.Everybody, Board: Who.Everybody } && k.Cat == Messages.Fun && Messages.All.Contains(k))
            && Messages.FunRefused is { Surface: Surface.None, On: Who.Nobody, Chat: Who.Player, Board: Who.Player, News: false } && Messages.All.Contains(Messages.FunRefused));

        // ---- every section starts with a capital ----
        var words = new List<string>();
        foreach (var s in Fun.Settings)
            foreach (var c in s.Choices)
            {
                var values = With(c.Sets);
                words.Add(Fun.ChangedLine("Bob", s, c));
                words.AddRange(HudMenu.FunPage(values, true, _ => "Bob").Texts.Where(x => x.Id.EndsWith("_txt", StringComparison.Ordinal)).Select(x => x.Text));
                words.AddRange(HudMenu.FixedRowWords(values, _ => "Bob").Select(x => x.Text));
            }
        words.AddRange([Fun.RandomLine("Bob", []), Fun.DefaultLine("Bob"), HudMenu.FunPage(With(("sv_airaccelerate", "2000"))).Texts.Single(x => x.Id == $"{HudMenu.FunRow(3)}_txt").Text, HudMenu.FunNoteText,
            Fun.Refusal(jump, high, moonValues)!, Fun.Refusal(gravity, moon, highValues)!, Fun.RefusedJust(jump, high, moonValues, _ => "Cara")!, Fun.RefusedJust(gravity, moon, highValues, null)!]);
        var lower = words.Select(w => Sections.Lower(w)).Where(x => x != null).Distinct().ToList();
        Check($"Fun: every section of the {words.Count} lines and row words starts with a capital (Set by Bob, Until the map changes, This map runs its own){(lower.Count > 0 ? ": not " + string.Join(" | ", lower.Take(5)) : "")}",
            lower.Count == 0 && Sections.Lower(Fun.ChangedLine("cara", gravity, moon), ["cara"]) == null);

        // ---- impossible choices greyed, the reason in the row's variable ----
        (List<string> Grey, List<(string Name, string Text)> Why) Page(Func<string, string?> values) =>
            (HudMenu.FunPage(values).Classes.Where(x => x.Class == "off" && x.On).Select(x => x.Id).ToList(), HudMenu.FunWhy(values).Where(x => x.Text != "").ToList());
        var (reasonHigh, reasonMoon) = ("Super jump cannot be High while Gravity is Moon: together they throw players out of most maps", "Gravity cannot be Moon while Super jump is High: together they throw players out of most maps");
        Check("The page greys what cannot be made, for everybody, and its row's variable says why: Moon gravity greys Super jump's High, Super jump High greys Gravity's Moon; nothing on a stock server, and nothing while a map runs both (they are lit, as they are)",
            Page(moonValues).Grey.SequenceEqual([highButton]) && Page(moonValues).Why.SequenceEqual([(HudMenu.FunWhyVar(jumpRow), reasonHigh)])
            && Page(highValues).Grey.SequenceEqual([moonButton]) && Page(highValues).Why.SequenceEqual([(HudMenu.FunWhyVar(gravityRow), reasonMoon)])
            && HudMenu.FunRowOf(HudMenu.FunWhyVar(jumpRow)) == $"gg_f{jumpRow}"
            && Page(stockValues).Grey.Count == 0 && Page(stockValues).Why.Count == 0 && Page(bothValues).Grey.Count == 0 && Page(bothValues).Why.Count == 0
            && HudMenu.FunPage(stockValues).Classes.Count(x => x.Class == "off") == Fun.Rows * Fun.ChoicesPerRow && HudMenu.FunWhy(stockValues).Select(x => x.Name).SequenceEqual(Enumerable.Range(1, Fun.Rows).Select(r => $"osfw{r}")));

        // ---- what a click comes to ----
        var at = new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);
        var clicks = new FunClicks();
        var cara = (Func<string, string?>)(id => id == "gravity" ? "Cara" : null);
        Check("A choice not greyed is a go; the one showing is lit and asks nothing",
            clicks.Click(highButton, jump, high, stockValues, at) == FunClicks.Verdict.Go && clicks.Click(HudMenu.FunButton(jumpRow, 1), jump, jump.Normal, stockValues, at) == FunClicks.Verdict.Lit);
        clicks.Grey(highButton, true, at);
        Check($"Cara sets Moon: a click on Super jump's High before the grey can be seen is refused - also before a frame has noted the grey - and one {FunClicks.SeenSeconds:0} s on is ignored, the grey and the mouse's bubble having said it",
            clicks.Click(highButton, jump, high, moonValues, at.AddSeconds(FunClicks.SeenSeconds - 0.5)) == FunClicks.Verdict.Refused && new FunClicks().Click(highButton, jump, high, moonValues, at) == FunClicks.Verdict.Refused
            && clicks.Click(highButton, jump, high, moonValues, at.AddSeconds(FunClicks.SeenSeconds)) == FunClicks.Verdict.Seen && clicks.Click(HudMenu.FunButton(jumpRow, 1), jump, jump.Normal, moonValues, at.AddSeconds(5)) == FunClicks.Verdict.Lit);
        clicks.Grey(highButton, true, at.AddSeconds(5));
        var stillSince = clicks.Click(highButton, jump, high, moonValues, at.AddSeconds(5)) == FunClicks.Verdict.Seen;
        clicks.Grey(highButton, false, at.AddSeconds(6));
        Check("...a frame that finds it grey still does not restart its time; once it is no longer grey, the choice is a go again, and a new grey starts anew",
            stillSince && clicks.Click(highButton, jump, high, stockValues, at.AddSeconds(6)) == FunClicks.Verdict.Go && clicks.Click(highButton, jump, high, moonValues, at.AddSeconds(6)) == FunClicks.Verdict.Refused);
        Check("A refusal nobody could know in advance says who changed what: Cara just set Gravity to Moon, so Super jump cannot be High; with nobody known, what changed; nothing for a choice that may be made",
            M.Plain(Fun.RefusedJust(jump, high, moonValues, cara)) == "Cara just set Gravity to Moon, so Super jump cannot be High" && M.Plain(Fun.RefusedJust(jump, high, moonValues, null)) == "Gravity just went to Moon, so Super jump cannot be High"
            && M.Plain(Fun.RefusedJust(gravity, moon, highValues, id => id == "jump" ? "Bob" : null)) == "Bob just set Super jump to High, so Gravity cannot be Moon" && Fun.RefusedJust(gravity, Fun.Find(gravity, "low")!, highValues, cara) == null
            && M.Render(Fun.RefusedJust(jump, high, moonValues, cara)!, palette) == "rCarar just set aGravityr to vMoonr, so aSuper jumpr cannot be vHighr");

        // ---- the bubble at the button that was clicked, for that player, for 8 s ----
        var (one, other, later) = (3, 4, 5);
        var why = HudMenu.FunWhy(moonValues);
        var bubbles = new FunClicks();
        var quiet = bubbles.Classes(one, at).Count == 0 && bubbles.Words(later, HudMenu.FunWhy(stockValues), at).Count == 0;
        bubbles.Refuse(one, highButton, jump, Fun.RefusedJust(jump, high, moonValues, cara)!, at);
        var (jumpVar, refusedWords) = (HudMenu.FunWhyVar(jumpRow), "Cara just set Gravity to Moon, so Super jump cannot be High");
        Check($"The refused click: class refused on that button alone, for that player alone, and its row's variable says who changed what (no marks: a bubble has plain words), for {FunClicks.RefusedSeconds:0} s; then the plain reason, and the class off",
            quiet && bubbles.Classes(one, at.AddSeconds(FunClicks.RefusedSeconds - 0.5)).SequenceEqual([(highButton, "refused", true)]) && bubbles.Classes(other, at).Count == 0
            && bubbles.Words(one, why, at.AddSeconds(1)).SequenceEqual([(jumpVar, refusedWords)]) && bubbles.Words(other, why, at.AddSeconds(1)).SequenceEqual([(jumpVar, reasonHigh)])
            && bubbles.Classes(one, at.AddSeconds(FunClicks.RefusedSeconds)).SequenceEqual([(highButton, "refused", false)]) && bubbles.Words(one, why, at.AddSeconds(FunClicks.RefusedSeconds)).SequenceEqual([(jumpVar, reasonHigh)]));
        bubbles.Refuse(one, highButton, jump, "First", at);
        bubbles.Refuse(one, moonButton, gravity, "Second", at);
        var newest = bubbles.Classes(one, at).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        var replaced = newest.Single(x => x.Id == moonButton).On && !newest.Single(x => x.Id == highButton).On && bubbles.Words(one, HudMenu.FunWhy(stockValues), at).Contains((HudMenu.FunWhyVar(gravityRow), "Second"));
        bubbles.Refuse(later, highButton, jump, "Theirs", at);
        var theirs = bubbles.Words(later, HudMenu.FunWhy(stockValues), at).SequenceEqual([(jumpVar, "Theirs")]);
        bubbles.Gone(later);
        Check("A newer refusal moves the bubble; a player who leaves takes theirs along, and the next one in the slot gets the class off and the row's words emptied; a variable that never had words is not sent",
            replaced && theirs && bubbles.Classes(later, at).SequenceEqual([(highButton, "refused", false)]) && bubbles.Words(later, HudMenu.FunWhy(stockValues), at).SequenceEqual([(jumpVar, "")]));

        Check("A game on a content before v4.6.0 has neither grey nor bubble, so its note says it; v4.6.0 and a game whose content is not known do have them",
            HudVersion.IsBefore("v4.5.0", FunClicks.BubbleLevel) && HudVersion.IsBefore(HudVersion.Older, FunClicks.BubbleLevel) && !HudVersion.IsBefore("v4.6.0", FunClicks.BubbleLevel) && !HudVersion.IsBefore(HudVersion.Unknown, FunClicks.BubbleLevel) && !HudVersion.IsBefore("", FunClicks.BubbleLevel));

        // ---- what the layout holds for it ----
        if (File.Exists(HudLayoutFile) && File.Exists(HudStyleFile))
        {
            var xml = File.ReadAllText(HudLayoutFile);
            var css = File.ReadAllText(HudStyleFile);
            // No bubble at the button (it never showed in a real client, 2026-11-05): the reason is the row's own second line, which reads the row's variable.
            var missing = Enumerable.Range(1, Fun.Rows).SelectMany(r => Enumerable.Range(1, Fun.ChoicesPerRow).Where(k => !Regex.IsMatch(xml,
                $@"<Button id=""{HudMenu.FunButton(r, k)}""[^>]*>\s*<Label id=""{HudMenu.FunButton(r, k)}_txt""[^>]*/>\s*</Button>")).Select(k => HudMenu.FunButton(r, k))).ToList();
            var lineless = Enumerable.Range(1, Fun.Rows).Where(r => !Regex.IsMatch(xml,
                $@"<Label id=""gg_f{r}_txt"" class=""gg-frow-txt"" text=""\{{s:text\}}"" />\s*<Label class=""gg-fwhy"" text=""\{{s:{HudMenu.FunWhyVar(r)}\}}"" />")).Select(r => $"gg_f{r}").ToList();
            Check($"The layout: each of the page's {Fun.Rows * Fun.ChoicesPerRow} choices is a Button, and each row has its reason line, which reads its row's variable{(missing.Count + lineless.Count > 0 ? ": not " + string.Join(", ", missing.Concat(lineless).Take(6)) : "")}; the stylesheet greys off, shows the reason in place of the row's words under clash, and edges a refused click's button",
                missing.Count == 0 && lineless.Count == 0 && Regex.IsMatch(css, @"\.gg-funb\.off\s*\{") && Regex.IsMatch(css, @"\.gg-frow\.clash \.gg-fwhy\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(css, @"\.gg-frow\.clash \.gg-frow-txt\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"\.gg-funb\.refused\s*\{") && !css.Contains("gg-ftip"));
        }
        else Console.WriteLine($"skip the Fun page's layout check: {HudLayoutFile} or {HudStyleFile} not found");
    }
}
