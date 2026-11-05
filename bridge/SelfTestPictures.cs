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
            b.Click("gg_fm_dm", wholeCount) && b.Mode == Modes.DeathmatchId && b.Page == 0 && Order() == "dm one,arena,dm and wm,de_inferno");
        Check("Mode filter: Wingman's", b.Click("gg_fm_wm", wholeCount) && Order() == "wm one,dm and wm,de_inferno");
        Check("Mode filter: Arms Race's", b.Click("gg_fm_ar", wholeCount) && Order() == "ar one,arena,ar_baggage");
        b.Query = "arena";
        var search = new HudSearch();
        Check("Mode filter: a search looks in the mode shown, and in every mode with All on",
            b.Matches(back, 1, search).Single().Name == "arena" && b.Click("gg_fm_wm", wholeCount) | true && b.Matches(back, 1, search).Count == 0 && b.Click("gg_fm_all", wholeCount) && b.Matches(back, 1, search).Single().Name == "arena");
        b.Query = "";
        var same = b.Matches(back, 1);
        Check("Mode filter: the list is worked out once for a state of the browser, and again when any of it moves",
            ReferenceEquals(same, b.Matches(back, 1)) && !ReferenceEquals(same, b.Matches(back, 2)) && b.Click("gg_fm_dm", wholeCount) && b.Matches(back, 1).Count != same.Count);
        b.Find("3000000003");
        Check("A pasted link searches every mode", b.Mode == "" && b.Matches(back, 1).Single().Name == "dm one");
        b = new HudBrowser();
        var classes = HudMenu.Maps(back, new HudState(), b, 1, DateTime.UtcNow).Classes;
        Check("Mode filter: its chips are lit by classes on gg_screen, which every content has; nothing is sent to the chips",
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
        Check("A card says its list: Wingman's and Deathmatch's numbers are their own", cards.Texts.Single(t => t.Id == "gg_m1_rank").Text == "Wingman #1" && cards.Texts.Single(t => t.Id == "gg_m3_rank").Text == "Official");
        var numbered = new HudCatalog.Entry(HudThumbList.Keys[0], "1", 1, long.Parse(HudThumbList.Keys[0]), "has one", 1, false, -1, 0, 0, 0);
        var plain2 = new HudCatalog.Entry("4000000000", "4000000000", 2, 4000000000, "uploaded later", 1, false, -1, 0, 0, 0);
        var off = new HudCatalog.Entry("ar_shoots", "ar_shoots", 0, 0, "ar_shoots", 0, true, -1, 0, 0, 0);
        var pics = new HudCatalog { Version = 1, Maps = [numbered, plain2, off] };
        var picClasses = HudMenu.Maps(pics, new HudState(), new HudBrowser(), 1, DateTime.UtcNow, null, "4000000000").Classes;
        bool On(string id, string cls) => picClasses.Any(c => c.Id == id && c.Class == cls && c.On);
        Check("The picture button: only a Workshop map with no picture in the addon has it, and it says Hide while that map's picture is up",
            !On("gg_m1", "nopic") && On("gg_m2", "nopic") && !On("gg_m3", "nopic") && On("gg_m2", "picopen") && !On("gg_m1", "picopen"));
        Check("Every official map of every mode has a picture number", Modes.All.SelectMany(m => m.Official).All(HudThumbs.Has));
        Check("Picture buttons: one per card, read back to their card, and nothing else reads as one",
            Enumerable.Range(1, HudMenu.Cards).All(k => HudMenu.PicCard(HudMenu.PicButton(k)) == k) && HudMenu.PicCard($"gg_m{HudMenu.Cards + 1}_pic") == 0 && HudMenu.PicCard("gg_m1_play") == 0 && HudMenu.PicCard("gg_mx_pic") == 0);
        Check("The picture buttons and the mode chips get nothing from the server: their state is classes on the cards and on gg_screen",
            !classes.Any(c => Enumerable.Range(1, HudMenu.Cards).Any(k => c.Id == HudMenu.PicButton(k))) && !classes.Any(c => HudBrowser.ModeButtons.Any(m => m.Id == c.Id)));
        const string preview = "https://images.steamusercontent.com/ugc/9254111869742482466/C3329D6202447C9B9F78B46CEEC5C7756DC424C9/";
        var url = HudPreview.Url(preview);
        Check("The picture's address: Steam's own image CDN scaled by Steam, at the address the Web API gave, and nothing else",
            url == preview + HudPreview.Scaled && HudPreview.Url(preview + "?imw=1") == url && HudPreview.Url(null) == null && HudPreview.Url("") == null && HudPreview.Url("not a url") == null
            && HudPreview.Url("http://images.steamusercontent.com/ugc/1/A/") == null && HudPreview.Url("https://example.com/ugc/1/A/") == null && HudPreview.Url("https://images.steamusercontent.com/ugc/1/'><b>x/") == null
            && HudPreview.Url("https://images.steamusercontent.com.example.com/ugc/1/A/") == null);
        Check("The message is the picture and nothing after it, well under the 1,450 bytes a message may have",
            HudPreview.Html(url!) == $"<img src='{url}'>" && HudPreview.Html(url!).Length < 300);
        Check("The short window keeps the rows of the card that asked: cards 1-4, 5-8, or the ballot's 9 and 10",
            HudPreview.RowClass(1) == "pic1" && HudPreview.RowClass(4) == "pic1" && HudPreview.RowClass(5) == "pic2" && HudPreview.RowClass(8) == "pic2" && HudPreview.RowClass(9) == "pic3" && HudPreview.RowClass(10) == "pic3");
        var item = WorkshopItem.Parse("""{"response":{"publishedfiledetails":[{"publishedfileid":"5","result":1,"visibility":0,"consumer_app_id":730,"title":"t","file_size":"9","preview_url":"PREVIEW"}]}}""".Replace("PREVIEW", preview))[5];
        Check("The Web API's answer carries the item's preview", item.Preview == preview && WorkshopItem.Parse("""{"response":{"publishedfiledetails":[{"publishedfileid":"5","result":9}]}}""")[5].Preview == "");

        // ---- the layout and the stylesheet ----
        const string layoutFile = "../hud/layout/gungame_hud_" + Hud.Release + ".xml", styleFile = "../hud/styles/gungame_hud_" + Hud.Release + ".css";
        if (File.Exists(layoutFile) && File.Exists(styleFile))
        {
            var xml = Regex.Replace(File.ReadAllText(layoutFile), "<!--.*?-->", "", RegexOptions.Singleline);
            var css = File.ReadAllText(styleFile);
            Check("Layout: each card has its picture button, with its two fixed labels and no id in them, last on its card - above the picture and a ballot card's own button",
                Enumerable.Range(1, HudMenu.Cards).All(k => Regex.IsMatch(xml, $@"<Button id=""{HudMenu.PicButton(k)}"" class=""gg-mpic""><Label class=""gg-mpic-text gg-mpic-show"" text=""Show picture"" /><Label class=""gg-mpic-text gg-mpic-hide"" text=""Hide picture"" /></Button>")
                    && xml.IndexOf($"id=\"{HudMenu.Card(k)}_thumb\"", StringComparison.Ordinal) < xml.IndexOf($"id=\"{HudMenu.Card(k)}_play\"", StringComparison.Ordinal)
                    && xml.IndexOf($"id=\"{HudMenu.Card(k)}_play\"", StringComparison.Ordinal) < xml.IndexOf($"id=\"{HudMenu.PicButton(k)}\"", StringComparison.Ordinal)));
            Check("Layout: the mode chips, each lit by its class on gg_screen",
                HudBrowser.ModeButtons.All(m => xml.Contains($"<Button id=\"{m.Id}\" class=\"gg-chipb gg-chipm-{(m.Mode == "" ? "all" : m.Mode)}\">") && css.Contains($".gg-screen.{HudBrowser.ModeClass(m.Mode)} .gg-chipm-{(m.Mode == "" ? "all" : m.Mode)}")));
            Check("The button shows only on a card with nopic, and the other row goes while a picture is up",
                Regex.IsMatch(css, @"\.gg-mpic\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"\.gg-mcard\.nopic \.gg-mpic\s*\{\s*visibility:\s*visible;")
                && Regex.IsMatch(css, @"\.gg-screen\.pic1 \.gg-grid-row2\s*\{\s*visibility:\s*collapse;") && Regex.IsMatch(css, @"\.gg-screen\.pic2 \.gg-grid-row1\s*\{\s*visibility:\s*collapse;") && xml.Contains("gg-grid-row gg-grid-row1") && xml.Contains("gg-grid-row gg-grid-row2"));
            var shortWin = Regex.Match(css, @"\.gg-screen\.pic1 \.gg-win\s*\{\s*height:\s*(\d+)px;\s*vertical-align:\s*top;\s*margin-top:\s*(\d+)px;");
            var shortEnds = shortWin.Success ? int.Parse(shortWin.Groups[1].Value) + int.Parse(shortWin.Groups[2].Value) : int.MaxValue;
            const int panelTop = 750; // the game's message panel at 1080p, measured in a real client on 2026-11-02 (x 810-1110, y 750-908)
            Check($"The short window ends above the game's message panel: y {shortEnds} of {panelTop}, the same for either row",
                shortEnds <= panelTop && Regex.IsMatch(css, @"\.gg-screen\.pic2 \.gg-win\s*\{\s*height:\s*" + shortWin.Groups[1].Value + @"px;\s*vertical-align:\s*top;\s*margin-top:\s*" + shortWin.Groups[2].Value + "px;"));
        }
        else Console.WriteLine($"skip the picture layout checks: {layoutFile} not found");
    }

    /// <summary>The bridge's own half, on a server with mode lists: a Play in a mode, a name only another mode has, and the player limit of a mode.</summary>
    static void PicturesBridgeSelfTest(Action<string, bool> Check, (string Name, string Id) player)
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
        var catalog = BuildCatalog();
        Check("The catalog: a map in two lists is one entry with both places, a map of another mode has its own, an official map of other modes too",
            catalog.Find(Key(ar)) is { RankDm: 1, RankAr: > 0, RankWm: -1 } && catalog.Find("3070000099") is { RankAr: -1, RankDm: 2, Arg: "3070000099" } && catalog.Find("de_dust2") is { RankAr: -1, RankDm: 0, Official: true, Arg: "de_dust2" }
            && catalog.Maps.Select(m => m.Key).Distinct().Count() == catalog.Maps.Count);
        ModeMaps.Remove(inDm.Id);
    }
}
