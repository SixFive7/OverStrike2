// --selftest, the mode filter's and the pictures' part: every mode's maps in the menu with a mode filter, the pictures shipped ahead, and the picture on request
// for a map that has none. Shared code only (HudBrowser.cs), plus the bridge's own pick of a map in a mode.
using System.Text.RegularExpressions;

static partial class Program
{
    static void PicturesSelfTest(Action<string, bool> Check)
    {
        // ---- the catalog carries each map's place in each mode ----
        HudCatalog.Entry E(string key, string name, int rank, long id, bool official = false, int ar = -1, int dm = -1, int wm = -1) =>
            new(key, official ? name : (ar > 0 ? ar.ToString() : id.ToString()), rank, id, name, 40, official, -1, 0, 0, 0, RankAr: ar, RankDm: dm, RankWm: wm);
        var both = E("3000000001", "arena", 7, 3000000001, ar: 7, dm: 2);
        var cat = new HudCatalog
        {
            Version = 1,
            Maps =
            [
                E("3000000002", "ar one", 1, 3000000002, ar: 1),
                both,
                E("ar_baggage", "ar_baggage", 0, 0, official: true, ar: 0),
                E("3000000003", "dm one", 1, 3000000003, dm: 1),
                E("3000000004", "wm one", 1, 3000000004, wm: 1),
                E("3000000005", "dm and wm", 3, 3000000005, dm: 3, wm: 2),
                E("de_inferno", "de_inferno", 0, 0, official: true, dm: 0, wm: 0),
            ],
        };
        var back = HudCatalog.Decode(cat.Encode())!;
        Check("Catalog: each map's place in each mode survives the codec, and a line from an older bridge reads as Arms Race's list",
            back.Find("3000000001") is { RankAr: 7, RankDm: 2, RankWm: -1 } && back.Find("de_inferno") is { RankAr: -1, RankDm: 0, RankWm: 0, Official: true }
            && back.Encode() == cat.Encode()
            && HudCatalog.Decode(HudCatalog.Magic + "\nm\tk\t5\t5\t123\tn\t1\t0\t-1\t0\t0\t0\n")!.Maps[0] is { RankAr: 5, RankDm: -1, RankWm: -1, FirstMode: Modes.ArmsRaceId });
        Check("An entry made without its places is Arms Race's, in the number it has", new HudCatalog.Entry("k", "9", 9, 1, "n", 1, false, -1, 0, 0, 0) is { FirstMode: Modes.ArmsRaceId } plain && plain.RankIn(Modes.ArmsRaceId) == 9 && plain.RankIn(Modes.DeathmatchId) == -1);

        // ---- the mode filter ----
        var b = new HudBrowser();
        string Order() => string.Join(",", b.Matches(back, 1).Select(m => m.Name));
        Check("Mode filter: all by default, Arms Race's maps first, then each other mode's own in their order, then the officials only they have",
            Order() == "ar one,arena,ar_baggage,dm one,dm and wm,wm one,de_inferno");
        var wholeCount = b.Matches(back, 1).Count;
        Check("Mode filter: the Deathmatch chip lists Deathmatch's maps in its own order, a map in two lists among them, its officials last, from the first page",
            b.Click("os_fm_dm", wholeCount) && b.Mode == Modes.DeathmatchId && b.Page == 0 && Order() == "dm one,arena,dm and wm,de_inferno");
        Check("Mode filter: Wingman's", b.Click("os_fm_wm", wholeCount) && Order() == "wm one,dm and wm,de_inferno");
        Check("Mode filter: Arms Race's", b.Click("os_fm_ar", wholeCount) && Order() == "ar one,arena,ar_baggage");
        b.Query = "arena";
        var search = new HudSearch();
        Check("Mode filter: a search looks in the mode shown, and in every mode with All on",
            b.Matches(back, 1, search).Single().Name == "arena" && b.Click("os_fm_wm", wholeCount) | true && b.Matches(back, 1, search).Count == 0 && b.Click("os_fm_all", wholeCount) && b.Matches(back, 1, search).Single().Name == "arena");
        b.Query = "";
        var same = b.Matches(back, 1);
        Check("Mode filter: the list is worked out once for a state of the browser, and again when any of it moves",
            ReferenceEquals(same, b.Matches(back, 1)) && !ReferenceEquals(same, b.Matches(back, 2)) && b.Click("os_fm_dm", wholeCount) && b.Matches(back, 1).Count != same.Count);
        b.Find("3000000003");
        Check("A pasted link searches every mode", b.Mode == "" && b.Matches(back, 1).Single().Name == "dm one");
        b = new HudBrowser();
        var classes = HudMenu.Maps(back, new HudState(), b, 1, DateTime.UtcNow).Classes;
        Check("Mode filter: its chips are lit by classes on os_screen, which every content has; nothing is sent to the chips",
            HudBrowser.ModeButtons.All(m => classes.Any(c => c.Id == HudView.Screen && c.Class == HudBrowser.ModeClass(m.Mode) && c.On == (m.Mode == ""))) && !classes.Any(c => HudBrowser.ModeButtons.Any(m => m.Id == c.Id)));

        // ---- what a card's Play asks for ----
        var dmOnly = back.Find("3000000003")!;
        var official2 = back.Find("de_inferno")!;
        Check("Play: a map in one list by its own argument; a map in two by the mode the filter shows it in, else the one the server is in, else its first",
            HudMenu.PlayArg(dmOnly, "", Modes.ArmsRaceId) == "3000000003" && HudMenu.PlayArg(both, "", Modes.ArmsRaceId) == "7" && HudMenu.PlayArg(both, Modes.DeathmatchId, Modes.ArmsRaceId) == "7 deathmatch"
            && HudMenu.PlayArg(both, "", Modes.DeathmatchId) == "7 deathmatch" && HudMenu.PlayArg(both, Modes.ArmsRaceId, Modes.DeathmatchId) == "7"
            && HudMenu.PlayArg(official2, "", Modes.ArmsRaceId) == "de_inferno" && HudMenu.PlayArg(official2, Modes.WingmanId, Modes.ArmsRaceId) == "de_inferno wingman");
        Check("A click may name a map in a mode, an official map of any mode, and nothing free-form",
            Hud.IsAction("!map 7 deathmatch") && Hud.IsAction("!map de_inferno wingman") && Hud.IsAction("!map de_dust2") && Hud.IsAction("!map cs_office") && Hud.IsAction("!up de_ancient_night")
            && !Hud.IsAction("!map 7 quake") && !Hud.IsAction("!map 7 deathmatch x") && !Hud.IsAction("!map de_x;quit") && !Hud.IsAction("!map deathmatch") && !Hud.IsAction("!up 7 deathmatch"));
        Check("A !map argument splits into its map and its mode", Hud.SplitMode("7 deathmatch") == ("7", Modes.DeathmatchId) && Hud.SplitMode("7") == ("7", "") && Hud.SplitMode("de_inferno wingman") == ("de_inferno", Modes.WingmanId) && Hud.SplitMode("7 quake") == ("7 quake", ""));

        // ---- the pictures ----
        var cards = HudMenu.Maps(back, new HudState(), new HudBrowser { Mode = Modes.WingmanId }, 1, DateTime.UtcNow);
        Check("A card says its list: Wingman's and Deathmatch's numbers are their own", cards.Texts.Single(t => t.Id == "os_m1_rank").Text == "Wingman #1" && cards.Texts.Single(t => t.Id == "os_m3_rank").Text == "Official");
        var numbered = new HudCatalog.Entry(HudThumbList.Keys[0], "1", 1, long.Parse(HudThumbList.Keys[0]), "has one", 1, false, -1, 0, 0, 0);
        var plain2 = new HudCatalog.Entry("4000000000", "4000000000", 2, 4000000000, "uploaded later", 1, false, -1, 0, 0, 0);
        var off = new HudCatalog.Entry("ar_shoots", "ar_shoots", 0, 0, "ar_shoots", 0, true, -1, 0, 0, 0);
        var pics = new HudCatalog { Version = 1, Maps = [numbered, plain2, off] };
        var thumbsOf = HudMenu.Thumbs(pics, new HudBrowser(), 1);
        Check("A map with a picture in the addon has its two classes on its card; a map uploaded later has none, and no button for one: pictures are not clickable",
            thumbsOf[0].Classes.Length == 2 && thumbsOf[1].Classes.Length == 0 && !HudMenu.Maps(pics, new HudState(), new HudBrowser(), 1, DateTime.UtcNow).Classes.Any(c => c.Class is "nopic" or "picopen"));
        Check("Every official map of every mode has a picture number", Modes.All.SelectMany(m => m.Official).All(HudThumbs.Has));
        Check("The mode chips get nothing from the server: their state is classes on os_screen", !classes.Any(c => HudBrowser.ModeButtons.Any(m => m.Id == c.Id)));

        // ---- the layout and the stylesheet ----
        const string layoutFile = "../hud/layout/gungame_hud_" + Hud.ReleaseFile + ".xml", styleFile = "../hud/styles/gungame_hud_" + Hud.ReleaseFile + ".css";
        if (File.Exists(layoutFile) && File.Exists(styleFile))
        {
            var xml = Regex.Replace(File.ReadAllText(layoutFile), "<!--.*?-->", "", RegexOptions.Singleline);
            var css = File.ReadAllText(styleFile);
            Check("Layout: the mode chips, each lit by its class on os_screen",
                HudBrowser.ModeButtons.All(m => xml.Contains($"<Button id=\"{m.Id}\" class=\"os-chipb os-chipm-{(m.Mode == "" ? "all" : m.Mode)}\">") && css.Contains($".os-screen.{HudBrowser.ModeClass(m.Mode)} .os-chipm-{(m.Mode == "" ? "all" : m.Mode)}")));
            Check("The standalone picture is gone from the layout and the stylesheet: no button on a card, no rule that shrinks the window for one", !xml.Contains("os-mpic") && !css.Contains("os-mpic") && !css.Contains(".pic1") && !css.Contains("picopen"));
        }
        else Console.WriteLine($"skip the picture layout checks: {layoutFile} not found");
    }

