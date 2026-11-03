// --selftest, part seven (2026-10-24): the menu's fun page (Fun.cs), the game-changers no map may set (BlockList.cs and the committed
// plugin/OverStrike2/blocked.txt), and the owner's rule 5a: the places players read the rules - the in-game How it works page, the
// in-game Help page and the Rules page - say the same as the code, number for number (docs/reference.md "Keeping the
// rules in sync"). Every number in those words must be one of the phrases RuleNumbers builds from the bridge's own constants.
using System.Text.RegularExpressions;

static partial class Program
{
    const string BlockedFile = "../plugin/OverStrike2/blocked.txt", ConVarsFile = "../plugin/OverStrike2/convars.txt",
        ServerCfgFile = "../server/gamemode_armsrace_server.cfg";

    /// <summary>Every phrase with a number in it that the rules may use, each made of the bridge's own constants. A number anywhere in
    /// How it works, Help, Rules or the start page that is not inside one of these fails --selftest - so a rule change
    /// changes the constant and all the words that state it together, or the selftest says which words still say the old number.</summary>
    /// <summary>The deployment's compose file, when OVERSTRIKE2_COMPOSE_FILE names it: the selftest then checks the mounts against it.
    /// The repository holds none.</summary>
    static string ComposeFile => Environment.GetEnvironmentVariable("OVERSTRIKE2_COMPOSE_FILE") ?? "";

    static string[] RuleNumbers()
    {
        var odds = (int)Math.Round(1 / TooBigChance);
        return
        [
            Guide.Name, // the server's name: not a rule's number
            $"#{FirstNewRank} and up", $"#1-#{FirstNewRank - 1}", $"1 random pick in {odds}", $"the other {odds - 1}", $"after {ResetEmptyMinutes} minutes with nobody on",
            $"{ResetEmptyMinutes} minutes with nobody on", $"the last {RecentCount} maps played", $"up to {UpvoteCap} Likes", $"(max {UpvoteCap})", $"up to {TopRankBoost:0}x",
            $"Up to {MaxRequestsEach} requests each", $"{ShortRoundMinutes} or {DefaultRoundMinutes} minutes",
            $"Grenades x{Fun.GrenadeShare}", $"does {Fun.GrenadeShare} times the damage", // friendly fire's grenades, on the Fun page
            $"Spawn with {Fun.VampireHealth} health", $"up to {Fun.VampireCap}", $"Invisible for {Fun.GhostSeconds:0} seconds", // Vampire and Ghost (#24)
            $"{MapVote.Seconds} seconds", $"{MapVote.Seconds} s", $"at least {MapVote.MinMaps} maps", $"{MapVote.MinMaps} maps", $"up to {Modes.Wingman.MaxPlayers} players", // the vote (VoteRound.cs), the modes (Modes.cs)
            $"waits {MidVoteCooldownMinutes} minutes", $"{MidVoteCooldownMinutes} minutes", // a vote a player started that kept the map
        ];
    }

    /// <summary>The digits left in a text once every RuleNumbers phrase is taken out: nothing, when every number it states is the code's.</summary>
    static List<string> StrayNumbers(string text)
    {
        var rest = Norm(text);
        foreach (var phrase in RuleNumbers().OrderByDescending(p => p.Length)) rest = rest.Replace(phrase, " ");
        return Regex.Matches(rest, @"[^.;:]{0,30}\d[^.;:]{0,30}").Select(m => m.Value.Trim()).ToList();
    }

