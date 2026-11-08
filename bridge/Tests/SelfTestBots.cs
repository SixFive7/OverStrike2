// --selftest, the Bots page's part: its rows and their convars (Bots.cs), what a mode cannot make and why, the presets in every mode, a choice that names its player, the
// page as HudBots.cs draws it and a click on it, and the words its messages say.
static partial class Program
{
    static void BotsSelfTest(Action<string, bool> Check)
    {
        // ---- the rows ----
        Check($"Bots: {Bots.Rows} rows of two to {Bots.ChoicesPerRow} choices, their ids unique and none a Fun page row's, each one's first choice Normal - it sets nothing, so it shows the server's own",
            Bots.Settings.Length == Bots.Rows && Bots.Settings.All(s => s.Choices.Length >= 2 && s.Choices.Length <= Bots.ChoicesPerRow && s.Normal.Sets.Length == 0)
            && Bots.Settings.Select(s => s.Id).Distinct().Count() == Bots.Rows && Bots.Settings.All(s => Fun.Find(s.Id) == null && Bots.Find(s.Id) == s)
            && Bots.Settings.All(s => s.Choices.Select(c => c.Id).Distinct().Count() == s.Choices.Length));
        if (File.Exists(ConVarsFile))
        {
            var names = File.ReadAllLines(ConVarsFile).Select(l => l.Trim()).Where(l => l != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = Bots.Settings.SelectMany(Fun.ConVarsOf).Distinct().Where(c => !names.Contains(c)).ToList();
            Check($"Bots: every convar the page sets ({Bots.Settings.SelectMany(Fun.ConVarsOf).Distinct().Count()}) is one this CS2 build has (convars.txt){(unknown.Count > 0 ? ": not " + string.Join(", ", unknown) : "")}", unknown.Count == 0);
        }
        var bots = Bots.Find("bots")!;
        var fill = Bots.Find("botfill")!;
        Check("Bots: Bots and Fill to both set bot_quota and bot_quota_mode - so many beside the players (normal), or up to so many in all (fill) - so one of them shows a number at a time",
            bots.Choices.Skip(1).All(c => c.Sets.Contains(("bot_quota_mode", "normal"))) && fill.Choices.Skip(1).All(c => c.Sets.Contains(("bot_quota_mode", "fill")))
            && Fun.ConVarsOf(bots).Order().SequenceEqual(["bot_quota", "bot_quota_mode"]) && Fun.ConVarsOf(fill).Order().SequenceEqual(["bot_quota", "bot_quota_mode"]));
        var live = new Dictionary<string, string> { ["bot_quota"] = "4", ["bot_quota_mode"] = "normal" };
        Check("Bots: 4 beside the players shows on Bots, and Fill to shows Off; 8 in all with fill, the other way round",
            Fun.Showing(bots, live.GetValueOrDefault).Id == "4" && Fun.Showing(fill, live.GetValueOrDefault).Id == "off" && Bots.Quota(live.GetValueOrDefault) == 4
            && Fun.Showing(fill, new Dictionary<string, string> { ["bot_quota"] = "8", ["bot_quota_mode"] = "fill" }.GetValueOrDefault).Id == "8"
            && Fun.Showing(bots, new Dictionary<string, string> { ["bot_quota"] = "8", ["bot_quota_mode"] = "fill" }.GetValueOrDefault).Id == "off");

        Check("Bots: the side, difficulty, the weapon rules and the name tag are what a bot takes as it joins, so a change of one has the bots that play join again; the counts, the chatter and the behaviours work on them as they are",
            Bots.Settings.Where(Bots.Rejoins).Select(s => s.Id).Order().SequenceEqual(["botlevel", "bottag", "botteam", "botweapons"]));
        var funConVars = Fun.Settings.SelectMany(Fun.ConVarsOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var botOwners = Bots.Settings.SelectMany(s => Fun.ConVarsOf(s).Select(c => (c, s.Id))).GroupBy(x => x.c, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
        Check("Bots: no bot row touches a convar of the Fun page's, and no two touch one but the two counts (bot_quota and bot_quota_mode); each row's stock covers its convars",
            !Bots.Settings.SelectMany(Fun.ConVarsOf).Any(funConVars.Contains) && botOwners.SequenceEqual(["bot_quota", "bot_quota_mode"])
            && Bots.Settings.All(s => s.Stock.Select(x => x.ConVar).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(Fun.ConVarsOf(s))));

        // ---- Side: every bot on the side asked for (the live lab: Side T and Bots 4 against one player on CT put 2 bots in, under mp_limitteams 1) ----
        var side = Bots.Find("botteam")!;
        Check("Bots: Side T and CT put every bot on that side - bot_join_team, with the team-size limit and the team balance off (mp_limitteams 0, mp_autoteambalance 0); Any sets nothing",
            side.Choices.Select(c => c.Id).SequenceEqual(["any", "t", "ct"]) && side.Normal.Sets.Length == 0
            && side.Choices.Skip(1).All(c => c.Sets.Length == 3 && c.Sets.Contains(("mp_limitteams", "0")) && c.Sets.Contains(("mp_autoteambalance", "0")))
            && Fun.Find(side, "t")!.Sets.Contains(("bot_join_team", "T")) && Fun.Find(side, "ct")!.Sets.Contains(("bot_join_team", "CT")));
        var sideT = new Dictionary<string, string> { ["bot_join_team"] = "T", ["mp_limitteams"] = "0", ["mp_autoteambalance"] = "false" };
        var limitBack = new Dictionary<string, string> { ["bot_join_team"] = "T", ["mp_limitteams"] = "1", ["mp_autoteambalance"] = "true" };
        Check("Bots: Side shows T while all three hold (a bool convar reads false), and Any once the limit is back, whatever bot_join_team says",
            Fun.Showing(side, sideT.GetValueOrDefault).Id == "t" && Fun.Showing(side, limitBack.GetValueOrDefault).Id == "any");
        foreach (var cfg in new[] { ServerCfgFile, "../server/gamemode_competitive2v2_server.cfg" })
        {
            if (!File.Exists(cfg)) continue;
            var lines = File.ReadAllLines(cfg).Select(l => l.Split("//")[0].Trim()).Where(l => l != "").Select(l => l.Split(' ', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2).GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
            Check($"Bots: Side's stock is what {Path.GetFileName(cfg)} sets - the team-size limit and the team balance it puts back with Any",
                side.Stock.Where(x => x.ConVar.StartsWith("mp_", StringComparison.Ordinal)).All(x => lines.TryGetValue(x.ConVar, out var v) && Fun.Same(v, x.Value)));
        }

        // ---- the bots' damage guard (BotGuard): bot_ignore_players covers the bots' sight only, so Bot war's stray shots, grenades and fire are the guard's;
        // and the cloak hides a player from people, not from the bots (the live lab: a bot's one shot killed a player 19 s into the spawn cloak) ----
        var asked = 0;
        bool On() { asked++; return true; }
        bool Off() { asked++; return false; }
        Check("Bots: a bot's damage to a player is refused while Bot war is on, or while the cloak shields the player - Bot war's word first when both hold; with neither it lands",
            Bots.Spares(attackerIsBot: true, victimIsBot: false, On, Off) == Bots.Spare.BotWar && Bots.Spares(true, false, Off, On) == Bots.Spare.Cloak
            && Bots.Spares(true, false, On, On) == Bots.Spare.BotWar && Bots.Spares(true, false, Off, Off) == Bots.Spare.None);
        Check("Bots: a player's damage lands on everybody, a hidden player too, and a bot's on a bot - whatever Bot war and the cloak say",
            Bots.Spares(false, false, On, On) == Bots.Spare.None && Bots.Spares(false, true, On, On) == Bots.Spare.None && Bots.Spares(true, true, On, On) == Bots.Spare.None);
        asked = 0;
        Bots.Spares(false, true, On, On); Bots.Spares(true, true, On, On); Bots.Spares(false, false, On, On);
        var firstOnly = asked == 0;
        Bots.Spares(true, false, On, On);
        Check("Bots: the guard reads Bot war and the cloak only for a bot's damage to a player, and the cloak only when Bot war is off", firstOnly && asked == 1);
        Check("Bots: Bot war is on while bot_ignore_players is, as the live values have it (1 or true), and off otherwise",
            Bots.War(new Dictionary<string, string> { ["bot_ignore_players"] = "1" }.GetValueOrDefault) && Bots.War(new Dictionary<string, string> { ["bot_ignore_players"] = "true" }.GetValueOrDefault)
            && !Bots.War(new Dictionary<string, string> { ["bot_ignore_players"] = "0" }.GetValueOrDefault) && !Bots.War(_ => null));

        // ---- what a mode cannot make ----
        var weapons = Bots.Find("botweapons")!;
        Check("Bots: Arms Race greys every weapon rule - its ladder hands out the weapons, to the bots too - and lets Any stand; Deathmatch and Wingman take them all",
            weapons.Choices.Skip(1).All(c => Bots.Unavailable(weapons, c, Modes.ArmsRace) == "Arms Race's ladder hands out the weapons, to the bots too")
            && Bots.Unavailable(weapons, weapons.Normal, Modes.ArmsRace) == null && weapons.Choices.All(c => Bots.Unavailable(weapons, c, Modes.Deathmatch) == null && Bots.Unavailable(weapons, c, Modes.Wingman) == null));
        Check("Bots: Wingman's room for 4 players greys Bots past 3 and Fill to past 4; Arms Race and Deathmatch take every count",
            bots.Choices.Where(c => Bots.Unavailable(bots, c, Modes.Wingman) != null).Select(c => c.Id).SequenceEqual(["4", "8"])
            && fill.Choices.Where(c => Bots.Unavailable(fill, c, Modes.Wingman) != null).Select(c => c.Id).SequenceEqual(["8", "12", "16"])
            && Bots.Unavailable(bots, Fun.Find(bots, "4")!, Modes.Wingman) == "Wingman has room for 4 players, one of them yours"
            && new[] { Modes.ArmsRace, Modes.Deathmatch }.All(m => bots.Choices.Concat(fill.Choices).All(c => Bots.Unavailable(Bots.Settings.First(s => s.Choices.Contains(c)), c, m) == null)));
        Check("Bots: nothing else is greyed anywhere", Modes.All.All(m => Bots.Settings.Where(s => s != weapons && s != bots && s != fill).All(s => s.Choices.All(c => Bots.Unavailable(s, c, m) == null))));

        // ---- a choice that names its player ----
        var mimic = Bots.Find("botmimic")!;
        var on = Fun.Find(mimic, "on")!;
        var mine = Bots.For(on, 4);
        Check("Bots: Copycats is made for the player who clicks it - bot_mimic gets their entity index (slot 4: 5) - and shows On whoever it copies; the console's has no player",
            Bots.NeedsPlayer(on) && !Bots.NeedsPlayer(mine) && mine.Sets.SequenceEqual([("bot_mimic", "5")]) && mine.Id == "on" && Bots.For(Fun.Find(mimic, "off")!, 4) == mimic.Normal
            && Fun.Showing(mimic, new Dictionary<string, string> { ["bot_mimic"] = "5" }.GetValueOrDefault).Id == "on" && Fun.Showing(mimic, new Dictionary<string, string> { ["bot_mimic"] = "0" }.GetValueOrDefault).Id == "off");

        // ---- the presets ----
        Check($"Bots: {Bots.PresetCount} presets, each naming real rows and choices, each with a count",
            Bots.Presets.Length == Bots.PresetCount && Bots.Presets.Select(p => p.Id).Distinct().Count() == Bots.PresetCount
            && Bots.Presets.All(p => p.Picks.All(x => Bots.Find(x.Setting) is { } s && Fun.Find(s, x.Choice) != null) && p.Picks.Any(x => x.Setting is "bots" or "botfill")));
        var zombies = Bots.FindPreset("zombies")!;
        var dmPlan = Bots.Plan(zombies, Modes.Deathmatch)!;
        var arPlan = Bots.Plan(zombies, Modes.ArmsRace)!;
        Check("Bots: a preset is every bot setting - its picks, the rest back to Normal - the Normals first and the count last, so the bots join with the rest set",
            dmPlan.Count == Bots.Rows && dmPlan[^1] is { Setting.Id: "bots", Choice.Id: "8" } && dmPlan.TakeWhile(x => x.Choice == x.Setting.Normal).Count() == Bots.Rows - zombies.Picks.Length
            && dmPlan.Single(x => x.Setting.Id == "botweapons").Choice.Id == "knives");
        Check("Bots: in Arms Race a preset leaves its weapon rule out and stands (Zombie horde: eight expert zombies with the ladder's guns); in Wingman one whose bots do not fit is greyed, with its reason",
            arPlan.Single(x => x.Setting.Id == "botweapons").Choice == weapons.Normal && arPlan.Single(x => x.Setting.Id == "botlevel").Choice.Id == "expert"
            && Bots.Plan(zombies, Modes.Wingman) == null && Bots.Unavailable(zombies, Modes.Wingman) == "Wingman has room for 4 players, one of them yours"
            && Bots.Presets.Where(p => Bots.Plan(p, Modes.Wingman) != null).Select(p => p.Id).SequenceEqual(["duel"]) && Bots.Presets.All(p => Bots.Plan(p, Modes.ArmsRace) != null && Bots.Plan(p, Modes.Deathmatch) != null));
        var asPlanned = dmPlan.SelectMany(x => x.Choice.Sets.Select(set => (set.ConVar, Value: set.Value))).GroupBy(x => x.ConVar).ToDictionary(g => g.Key, g => g.Last().Value);
        string? Live(string name) => asPlanned.TryGetValue(name, out var v) ? v : Bots.Settings.SelectMany(s => s.Stock).FirstOrDefault(x => x.ConVar == name).Value;
        Check("Bots: once made, the preset shows lit, and no other", Bots.Showing(Live, Modes.Deathmatch) == zombies);

        // ---- the page, the same for everybody ----
        var (texts, classes) = HudBots.Page(live.GetValueOrDefault, Modes.Wingman, id => id == "bots" ? "Remy" : null);
        string Text(string id) => texts.Single(t => t.Id == id).Text;
        bool Class(string id, string cls) => classes.Any(c => c.Id == id && c.Class == cls && c.On);
        Check("Bots page: the note, each row's title and words - who set a setting off its default - its choices, the one showing lit",
            Text(HudBots.Note) == Bots.Note && Text("os_b1_title") == "Bots" && Text("os_b1_txt") == $"4 - Set by Remy  ·  {bots.Words}" && Text("os_b1_4_txt") == "4"
            && Class("os_b1_4", "on") && !Class("os_b1_1", "on") && Class("os_b1", "changed") && !Class("os_b2", "changed") && Class("os_b1_5", "used") && !Class("os_b3_4", "used"));
        Check("Bots page: what Wingman cannot make is greyed, the reason its row's second line; the presets whose bots do not fit greyed, their reason in their words; Kick all bots lit while bots play",
            Class("os_b1_5", "off") && !Class("os_b1_2", "off") && HudBots.Why(live.GetValueOrDefault, Modes.Wingman).Single(w => w.Name == "osbw1").Text == "Wingman has room for 4 players, one of them yours"
            && HudBots.Why(live.GetValueOrDefault, Modes.Deathmatch).All(w => w.Text == "") && Class("os_bp1", "off") && Text("os_bp1_txt") == "Wingman has room for 4 players, one of them yours"
            && !Class("os_bp2", "off") && Text("os_bp2_txt") == Bots.FindPreset("duel")!.Words && !Class(HudBots.Kick, "off")
            && HudBots.Page(_ => null, Modes.Deathmatch).Classes.Contains((HudBots.Kick, "off", true)));
        Check("Bots page: a click is a row's choice, a preset, Kick all bots or Default - and nothing else, not the board's buttons, not the page's labels",
            HudBots.Clicked("os_b1_4") is { Setting.Id: "bots", Choice.Id: "4" } && HudBots.Clicked("os_b10_2") is { Setting.Id: "botmimic", Choice.Id: "on" }
            && HudBots.Clicked("os_bp1") is { Preset.Id: "zombies" } && HudBots.Clicked(HudBots.Kick) is { All: Bots.KickWord } && HudBots.Clicked(HudBots.Default) is { All: Bots.DefaultWord }
            && new[] { "os_b1_6", "os_b13_1", "os_b3_4", "os_bp7", "os_bp0", HudBots.Note, "os_b_vup", "os_b1", "os_btn_close", "os_b1_title" }.All(id => HudBots.Clicked(id) == null));

        // ---- the words ----
        Check("Bots: a preset's message names what it set, and what the mode left out; Kick all bots and Default say who",
            M.Plain(Bots.PresetLine("Remy", zombies, arPlan.Where(x => x.Choice != x.Setting.Normal || x.Setting.Id == "botweapons").ToList(), Modes.ArmsRace))
                == $"Remy started Zombie horde: difficulty expert, chatter quiet, name tag zombie, bots 8 (weapons not in Arms Race) - {Fun.UntilMapChange}"
            && M.Plain(Bots.OffLine("Remy", Bots.KickWord)) == "Remy kicked every bot" && M.Plain(Bots.OffLine("Remy", Bots.DefaultWord)) == "Remy put every bot setting back to normal");
    }
}