    /// <summary>The bridge's own half, on a server with mode lists: a Play in a mode, a name only another mode has, and the player limit of a mode.</summary>
    static void PicturesBridgeSelfTest(Action<string, bool> Check)
    {
        var inDm = Modes.Find(Modes.DeathmatchId)!;
        var ar = Maps[0];
        ModeMaps[inDm.Id] = [new(1, ar.Id, ar.Name, ar.SizeMb, false, inDm.Id), new(2, 3070000099, "only_in_dm", 12, false, inDm.Id)];
        var listed = ListedIn(inDm, ar);
        Check("A map in two lists: its entry in the mode asked for", listed != null && listed.Mode == inDm.Id && ListedIn(Modes.Wingman, ar) == null && ListedIn(Modes.ArmsRace, listed) is { Mode: "" });
        Check("By id: a map only another mode lists is found there, in that mode", Find("3070000099") is [{ Id: 3070000099, Mode: Modes.DeathmatchId }]);
        Check("By name: an official map of another mode by its whole name", Find("de_dust2") is [{ Official: true, Mode: Modes.DeathmatchId }]);
        Check("By id: an Arms Race map's id stays Arms Race's", Find(ar.Id.ToString()).All(m => m.Mode == ""));
        Check("A crash notice names another mode's map by its name, not its Workshop id (B13)", NameOfKey("3070000099") == "only_in_dm" && NameOfKey("de_dust2") == "de_dust2 (official)");
        var updatedBefore = UpdatedOf("3070000099");
        ModeMaps[inDm.Id] = [.. ModeMaps[inDm.Id].Select(m => m.Id == 3070000099 ? m with { Name = "renamed_in_dm", Updated = 1_790_000_000 } : m)];
        Check("The lookup by key follows a list that moves: the new name and the new update time, at once; the newest update time of any list counts",
            updatedBefore == 0 && NameOfKey("3070000099") == "renamed_in_dm" && MapOf("3070000099").Name == "renamed_in_dm" && UpdatedOf("3070000099") == 1_790_000_000
            && NameOf("3070000099") == "renamed_in_dm" && NameOf("3070000098") == "workshop 3070000098" && MapOf("3070000098") is { Rank: 0, Id: 3070000098, Name: "workshop 3070000098" });
        var catalog = BuildCatalog();
        Check("The catalog: a map in two lists is one entry with both places, a map of another mode has its own, an official map of other modes too",
            catalog.Find(Key(ar)) is { RankDm: 1, RankAr: > 0, RankWm: -1 } && catalog.Find("3070000099") is { RankAr: -1, RankDm: 2, Arg: "3070000099" } && catalog.Find("de_dust2") is { RankAr: -1, RankDm: 0, Official: true, Arg: "de_dust2" }
            && catalog.Maps.Select(m => m.Key).Distinct().Count() == catalog.Maps.Count);
        ModeMaps.Remove(inDm.Id);
    }
}