    /// <summary>A cfg file's settings: the first word of each line and the second, comments and quotes left out.</summary>
    static Dictionary<string, string> ParseCfg(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split("//")[0].Trim();
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) d[parts[0].Trim('"')] = parts[1].Trim().Trim('"');
        }
        return d;
    }

    static void RulesSelfTest(Action<string, bool> Check)
    {
        // ---- the fun page: the settings ----
        Check($"Fun page: {Fun.Settings.Length} settings, 6 to {Fun.Rows} (the layout's rows), each with 2 or {Fun.ChoicesPerRow} choices, the first setting nothing (no player's change) - or, On by default, every convar of the setting to the server's own value",
            Fun.Settings.Length is >= 6 and <= Fun.Rows && Fun.Settings.All(s => s.Choices.Length is >= 2 and <= Fun.ChoicesPerRow && s.Choices.Skip(1).All(c => c.Sets.Length > 0)
                && (s.Normal.Sets.Length == 0 || (Fun.ConVarsOf(s).All(cv => s.Normal.Sets.Any(x => x.ConVar == cv)) && s.Normal.Sets.All(x => s.Stock.Any(y => y.ConVar == x.ConVar && Fun.Same(y.Value, x.Value)))))));
        var onByDefault = Fun.Settings.Where(s => s.Normal.Sets.Length > 0).ToList();
        Check($"Fun page: friendly fire's three are the settings On by default ({string.Join(", ", onByDefault.Select(s => s.Id))}), each On or Off; Instakill and Grenades x{Fun.GrenadeShare} need friendly fire on",
            onByDefault.Select(s => s.Id).SequenceEqual(["ff", "instakill", "ffnades"]) && onByDefault.All(s => s.Choices.Select(c => c.Label).SequenceEqual(["On", "Off"]))
            && Fun.Settings.Where(s => s.Needs?.Any(n => n.Setting == "ff") == true).Select(s => s.Id).SequenceEqual(["instakill", "ffnades"])
            && Fun.Settings.Where(s => s.Needs?.Any(n => n.Setting == "ffa" && n.Not) == true).Select(s => s.Id).SequenceEqual(["ff", "instakill", "ffnades"])); // #4
        Check("Fun page: a setting that needs another names one that exists, and a choice of it, never itself; its idle words fit the words' label",
            Fun.Settings.Where(s => s.Needs != null).All(s => s.Needs!.All(n => n.Setting != s.Id && Fun.Find(n.Setting) is { } other && Fun.Find(other, n.Choice) != null
                && other.Needs?.Any(m => m.Setting == s.Id) != true && n.Idle.Length is > 0 and <= 90 && !Regex.IsMatch(n.Idle, @"\d"))));
        Check("Fun page: ids plain and unique (they travel in gg_fun's arguments); titles, words and button labels fit their labels",
            Fun.Settings.Select(s => s.Id).Distinct().Count() == Fun.Settings.Length
            && Fun.Settings.All(s => Regex.IsMatch(s.Id, "^[a-z]+$") && s.Choices.All(c => Regex.IsMatch(c.Id, "^[a-z]+$")) && s.Choices.Select(c => c.Id).Distinct().Count() == s.Choices.Length
                && s.Title.Length <= 16 && s.Words.Length <= 90 && s.Choices.All(c => c.Label.Length <= 8)));
        Check("Fun page: every convar a choice sets has the server's own value in Stock (CS2's, or the server cfg's), and Stock names nothing else",
            Fun.Settings.All(s => Fun.ConVarsOf(s).All(cv => s.Stock.Any(x => x.ConVar == cv)) && s.Stock.All(x => Fun.ConVarsOf(s).Contains(x.ConVar))));
        // Since Over Strike 2 (the owner's #16 and #24) headshots only and infinite ammo are players' choices; Normal on the ammo is Arms Race's own 2.
        Check("Fun page: nothing the bridge or the server owns - the match length, the mode, cheats, the grenade drops; infinite ammo's Off is Arms Race's endless reserve (2)",
            !Fun.AllConVars.Any(cv => cv is "mp_roundtime" or "game_type" or "game_mode" or "sv_cheats" or "mp_death_drop_grenade")
            && Fun.Find("ammo") is { } ammo && ammo.Stock.Single(x => x.ConVar == "sv_infinite_ammo").Value == "2" && Fun.Find(ammo, "on")!.Sets.Single().Value == "1");
        if (File.Exists(ConVarsFile))
        {
            var known = File.ReadAllLines(ConVarsFile).Select(l => l.Trim()).ToHashSet();
            var unknown = Fun.AllConVars.Where(cv => !known.Contains(cv) && !Fun.IsEffect(cv)).ToList(); // the effects (os2_...) are the plugin's own switches
            Check($"Fun page: every convar it sets ({Fun.AllConVars.Count()}) is a game convar of this CS2 build (convars.txt){(unknown.Count > 0 ? ": not " + string.Join(", ", unknown) : "")}", unknown.Count == 0);
        }
        else Console.WriteLine($"skip the fun convars check: {ConVarsFile} not found");
        if (File.Exists(ServerCfgFile))
        {
            var cfg = ParseCfg(File.ReadAllText(ServerCfgFile));
            var named = Fun.Settings.SelectMany(s => s.Stock).Where(x => cfg.ContainsKey(x.ConVar)).ToList();
            var clash = named.Where(x => !Fun.Same(cfg[x.ConVar], x.Value)).Select(x => x.ConVar).ToList();
            Check($"Fun page: what the server's own cfg sets is what the page calls normal ({string.Join(", ", named.Select(x => $"{x.ConVar} {cfg[x.ConVar]}"))}){(clash.Count > 0 ? ": not " + string.Join(", ", clash) : "")}",
                clash.Count == 0);
            var unset = onByDefault.SelectMany(Fun.ConVarsOf).Where(cv => !cfg.ContainsKey(cv)).ToList();
            Check($"Fun page: what is On by default is the server cfg's, so every map load starts with it - friendly fire {Fun.FriendlyFireOn}, bullets and the rest {Fun.InstakillShare}, grenades {Fun.GrenadeShare}{(unset.Count > 0 ? ": the cfg does not set " + string.Join(", ", unset) : "")}",
                unset.Count == 0 && cfg.TryGetValue("mp_friendlyfire", out var ffCfg) && Fun.Same(ffCfg, Fun.FriendlyFireOn)
                && cfg.TryGetValue("ff_damage_reduction_bullets", out var bulletsCfg) && Fun.Same(bulletsCfg, Fun.InstakillShare)
                && cfg.TryGetValue("ff_damage_reduction_other", out var otherCfg) && Fun.Same(otherCfg, Fun.InstakillShare)
                && cfg.TryGetValue("ff_damage_reduction_grenade", out var grenadeCfg) && Fun.Same(grenadeCfg, Fun.GrenadeShare));
        }
        else Console.WriteLine($"skip the server cfg checks: {ServerCfgFile} not found");
        Check("Values compare as the game prints them: 301.993 is 301.993377, false is 0, TRUE is 1, 5.2 is 5.200000; 12 is not 100",
            Fun.Same("301.993", "301.993377") && Fun.Same("false", "0") && Fun.Same("TRUE", "1") && Fun.Same("5.2", "5.200000") && !Fun.Same("12", "100") && !Fun.Same("0", "1") && !Fun.Same(null, "1"));
        Check("The news line: who switched what, and that it lasts until the map changes",
            Fun.News("Remy", Fun.Find("gravity")!, Fun.Find(Fun.Find("gravity")!, "low")!) == "Remy switched gravity to Low - until the map changes"
            && Fun.News("Remy", Fun.Find("gravity")!, Fun.Find("gravity")!.Normal) == "Remy put gravity back to normal"
            && Fun.News("Remy", Fun.Find("ff")!, Fun.Find(Fun.Find("ff")!, "off")!) == "Remy switched friendly fire to Off - until the map changes"
            && Fun.News("Remy", Fun.Find("ff")!, Fun.Find("ff")!.Normal) == "Remy put friendly fire back to on"
            && Fun.Settings.All(s => s.Choices.All(c => Fun.News(new string('x', 32), s, c).Length <= NewsLength)));

        // ---- the fun page: a map's worth of player changes, on a dictionary standing in for the game's convars ----
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cv, value) in Fun.Settings.SelectMany(s => s.Stock)) live[cv] = value;
        live["sv_enablebunnyhopping"] = "false"; live["sv_autobunnyhopping"] = "false"; // the game prints a bool as true/false
        string? V(string cv) => live.TryGetValue(cv, out var v) ? v : null;
        void Do(List<(string ConVar, string Value)> writes) { foreach (var (cv, v) in writes) live[cv] = v; }
        var fun = new FunState();
        var gravity = Fun.Find("gravity")!;
        var bhop = Fun.Find("bhop")!;
        Check("Every setting starts at its first choice on a map that sets nothing", Fun.Settings.All(s => Fun.Showing(s, V) == s.Normal) && !Fun.AllConVars.Any(fun.Chosen));
        Do(fun.Apply(gravity, Fun.Find(gravity, "low")!, V));
        Check("Low gravity: sv_gravity 400, what it was remembered, and the convar a player's now (exempt from the block)",
            live["sv_gravity"] == "400" && fun.Before["sv_gravity"] == "800" && fun.Chosen("sv_gravity") && Fun.Showing(gravity, V).Id == "low");
        Do(fun.Apply(gravity, Fun.Find(gravity, "moon")!, V));
        Check("...Moon after Low keeps what it was before any player (800), not Low's 400", live["sv_gravity"] == "200" && fun.Before["sv_gravity"] == "800");
        Do(fun.Apply(gravity, gravity.Normal, V));
        Check("...and Normal puts that back and makes it the map's again", live["sv_gravity"] == "800" && !fun.Chosen("sv_gravity") && Fun.Showing(gravity, V) == gravity.Normal);
        live["sv_gravity"] = "132.8"; // fs_highsky's own
        Check("A map's own value that no choice has reads as Normal, and the page can say it is the map's own",
            Fun.Showing(gravity, V) == gravity.Normal && Fun.OwnValues(gravity, V) is [("sv_gravity", "132.8")]);
        Do(fun.Apply(gravity, Fun.Find(gravity, "low")!, V)); Do(fun.Apply(gravity, gravity.Normal, V));
        Check("...a player's detour through Low gives the map its own 132.8 back", live["sv_gravity"] == "132.8" && !fun.Chosen("sv_gravity"));
        live["sv_airaccelerate"] = "2000"; // a surf map's own
        Do(fun.Apply(bhop, Fun.Find(bhop, "on")!, V));
        Check("Bunny hopping On: hopping and no stamina, a surf map's own air acceleration (2000) left alone",
            live["sv_autobunnyhopping"] == "1" && live["sv_enablebunnyhopping"] == "1" && live["sv_staminajumpcost"] == "0" && live["sv_airaccelerate"] == "2000" && Fun.Showing(bhop, V).Id == "on");
        Do(fun.Apply(bhop, Fun.Find(bhop, "turbo")!, V));
        Check("...Turbo: air acceleration 100, and the page shows Turbo rather than On (the more specific choice)", live["sv_airaccelerate"] == "100" && Fun.Showing(bhop, V).Id == "turbo");
        Do(fun.Apply(bhop, Fun.Find(bhop, "on")!, V));
        Check("...back to On: the surf map's 2000 again, hopping still on", live["sv_airaccelerate"] == "2000" && live["sv_autobunnyhopping"] == "1" && Fun.Showing(bhop, V).Id == "on" && !fun.Chosen("sv_airaccelerate"));
        Do(fun.Apply(gravity, Fun.Find(gravity, "moon")!, V));
        Check("What players changed, for the console", fun.Describe(V) == "gravity moon, bhop on");
        Do(fun.Reset(V));
        Check("The reset (a map's end, or nobody on for 10 minutes): every player change back to how the map had it, nothing a player's any more",
            live["sv_gravity"] == "132.8" && live["sv_autobunnyhopping"] == "false" && live["sv_enablebunnyhopping"] == "false" && live["sv_staminajumpcost"] == "0.08"
            && live["sv_airaccelerate"] == "2000" && !Fun.AllConVars.Any(fun.Chosen) && fun.Reset(V).Count == 0 && fun.Describe(V) == "no player changes");
        var none = new FunState();
        Check("A choice that is so already writes nothing, and a convar the game does not have is never remembered",
            none.Apply(gravity, gravity.Normal, V).Count == 0 && none.Apply(Fun.Find("ffa")!, Fun.Find(Fun.Find("ffa")!, "on")!, _ => null) is [("mp_teammates_are_enemies", "1")] && !none.Chosen("mp_teammates_are_enemies"));

        // ---- friendly fire's three: On by default (the server cfg), Off a player's change, and the two that need it idle while it is off ----
        var ff = Fun.Find("ff")!;
        var instakill = Fun.Find("instakill")!;
        var ffnades = Fun.Find("ffnades")!;
        var team = new FunState();
        live["mp_friendlyfire"] = "true"; // a bool, as the game prints it
        Check($"Friendly fire, Instakill and Grenades x{Fun.GrenadeShare} start On on a map that sets nothing (the server cfg's values), and none is idle",
            new[] { ff, instakill, ffnades }.All(s => Fun.Showing(s, V) == s.Normal && s.Normal.Id == "on" && !Fun.Idle(s, V) && Fun.OwnValues(s, V).Count == 0));
        Do(team.Apply(ff, Fun.Find(ff, "off")!, V));
        Check("Friendly fire Off: mp_friendlyfire 0, a player's until the map changes - and the two that need it show idle, still On",
            live["mp_friendlyfire"] == "0" && team.Chosen("mp_friendlyfire") && Fun.Showing(ff, V).Id == "off" && Fun.Idle(instakill, V) && Fun.Idle(ffnades, V)
            && Fun.Showing(instakill, V).Id == "on" && Fun.Showing(ffnades, V).Id == "on");
        Do(team.Apply(instakill, Fun.Find(instakill, "off")!, V));
        Check("...Instakill Off while idle: bullets and the rest 1, an enemy's damage, ready for when friendly fire is back",
            live["ff_damage_reduction_bullets"] == "1" && live["ff_damage_reduction_other"] == "1" && Fun.Showing(instakill, V).Id == "off" && Fun.Idle(instakill, V));
        Do(team.Apply(ff, ff.Normal, V));
        Check("...friendly fire On again: 1, nothing idle, Instakill still Off", Fun.Same(live["mp_friendlyfire"], "1") && !Fun.Idle(instakill, V) && Fun.Showing(instakill, V).Id == "off");
        Do(team.Apply(ffnades, Fun.Find(ffnades, "off")!, V));
        Check($"Grenades x{Fun.GrenadeShare} Off: ff_damage_reduction_grenade 1", live["ff_damage_reduction_grenade"] == "1" && Fun.Showing(ffnades, V).Id == "off");
        Do(team.Reset(V));
        Check($"The reset puts all three back to the server's own (friendly fire on, {Fun.InstakillShare}, {Fun.GrenadeShare}), nothing a player's",
            Fun.Same(live["mp_friendlyfire"], "1") && live["ff_damage_reduction_bullets"] == Fun.InstakillShare && live["ff_damage_reduction_other"] == Fun.InstakillShare
            && live["ff_damage_reduction_grenade"] == Fun.GrenadeShare && !Fun.AllConVars.Any(team.Chosen));
        live["mp_friendlyfire"] = "false"; // one of the maps that turn friendly fire off themselves
        Check("A map that turns friendly fire off shows Off, and the two that need it idle", Fun.Showing(ff, V).Id == "off" && Fun.Idle(instakill, V) && Fun.Idle(ffnades, V));
        Do(team.Apply(ff, ff.Normal, V));
        Check("...a player's On there turns it on (On sets the cfg's value; it is not 'no change'), and the map's end gives the map its own off back",
            Fun.Same(live["mp_friendlyfire"], "1") && Fun.Showing(ff, V).Id == "on" && team.Reset(V) is [("mp_friendlyfire", "false")]);
        live["mp_friendlyfire"] = "1"; live["ff_damage_reduction_bullets"] = "0.33"; // a map's own share, which no choice has
        Check("A map's own share reads as On (no choice has it), and the page can say it is the map's own",
            Fun.Showing(instakill, V) == instakill.Normal && Fun.OwnValues(instakill, V) is [("ff_damage_reduction_bullets", "0.33")]);
        live["ff_damage_reduction_bullets"] = Fun.InstakillShare;

        // ---- the game-changers block: the file's format and the decision ----
        var parsed = BlockList.Parse("// a comment\n\nweapon_accuracy_nospread   // no spread\nsv_cheats 0 // cheats\r\nbad-name // x\nsv_cheats // again\na b c // three words\n", out var errors);
        Check("blocked.txt's format: a convar per line, an optional value, // and why; comments and blank lines skipped; a bad name, a third word or a second line for a name is an error",
            parsed.Count == 2 && parsed[0] == new BlockList.Entry("weapon_accuracy_nospread", null, "no spread") && parsed[1] == new BlockList.Entry("sv_cheats", "0", "cheats") && errors.Count == 3);
        var game = new Dictionary<string, string> { ["weapon_accuracy_nospread"] = "true", ["sv_cheats"] = "true", ["sv_disable_radar"] = "0", ["mp_teammates_are_enemies"] = "1", ["game_type"] = "0" };
        var baseline = new Dictionary<string, string> { ["weapon_accuracy_nospread"] = "false", ["sv_disable_radar"] = "0", ["mp_teammates_are_enemies"] = "false", ["sv_cheats"] = "false", ["game_type"] = "1" };
        var rules = BlockList.Parse("weapon_accuracy_nospread\nsv_cheats 0\nsv_disable_radar\nmp_teammates_are_enemies\ngame_type\nno_such_convar\n", out _);
        var due = BlockList.Due(rules, cv => game.GetValueOrDefault(cv), cv => baseline.GetValueOrDefault(cv), cv => cv == "mp_teammates_are_enemies");
        Check("The block puts back what a map changed - to the baseline, or the value the file gives - and leaves alone what is right already, a player's choice on the fun page, and a convar the game does not have",
            due.Select(d => $"{d.Entry.ConVar} {d.Now}->{d.Want}").SequenceEqual(["weapon_accuracy_nospread true->false", "sv_cheats true->0", "game_type 0->1"]));
        if (File.Exists(BlockedFile))
        {
            var committed = BlockList.Parse(File.ReadAllText(BlockedFile), out var badLines);
            Check($"blocked.txt ({committed.Count} game-changers) parses without an error, and every line says why", badLines.Count == 0 && committed.Count > 0 && committed.All(e => e.Why.Length >= 8));
            Check("blocked.txt holds what the owner named (12b): no-spread, the radar, the game mode and game type - and cheats",
                new[] { "weapon_accuracy_nospread", "sv_disable_radar", "game_type", "game_mode", "sv_cheats" }.All(n => committed.Any(e => e.ConVar == n)));
            var movement = committed.Where(e => BlockList.Movement.Contains(e.ConVar, StringComparer.OrdinalIgnoreCase)).Select(e => e.ConVar).ToList();
            Check($"blocked.txt leaves the movement settings bhop and surf maps need alone ({BlockList.Movement.Length}){(movement.Count > 0 ? ": but blocks " + string.Join(", ", movement) : "")}", movement.Count == 0);
            Check("blocked.txt leaves the bridge's match length and every plugin's own settings alone", !committed.Any(e => e.ConVar is "mp_roundtime" || e.ConVar.StartsWith("gg_") || e.ConVar.StartsWith("css_") || e.ConVar.StartsWith("mm_")));
            Check("blocked.txt leaves friendly fire to the maps (12b): a map that sets its own keeps it for its match", !committed.Any(e => e.ConVar is "mp_friendlyfire" || e.ConVar.StartsWith("ff_damage_")));
            if (File.Exists(ConVarsFile))
            {
                var known = File.ReadAllLines(ConVarsFile).Select(l => l.Trim()).ToHashSet();
                var unknown = committed.Where(e => !known.Contains(e.ConVar) && e.ConVar != "sv_cheats").Select(e => e.ConVar).ToList(); // sv_cheats is the engine's, not a game convar
                Check($"blocked.txt names only convars this CS2 build has (convars.txt, and sv_cheats){(unknown.Count > 0 ? ": not " + string.Join(", ", unknown) : "")}", unknown.Count == 0);
            }
            var both = Fun.AllConVars.Where(cv => committed.Any(e => e.ConVar.Equals(cv, StringComparison.OrdinalIgnoreCase))).ToList();
            Check($"A fun setting no map may set itself is still a player's to choose ({string.Join(", ", both)}): the block spares what a player chose",
                BlockList.Due(committed.Where(e => both.Contains(e.ConVar)), _ => "123", _ => "0", _ => true).Count == 0);
        }
        else Console.WriteLine($"skip the blocked.txt checks: {BlockedFile} not found");

        // ---- the rules (the owner's 5a): every number in the game's words is the code's ----
        var gameWords = new List<(string Where, string Text)>();
        foreach (var r in Hud.How) gameWords.AddRange(r.Lines.Prepend(r.Title).Select(l => ($"How it works, {r.Title}", l)));
        foreach (var g in Hud.Help) gameWords.AddRange(g.Rows.Select(r => ($"Help, {g.Name}", $"{r.Command} {r.Words} {r.Also}")));
        foreach (var r in Hud.Rules) gameWords.AddRange(r.Lines.Prepend(r.Title).Select(l => ($"Rules, {r.Title}", l)));
        gameWords.AddRange(Hud.Steps.Select(s => ("Start", $"{s.Title} {s.Command} {s.Words}")));
        gameWords.AddRange(NewMapsModes.Select(m => ("Game, new maps", HudMenu.NewMapsLine(m))));
        gameWords.AddRange(Hud.HelpMore.Select(m => ($"Help ({Hud.Content}), {Hud.Help[m.Group].Name}", $"{m.Row.Command} {m.Row.Words} {m.Row.Also}")));
        gameWords.Add(("Fun page", HudMenu.FunNoteText));
        var stray = gameWords.SelectMany(w => StrayNumbers(w.Text).Select(n => $"{w.Where}: '{n}'")).ToList();
        Check($"Every number in How it works, Help, Rules, the start page and the Game page's lines is one of the code's constants ({gameWords.Count} lines){(stray.Count > 0 ? " - not: " + string.Join(" | ", stray) : "")}",
            stray.Count == 0);
        Check("...and the checker itself finds a number that is not (a 400 MB rule, a 2% chance)", StrayNumbers("Maps of 400 MB and up").Count == 1 && StrayNumbers("1 random pick in 50").Count == 1
            && StrayNumbers($"1 random pick in {(int)Math.Round(1 / TooBigChance)}").Count == 0);

        if (File.Exists("../docs/reference.md"))
            Check("The reference states the rule: a rule change updates How it works, Help and Rules together, and --selftest holds them to the code",
                File.ReadAllText("../docs/reference.md").Contains("### Keeping the rules in sync"));
    }
}
